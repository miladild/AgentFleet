using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace AgentFleet;

internal sealed record WorkerWorkspaceBaseline(string Machine, Dictionary<string, string> Files);

internal sealed record WorkerWorkspaceRecoveryAnalysis(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> FilesToApply,
    IReadOnlyList<string> Conflicts)
{
    public bool Safe => Conflicts.Count == 0;
}

internal interface IWorkerWorkspaceSession : IDisposable
{
    string Machine { get; }
    string Platform { get; }
    TimeSpan CommandTimeout { get; }
    Task<bool> AnswersAsync(CancellationToken cancellationToken);
    string SnapshotId();
    Dictionary<string, string> BaselineHashes();
    IDisposable Enter();
    Task SyncToHubAsync(CancellationToken cancellationToken);
    Task RefreshFromHubAsync(CancellationToken cancellationToken);
    Task ReconnectAsync(CancellationToken cancellationToken);

    // A session that does not hold frozen test files has none to put back.
    Task<IReadOnlyList<string>> RestoreFrozenTestsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class WorkerCommandTimeoutException(string message, Exception innerException, string? partialOutput = null)
    : TimeoutException(message, innerException)
{
    // What the command printed before it was killed (the newest part, capped), or null when the transport did not keep it.
    public string? PartialOutput { get; } = partialOutput;
}

internal sealed class WorkerPathRefusedException(string message) : Exception(message);

internal interface IWorkerWorkspaceManager
{
    Task<IWorkerWorkspaceSession> StageAsync(PlanRecord plan, PlanStep step, string machine, CancellationToken cancellationToken);
    Task<WorkerWorkspaceResume> ResumeInterruptedAsync(PlanRecord plan, PlanStep step, WorkerWorkspaceBaseline baseline, CancellationToken cancellationToken);
    Task<FailFirstCheckResult> RunScratchCheckAsync(PlanRecord plan, PlanStep step, string machine, IWorkerWorkspaceSession workspace,
        IReadOnlyDictionary<string, byte[]?> overlay, string command, CancellationToken cancellationToken) =>
        Task.FromException<FailFirstCheckResult>(new NotSupportedException("Scratch checks are not supported by this workspace manager."));
}

internal sealed record FailFirstCheckResult(int ExitCode, string Output, double DurationSeconds, bool TimedOut = false);

internal sealed record WorkerWorkspaceResume(IWorkerWorkspaceSession? Session, WorkerWorkspaceRecoveryAnalysis Analysis);

internal interface IWorkerWorkspaceFileAccess
{
    bool Exists(string path);
    Task<string> ReadTextAsync(string path, CancellationToken cancellationToken);
    void CreateDirectoryTree(string path);
    Task WriteTextAsync(string path, string content, CancellationToken cancellationToken);
}

/// <summary>Creates isolated per-plan copies on a selected, explicitly configured worker.</summary>
internal sealed class WorkerWorkspaceManager(FleetOptions fleet, ILogger logger, TimeSpan commandTimeout, string? syncRecordDirectory = null) : IWorkerWorkspaceManager
{
    // What each worker's copy of a plan held when it was last synced to the hub (file -> hash). A session lasts one round,
    // so this is how the next staging knows that a worker file nobody touched since that sync holds no unsynced work, even
    // when the hub has moved on (the runner rolled a round back, or the user edited the project).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _lastSynced =
        new(StringComparer.OrdinalIgnoreCase);

    // The same record, kept in a small file per plan and machine in the plans folder, so a backend restart (a deploy) does not
    // leave a plan unable to stage. Null means no file: the record lives only in memory, as it always did.
    private readonly string? _syncRecordDirectory = syncRecordDirectory;
    private readonly object _syncRecordLock = new();

    // The test files every accepted tests step of the plan froze, in the order the steps were accepted.
    internal static IReadOnlyList<PlanFrozenTestFile> FrozenFilesOf(PlanRecord plan) =>
        (plan.FrozenTests ?? []).SelectMany(set => set.Files).ToArray();

    // A coding worker that can hold a step's workspace: not the hub, not a vision machine, and with a workspace configured.
    internal static bool IsWorkspaceWorker(FleetNodeDefinition node, string tier) =>
        !node.Vision && !string.Equals(node.Name, "hub", StringComparison.OrdinalIgnoreCase) &&
        node.Workspace is not null && string.Equals(node.Tier, tier, StringComparison.OrdinalIgnoreCase);

    // A tier with no such worker borrows the nearest tier's worker for its files and checks; its own model still does the work.
    internal static bool HasOwnWorkspaceWorker(IEnumerable<FleetNodeDefinition> nodes, string tier) =>
        nodes.Any(node => IsWorkspaceWorker(node, tier));

    public async Task<IWorkerWorkspaceSession> StageAsync(PlanRecord plan, PlanStep step, string machine, CancellationToken cancellationToken)
    {
        FleetNodeDefinition node = fleet.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, machine, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Worker workspace unavailable: machine '{machine}' is not configured.");
        if (string.Equals(node.Name, "hub", StringComparison.OrdinalIgnoreCase) || node.Vision)
            throw new InvalidOperationException($"Worker workspace unavailable: '{machine}' is not a coding worker; plan work must run on a configured worker machine.");
        // A machine of another tier serves only as the borrowed worker: the step's tier has no workspace worker of its own.
        if (!string.Equals(node.Tier, step.Tier, StringComparison.OrdinalIgnoreCase) && HasOwnWorkspaceWorker(fleet.Nodes, step.Tier))
            throw new InvalidOperationException($"Worker workspace unavailable: '{machine}' is configured for the {node.Tier ?? "unspecified"} tier, not this step's approved {step.Tier} tier.");
        FleetWorkerWorkspaceConfig config = node.Workspace
            ?? throw new InvalidOperationException($"Worker workspace unavailable: machine '{machine}' has no worker workspace configured. Configure a dedicated low-privilege SSH account and pinned host key in Config.");
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !Directory.Exists(plan.WorkingDirectory))
        {
            throw new InvalidOperationException("Worker workspace unavailable: the plan's project folder does not exist on the hub.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var session = new WorkerWorkspaceSession(machine, config, Path.GetFullPath(plan.WorkingDirectory), logger, commandTimeout,
            onSynced: hashes => RecordSynced(plan.Id, machine, hashes), frozenFiles: FrozenFilesOf(plan), planStartedUtc: plan.ApprovedUtc ?? plan.CreatedUtc);
        try
        {
            session.Connect();
            session.Stage(plan.Id, step, LastSynced(plan.Id, machine), cancellationToken);
            logger.LogInformation("Staged plan {PlanId} on worker {Machine} at {Workspace}.", plan.Id, machine, session.RemoteRoot);
            await Task.CompletedTask;
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // Remembers what a worker's copy held after a sync, in memory and, when a folder is configured, on disk. A write failure is
    // logged and ignored: the in-memory record still works for this run.
    internal void RecordSynced(string planId, string machine, IReadOnlyDictionary<string, string> hashes)
    {
        _lastSynced[$"{planId}|{machine}"] = hashes;
        if (_syncRecordDirectory is null || !FleetPlanStore.IsValidId(planId)) return;

        string path = Path.Combine(_syncRecordDirectory, SyncRecordFileName(planId, machine));
        string temporary = path + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, string>(hashes, StringComparer.OrdinalIgnoreCase));
            lock (_syncRecordLock)
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(exception, "Could not save the last sync record of plan {PlanId} on worker {Machine}; it is kept in memory only.", planId, machine);
        }
    }

    // The record of a worker's last sync: in memory if this run made it, otherwise read back from its file after a restart.
    internal IReadOnlyDictionary<string, string>? LastSynced(string planId, string machine)
    {
        string key = $"{planId}|{machine}";
        if (_lastSynced.TryGetValue(key, out IReadOnlyDictionary<string, string>? known)) return known;
        IReadOnlyDictionary<string, string>? stored = ReadSyncRecord(planId, machine);
        return stored is null ? null : _lastSynced.GetOrAdd(key, stored);
    }

    // A missing, empty or damaged file is no record at all; reading never throws.
    private IReadOnlyDictionary<string, string>? ReadSyncRecord(string planId, string machine)
    {
        if (_syncRecordDirectory is null || !FleetPlanStore.IsValidId(planId)) return null;

        string path = Path.Combine(_syncRecordDirectory, SyncRecordFileName(planId, machine));
        try
        {
            if (!File.Exists(path)) return null;
            Dictionary<string, string?>? stored = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path));
            return stored?
                .Where(pair => !string.IsNullOrEmpty(pair.Key) && pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(exception, "The last sync record of plan {PlanId} on worker {Machine} is unreadable; treating it as no record.", planId, machine);
            return null;
        }
    }

    // One file per plan and machine. A machine name that is not a plain file name is made one, and a short hash of the original
    // name keeps two names that would otherwise look alike apart. The extension is neither .json nor .lease, so the plan store never lists it.
    internal static string SyncRecordFileName(string planId, string machine)
    {
        string safe = Regex.Replace(machine, "[^A-Za-z0-9_-]", "_");
        if (!string.Equals(safe, machine, StringComparison.Ordinal))
            safe += "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine)))[..8].ToLowerInvariant();
        return $"{planId}.{safe}.synced";
    }

    public async Task<WorkerWorkspaceResume> ResumeInterruptedAsync(
        PlanRecord plan,
        PlanStep step,
        WorkerWorkspaceBaseline baseline,
        CancellationToken cancellationToken)
    {
        if (!fleet.TryGetNode(baseline.Machine, out FleetNodeDefinition? node) || node.Workspace is null || node.Vision ||
            string.Equals(node.Name, "hub", StringComparison.OrdinalIgnoreCase) ||
            (!string.Equals(node.Tier, step.Tier, StringComparison.OrdinalIgnoreCase) && HasOwnWorkspaceWorker(fleet.Nodes, step.Tier)))
            throw new InvalidOperationException($"Worker workspace unavailable: {baseline.Machine} is not configured for workspace recovery.");
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !Directory.Exists(plan.WorkingDirectory))
            throw new InvalidOperationException("Worker workspace unavailable: the plan's project folder does not exist on the hub.");
        var session = new WorkerWorkspaceSession(baseline.Machine, node.Workspace,
            Path.GetFullPath(plan.WorkingDirectory!), logger, commandTimeout, frozenFiles: FrozenFilesOf(plan), planStartedUtc: plan.ApprovedUtc ?? plan.CreatedUtc);
        try
        {
            session.Connect();
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, string hash) in baseline.Files)
            {
                if (!hashes.TryAdd(path, hash))
                    throw new InvalidDataException("The saved worker baseline contains duplicate paths.");
            }
            WorkerWorkspaceRecoveryAnalysis analysis = await session.ReconcileInterruptedAsync(plan, step, hashes, cancellationToken);
            if (!analysis.Safe)
            {
                session.Dispose();
                return new WorkerWorkspaceResume(null, analysis);
            }
            return new WorkerWorkspaceResume(session, analysis);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<FailFirstCheckResult> RunScratchCheckAsync(PlanRecord plan, PlanStep step, string machine,
        IWorkerWorkspaceSession workspace, IReadOnlyDictionary<string, byte[]?> overlay, string command, CancellationToken cancellationToken) =>
        workspace is WorkerWorkspaceSession session
            ? session.RunScratchCheckAsync(plan, step, overlay, command, cancellationToken)
            : throw new InvalidOperationException("The worker session cannot run a scratch check.");
}

/// <summary>Worker scope is ambient only while a plan step's model and verifier are running.</summary>
internal static class WorkerWorkspaceContext
{
    private static readonly AsyncLocal<WorkerWorkspaceSession?> CurrentSession = new();
    public static WorkerWorkspaceSession? Current => CurrentSession.Value;

    public static IDisposable Push(WorkerWorkspaceSession session)
    {
        WorkerWorkspaceSession? previous = CurrentSession.Value;
        CurrentSession.Value = session;
        return new Restore(previous);
    }

    private sealed class Restore(WorkerWorkspaceSession? previous) : IDisposable
    {
        public void Dispose() => CurrentSession.Value = previous;
    }
}

/// <summary>
/// An SSH/SFTP session for one plan attempt. Paths exposed to tools are confined to its staged project; shell
/// commands run as the configured worker account, whose OS permissions must be limited to its own workspace.
/// </summary>
internal sealed class WorkerWorkspaceSession : IWorkerWorkspaceSession, IEditCheckFiles
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".next", "dist", "build", "out", ".venv", "venv",
        "__pycache__", ".idea", ".vs", "target", "coverage", "vendor", "TestResults", "playwright-report"
    };
    private static readonly HashSet<string> PrivateFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot-instructions.md", "AGENTS.md", "CLAUDE.md", "fleet.config.json",
        ".npmrc", ".pypirc", ".netrc", ".git-credentials", ".gitconfig", "credentials", "secrets",
        "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa", "authorized_keys", "known_hosts"
    };
    private static readonly Regex SecretEnvironmentFile = new(@"^\.env(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Said to the model when a file tool is asked to change a test file that an accepted tests step fixed for the plan.
    internal const string FrozenTestRefusal = FrozenTestFiles.RefusalMessage;

    // Windows reserves these names, with or without an extension, trailing dots and spaces ignored.
    private static readonly Regex WindowsDeviceName = new(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> PrivateDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".aws", ".azure", ".gnupg", ".kube", "secrets", "credentials"
    };
    private static readonly HashSet<string> PrivateExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".p12", ".pfx", ".kdbx"
    };
    private readonly FleetWorkerWorkspaceConfig _config;
    private readonly string _hubRoot;
    private readonly ILogger _logger;
    private readonly TimeSpan _commandTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _stagedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stepFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _baselineHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<IReadOnlyDictionary<string, string>>? _onSynced;
    private readonly IWorkerWorkspaceFileAccess? _fileAccess;
    private readonly IReadOnlyList<PlanFrozenTestFile> _frozenFiles;
    private readonly DateTimeOffset? _planStartedUtc;
    private SftpClient? _sftp;
    private SshClient? _ssh;
    private bool _disposed;

    internal WorkerWorkspaceSession(string machine, FleetWorkerWorkspaceConfig config, string hubRoot, ILogger logger, TimeSpan commandTimeout,
        Action<IReadOnlyDictionary<string, string>>? onSynced = null, IWorkerWorkspaceFileAccess? fileAccess = null,
        IReadOnlyList<PlanFrozenTestFile>? frozenFiles = null, DateTimeOffset? planStartedUtc = null)
    {
        _planStartedUtc = planStartedUtc;
        _onSynced = onSynced;
        _fileAccess = fileAccess;
        _frozenFiles = frozenFiles ?? [];
        Machine = machine;
        _config = config;
        _hubRoot = hubRoot;
        _logger = logger;
        _commandTimeout = commandTimeout;
        RemoteRoot = JoinRemote(config.Root.TrimEnd('/'), "agentfleet", "plans", "workspace", SafePart(machine), SafePart("plan"));
    }

    public string Machine { get; }
    public string Platform => _config.Platform;
    public TimeSpan CommandTimeout => _commandTimeout;
    public string RemoteRoot { get; private set; }
    public string HubRoot => _hubRoot;

    public string SnapshotId()
    {
        string contents = string.Join('\n', _baselineHashes
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => $"{file.Key}\0{file.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)))[..16].ToLowerInvariant();
    }

    public Dictionary<string, string> BaselineHashes() => new(_baselineHashes, StringComparer.OrdinalIgnoreCase);

    internal void Connect()
    {
        if (string.IsNullOrWhiteSpace(_config.HostKey) || !_config.HostKey.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Worker workspace unavailable: {Machine} must have a pinned SHA256 SSH host key.");
        }

        string keyPath = Environment.ExpandEnvironmentVariables(_config.KeyPath.Trim());
        if (!Path.IsPathRooted(keyPath)) keyPath = Path.GetFullPath(keyPath);
        if (!File.Exists(keyPath)) throw new FileNotFoundException($"The SSH key configured for worker '{Machine}' was not found.", keyPath);

        _sftp = new SftpClient(_config.Host, _config.Port, _config.User, new PrivateKeyFile(keyPath));
        _ssh = new SshClient(_config.Host, _config.Port, _config.User, new PrivateKeyFile(keyPath));
        _sftp.OperationTimeout = TimeSpan.FromMinutes(2);
        _sftp.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _ssh.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        _ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        SandboxSsh.Guard(_sftp, _config.HostKey, null);
        SandboxSsh.Guard(_ssh, _config.HostKey, null);
        _sftp.Connect();
        _ssh.Connect();
        ValidateWorkerAccount();
    }

    public Task ReconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { _sftp?.Dispose(); } catch { }
        try { _ssh?.Dispose(); } catch { }
        _sftp = null;
        _ssh = null;
        Connect();
        return Task.CompletedTask;
    }

    private void ValidateWorkerAccount()
    {
        string probe = _config.Platform == "linux"
            ? "id -u; id -nG; if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then echo __FLEET_SUDO__; fi"
            : "whoami /groups /fo csv";
        using SshCommand command = _ssh!.CreateCommand(probe);
        command.CommandTimeout = TimeSpan.FromSeconds(15);
        string result = command.Execute();
        if (command.ExitStatus != 0)
            throw new InvalidOperationException($"Worker workspace unavailable: could not verify that {Machine}'s SSH account is non-admin.");

        if (_config.Platform == "linux")
        {
            string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool root = lines.FirstOrDefault() == "0";
            string groups = lines.Skip(1).FirstOrDefault() ?? string.Empty;
            bool elevatedGroup = groups.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(group =>
                group is "sudo" or "wheel" or "docker" or "lxd" or "disk");
            if (root || elevatedGroup || result.Contains("__FLEET_SUDO__", StringComparison.Ordinal))
                throw new InvalidOperationException($"Worker workspace unavailable: SSH account '{_config.User}' on {Machine} has administrator/root or passwordless sudo access. Use the dedicated limited account from the setup instructions.");
        }
        else if (result.Contains("S-1-5-32-544", StringComparison.OrdinalIgnoreCase) ||
                 result.Contains("BUILTIN\\Administrators", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Worker workspace unavailable: SSH account '{_config.User}' on {Machine} belongs to the Windows Administrators group. Use a dedicated standard account.");
        }
    }

    internal void Stage(string planId, PlanStep step, IReadOnlyDictionary<string, string>? lastSynced, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(planId, "^[a-f0-9]{32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Worker workspace unavailable: invalid plan id.");

        RemoteRoot = JoinRemote(_config.Root.TrimEnd('/'), "agentfleet", "plans", planId.ToLowerInvariant(), SafePart(Machine), "project");
        EnsureNoLinkComponents(RemoteRoot);
        CreateDirectoryTree(RemoteRoot);
        _stagedFiles.Clear();
        _stepFiles.Clear();
        _baselineHashes.Clear();
        foreach (string requested in step.Files)
        {
            string relative = LocalRelativePath(requested);
            if (!string.IsNullOrWhiteSpace(relative)) _stepFiles.Add(relative.TrimEnd('/'));
        }

        string[] sourceFiles = EnumerateStageFiles(_hubRoot)
            .Where(source => new FileInfo(source).Length <= 20 * 1024 * 1024)
            .ToArray();
        var currentFiles = sourceFiles.Select(source => Path.GetRelativePath(_hubRoot, source).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceByRelative = sourceFiles.ToDictionary(
            source => Path.GetRelativePath(_hubRoot, source).Replace('\\', '/'),
            StringComparer.OrdinalIgnoreCase);
        foreach (string remote in EnumerateRemoteFiles(RemoteRoot).ToArray())
        {
            string relative = RelativeRemote(RemoteRoot, remote);
            long size = _sftp!.GetAttributes(remote).Size;
            if (size > 20 * 1024 * 1024)
                throw new InvalidOperationException($"Worker workspace unavailable: existing worker file '{relative}' exceeds 20 MiB; it was preserved.");
            string? syncedHash = null;
            lastSynced?.TryGetValue(relative, out syncedHash);
            bool onHub = currentFiles.Contains(relative);
            if (!onHub && syncedHash is null)
                throw new InvalidOperationException($"Worker workspace unavailable: existing worker file '{relative}' is absent from the hub checkout. It may contain unsynced work, so it was preserved.");

            using var content = new MemoryStream();
            _sftp.DownloadFile(remote, content);
            string workerHash = HashBytes(content.ToArray());
            switch (JudgeExistingWorkerFile(workerHash, onHub ? HashFile(sourceByRelative[relative]) : null, syncedHash, IsFrozen(relative)))
            {
                case ExistingWorkerFile.Refuse when onHub:
                    throw new InvalidOperationException($"Worker workspace unavailable: existing worker file '{relative}' differs from the hub checkout. It may contain unsynced work, so the workspace was preserved.");
                case ExistingWorkerFile.Refuse:
                    throw new InvalidOperationException($"Worker workspace unavailable: existing worker file '{relative}' is absent from the hub checkout. It may contain unsynced work, so it was preserved.");
                case ExistingWorkerFile.Remove:
                    _sftp.DeleteFile(remote);
                    break;
            }
        }

        foreach (string source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(_hubRoot, source).Replace('\\', '/');
            string destination = JoinRemote(RemoteRoot, relative);
            EnsureNoLinkComponents(destination);
            CreateDirectoryTree(Parent(destination));
            UploadHubFile(source, destination);
            _stagedFiles.Add(relative);
            _baselineHashes[relative] = HashFile(source);
        }

        InitializeWorkspaceGit();
    }

    internal async Task<WorkerWorkspaceRecoveryAnalysis> ReconcileInterruptedAsync(
        PlanRecord plan,
        PlanStep step,
        IReadOnlyDictionary<string, string> baseline,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RemoteRoot = JoinRemote(_config.Root.TrimEnd('/'), "agentfleet", "plans", plan.Id.ToLowerInvariant(), SafePart(Machine), "project");
            EnsureNoLinkComponents(RemoteRoot);
            if (!_sftp!.Exists(RemoteRoot)) throw new IOException($"Worker workspace on {Machine} is missing; its staged baseline could not be checked.");

            _stepFiles.Clear();
            foreach (string requested in step.Files)
            {
                string relative = LocalRelativePath(requested);
                if (!string.IsNullOrWhiteSpace(relative)) _stepFiles.Add(relative);
            }

            var remoteBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var remoteHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sizeConflicts = new List<string>();
            var linkConflicts = new List<string>();
            foreach (string remote in EnumerateRemoteFilesForRecovery(RemoteRoot, linkConflicts))
            {
                string relative = RelativeRemote(RemoteRoot, remote);
                if (_sftp.GetAttributes(remote).Size > 20 * 1024 * 1024)
                {
                    sizeConflicts.Add(relative);
                    continue;
                }

                using var content = new MemoryStream();
                _sftp.DownloadFile(remote, content);
                byte[] bytes = content.ToArray();
                remoteBytes.Add(relative, bytes);
                remoteHashes.Add(relative, HashBytes(bytes));
            }

            if (linkConflicts.Count > 0)
                return new WorkerWorkspaceRecoveryAnalysis([], [], linkConflicts.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            if (sizeConflicts.Count > 0)
                return new WorkerWorkspaceRecoveryAnalysis([], [], sizeConflicts.Order(StringComparer.OrdinalIgnoreCase).ToArray());

            string[] hubFiles = EnumerateStageFiles(_hubRoot)
                .Where(file => new FileInfo(file).Length <= 20 * 1024 * 1024)
                .ToArray();
            var hubHashes = hubFiles.ToDictionary(
                file => Path.GetRelativePath(_hubRoot, file).Replace('\\', '/'),
                HashFile,
                StringComparer.OrdinalIgnoreCase);
            WorkerWorkspaceRecoveryAnalysis analysis = AnalyzeInterruptedFiles(
                plan, step, baseline, remoteHashes, hubHashes);
            if (!analysis.Safe) return analysis;

            foreach (string relative in analysis.FilesToApply)
            {
                string local = SafeHubPath(relative);
                if (PlanRunner.HasLinkedPathComponent(_hubRoot, local))
                    throw new UnauthorizedAccessException($"Worker recovery refused a linked hub path: {relative}.");
                if (!remoteBytes.TryGetValue(relative, out byte[]? bytes))
                {
                    if (File.Exists(local)) File.Delete(local);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                string temporary = local + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                    File.Move(temporary, local, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }

            // The comparison proved every worker-only change is either already in the hub or named by this step
            // and uncontested. Re-stage the hub snapshot so future tools and checks see one consistent copy.
            var currentHub = EnumerateStageFiles(_hubRoot)
                .Where(file => new FileInfo(file).Length <= 20 * 1024 * 1024)
                .ToDictionary(file => Path.GetRelativePath(_hubRoot, file).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            foreach (string remote in EnumerateRemoteFiles(RemoteRoot).ToArray())
            {
                string relative = RelativeRemote(RemoteRoot, remote);
                if (!currentHub.ContainsKey(relative)) _sftp.DeleteFile(remote);
            }
            foreach ((string relative, string source) in currentHub)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = JoinRemote(RemoteRoot, relative);
                EnsureNoLinkComponents(destination);
                CreateDirectoryTree(Parent(destination));
                UploadHubFile(source, destination);
            }

            _stagedFiles.Clear();
            _baselineHashes.Clear();
            foreach ((string relative, string source) in currentHub)
            {
                _stagedFiles.Add(relative);
                _baselineHashes[relative] = HashFile(source);
            }
            return analysis;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static WorkerWorkspaceRecoveryAnalysis AnalyzeInterruptedFiles(
        PlanRecord plan,
        PlanStep step,
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> remote,
        IReadOnlyDictionary<string, string> hub)
    {
        var changed = new List<string>();
        var apply = new List<string>();
        var conflicts = new List<string>();
        string root = Path.GetFullPath(plan.WorkingDirectory ?? string.Empty);
        string[] paths = baseline.Keys.Concat(remote.Keys).Concat(hub.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string relative in paths)
        {
            if (!IsSafeRelative(relative))
            {
                conflicts.Add(relative);
                continue;
            }

            bool remoteExists = remote.TryGetValue(relative, out string? remoteHash);
            bool baselineExists = baseline.TryGetValue(relative, out string? baselineHash);
            if (remoteExists == baselineExists && (!remoteExists || string.Equals(remoteHash, baselineHash, StringComparison.Ordinal))) continue;

            changed.Add(relative);
            string fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            bool named = FleetPlanContext.DeclaredPaths(plan, step).Any(declared => FleetPlanContext.Names(declared, fullPath));
            if (!named)
            {
                conflicts.Add(relative);
                continue;
            }

            bool hubExists = hub.TryGetValue(relative, out string? hubHash);
            if (hubExists == remoteExists && (!hubExists || string.Equals(hubHash, remoteHash, StringComparison.Ordinal))) continue;
            if (hubExists == baselineExists && (!hubExists || string.Equals(hubHash, baselineHash, StringComparison.Ordinal)))
            {
                apply.Add(relative);
                continue;
            }

            conflicts.Add(relative);
        }

        return new WorkerWorkspaceRecoveryAnalysis(changed, apply, conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal enum ExistingWorkerFile
    {
        /// <summary>The worker's copy is the hub's: nothing to do.</summary>
        Matches,
        /// <summary>The hub has moved on and the worker's copy is as it was synced: it is replaced by the hub's.</summary>
        Replace,
        /// <summary>The hub no longer has the file and the worker's copy is as it was synced: it is removed.</summary>
        Remove,
        /// <summary>The worker's copy was changed after the last sync (or is unknown): it may be unsynced work, so staging stops.</summary>
        Refuse
    }

    /// <summary>
    /// What to do with a file already in a worker's workspace when the project is staged again. A file that differs from the
    /// hub's is unsynced work only if it also differs from what the worker held at the last sync; when it is exactly that,
    /// nobody touched it there and the hub (which the runner or the user changed since) is the truth. Without a recorded
    /// sync (a restart) the safe answer stays the old one: refuse. A frozen test file is the exception: the hub copy is the
    /// accepted one and the worker's copy is never synced back, so a frozen file that differs from the hub's is replaced by it.
    /// </summary>
    internal static ExistingWorkerFile JudgeExistingWorkerFile(string workerHash, string? hubHash, string? lastSyncedHash, bool frozen = false)
    {
        if (hubHash is not null && string.Equals(workerHash, hubHash, StringComparison.Ordinal))
        {
            return ExistingWorkerFile.Matches;
        }

        if (frozen && hubHash is not null)
        {
            return ExistingWorkerFile.Replace;
        }

        if (lastSyncedHash is null || !string.Equals(workerHash, lastSyncedHash, StringComparison.Ordinal))
        {
            return ExistingWorkerFile.Refuse;
        }

        return hubHash is null ? ExistingWorkerFile.Remove : ExistingWorkerFile.Replace;
    }

    // The remote files a sync compares with the hub. A frozen test file is never one of them, under any spelling the platform
    // reads as the same name, so the hub copy stays the accepted one.
    internal static IReadOnlyList<string> FilesToCompareOnSync(IEnumerable<string> relatives, IReadOnlyList<PlanFrozenTestFile> frozen, string platform) =>
        relatives.Where(relative => AllowedRelative(relative) && !IsFrozenFile(relative, frozen, platform)).ToArray();

    // The staged files a sync may propagate as deleted: gone from the worker, named by the step, and not a frozen test file.
    internal static IReadOnlyList<string> DeletionCandidates(IEnumerable<string> staged, IReadOnlySet<string> remaining,
        IReadOnlyList<PlanFrozenTestFile> frozen, string platform, Func<string, bool> namedByStep) =>
        staged.Where(path => !remaining.Contains(path) && !IsFrozenFile(path, frozen, platform) && namedByStep(path)).ToArray();

    // The one rule for what a frozen test file is: the project-relative path, compared as the worker's platform names it.
    internal static bool IsFrozenFile(string relative, IReadOnlyList<PlanFrozenTestFile> frozen, string platform) =>
        FrozenTestFiles.Contains(frozen, relative, IsWindowsPlatform(platform));

    private static bool IsWindowsPlatform(string platform) => string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase);

    public IDisposable Enter() => WorkerWorkspaceContext.Push(this);

    public async Task SyncToHubAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sftp!.Exists(RemoteRoot))
                throw new IOException($"Worker workspace on {Machine} disappeared; no files were synced to the hub.");

            var remoteFiles = EnumerateRemoteFiles(RemoteRoot).ToArray();
            var remaining = new HashSet<string>(remoteFiles.Select(file => RelativeRemote(RemoteRoot, file)), StringComparer.OrdinalIgnoreCase);
            var pendingWrites = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var remoteHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pendingDeletes = new List<string>();
            var conflicts = new List<string>();
            // A frozen test file is never carried to the hub, so the hub copy stays the accepted one (no write, no conflict).
            foreach (string relative in FilesToCompareOnSync(remoteFiles.Select(file => RelativeRemote(RemoteRoot, file)), _frozenFiles, _config.Platform))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = JoinRemote(RemoteRoot, relative);
                if (_sftp.GetAttributes(path).Size > 20 * 1024 * 1024)
                    throw new IOException($"Worker sync refused an output larger than 20 MiB: {relative}.");
                string local = SafeHubPath(relative);
                using var content = new MemoryStream();
                _sftp.DownloadFile(path, content);
                byte[] bytes = content.ToArray();
                string remoteHash = HashBytes(bytes);
                remoteHashes[relative] = remoteHash;

                _baselineHashes.TryGetValue(relative, out string? baseline);
                string? localHash = File.Exists(local) ? HashFile(local) : null;
                if (baseline == remoteHash)
                {
                    // This worker copy did not change the file. Keep any new hub-side edit/deletion;
                    // RefreshFromHubAsync below will bring it back to the worker.
                    continue;
                }

                if (baseline is not null && localHash is null)
                {
                    conflicts.Add(relative);
                    continue;
                }

                if (localHash is not null && localHash != baseline && localHash != remoteHash)
                {
                    conflicts.Add(relative);
                    continue;
                }

                if (baseline is null && localHash is not null && localHash != remoteHash)
                {
                    conflicts.Add(relative);
                    continue;
                }

                if (localHash != remoteHash) pendingWrites[relative] = bytes;
            }

            // Propagate intentional workspace deletions, but only for files this manager staged and only inside
            // the original project. If SFTP could not enumerate the workspace, the guard above prevents deletion.
            foreach (string relative in DeletionCandidates(_stagedFiles, remaining, _frozenFiles, _config.Platform, MayDeleteOnHub))
            {
                string local = SafeHubPath(relative);
                string? baseline = _baselineHashes.GetValueOrDefault(relative);
                string? localHash = File.Exists(local) ? HashFile(local) : null;
                if (localHash is not null && localHash != baseline)
                {
                    conflicts.Add(relative);
                }
                else if (localHash is not null)
                {
                    pendingDeletes.Add(local);
                }
            }

            if (conflicts.Count > 0)
            {
                throw new IOException($"Worker sync on {Machine} found hub-side edits made during this attempt in: {string.Join(", ", conflicts.Take(10))}. Nothing from this sync was applied; review the conflicting files before retrying.");
            }

            foreach ((string relative, byte[] bytes) in pendingWrites)
            {
                string local = SafeHubPath(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                await File.WriteAllBytesAsync(local, bytes, cancellationToken);
            }
            foreach (string local in pendingDeletes) File.Delete(local);

            _stagedFiles.Clear();
            foreach (string relative in remaining.Where(AllowedRelative)) _stagedFiles.Add(relative);
            _baselineHashes.Clear();
            foreach ((string relative, string hash) in remoteHashes) _baselineHashes[relative] = hash;
            _onSynced?.Invoke(new Dictionary<string, string>(remoteHashes, StringComparer.OrdinalIgnoreCase));
            _logger.LogInformation("Synced worker {Machine} workspace back to the plan project ({FileCount} files).", Machine, remaining.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshFromHubAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (string source in EnumerateStageFiles(_hubRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo info = new(source);
                if (info.Length > 20 * 1024 * 1024) continue;
                string relative = Path.GetRelativePath(_hubRoot, source).Replace('\\', '/');
                string destination = JoinRemote(RemoteRoot, relative);
                EnsureNoLinkComponents(destination);
                CreateDirectoryTree(Parent(destination));
                UploadHubFile(source, destination);
                _stagedFiles.Add(relative);
                _baselineHashes[relative] = HashFile(source);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // The newest part of what a killed command printed is kept: a failing assertion is printed last, before the process hangs.
    private const int MaxPartialOutputCharacters = 20_000;

    // SSH.NET reads a command's output streams, which can stay open after a timeout; a read that waits on them must not hold up the failure.
    private static readonly TimeSpan PartialOutputReadLimit = TimeSpan.FromSeconds(5);

    internal static string? PartialOutputText(string? stdout, string? stderr)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout)) text.Append(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (text.Length > 0) text.Append('\n');
            text.Append("--- stderr ---\n").Append(stderr);
        }

        if (text.Length == 0) return null;
        string kept = text.Length <= MaxPartialOutputCharacters
            ? text.ToString()
            : text.ToString(text.Length - MaxPartialOutputCharacters, MaxPartialOutputCharacters);
        return char.IsLowSurrogate(kept[0]) ? kept[1..] : kept;
    }

    // What a command printed before its timeout, or null when none can be read in time or it printed nothing.
    private static async Task<string?> PartialOutputOfAsync(SshCommand command)
    {
        try
        {
            return await Task.Run(() => PartialOutputText(command.Result, command.Error)).WaitAsync(PartialOutputReadLimit);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> AnswersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using SshCommand command = _ssh!.CreateCommand("echo fleet-alive");
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            await command.ExecuteAsync(cancellationToken);
            return command.Result.Contains("fleet-alive", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public async Task<string> RunCommandAsync(string command, string? workingDirectory, CancellationToken cancellationToken, string auditTool = "run_command")
    {
        string directory = ResolveRemotePath(workingDirectory);
        Audit(auditTool, command);
        string remoteCommand = _config.Platform == "windows"
            ? BuildWindowsCommand(directory, command)
            : $"cd {QuotePosix(directory)} && {command}";
        using SshCommand remote = _ssh!.CreateCommand(remoteCommand);
        remote.CommandTimeout = _commandTimeout;
        try
        {
            await remote.ExecuteAsync(cancellationToken);
        }
        catch (SshOperationTimeoutException exception)
        {
            throw new WorkerCommandTimeoutException(exception.Message, exception, await PartialOutputOfAsync(remote));
        }
        string stdout = remote.Result;
        string result = $"Exit code: {remote.ExitStatus}\n--- stdout ---\n{stdout}";
        if (!string.IsNullOrWhiteSpace(remote.Error)) result += $"\n--- stderr ---\n{remote.Error}";
        return result;
    }

    internal async Task<FailFirstCheckResult> RunScratchCheckAsync(PlanRecord plan, PlanStep step,
        IReadOnlyDictionary<string, byte[]?> overlay, string command, CancellationToken cancellationToken)
    {
        if (PlanRunnerToolPolicy.CommandRefusal(command) is { } refusal)
            throw new WorkerPathRefusedException(refusal);
        string scratch = ScratchPath(RemoteRoot);
        try
        {
            await RunRemoteRawAsync(BuildScratchCopyCommand(_config.Platform, RemoteRoot, scratch), cancellationToken);
            foreach ((string relative, byte[]? bytes) in overlay)
            {
                string path = JoinRemote(scratch, LocalRelativePath(relative));
                if (bytes is null)
                {
                    if (_sftp!.Exists(path)) _sftp.DeleteFile(path);
                    continue;
                }
                CreateDirectoryTree(Parent(path));
                using var output = _sftp!.Open(path, FileMode.Create, FileAccess.Write);
                await output.WriteAsync(bytes, cancellationToken);
            }

            string remote = _config.Platform == "windows" ? BuildWindowsCommand(scratch, command) : $"cd {QuotePosix(scratch)} && {command}";
            var timer = Stopwatch.StartNew();
            using SshCommand check = _ssh!.CreateCommand(remote);
            check.CommandTimeout = _commandTimeout;
            try { await check.ExecuteAsync(cancellationToken); }
            catch (SshOperationTimeoutException exception)
            {
                throw new WorkerCommandTimeoutException(exception.Message, exception, await PartialOutputOfAsync(check));
            }
            timer.Stop();
            string outputText = $"Exit code: {check.ExitStatus}\n--- stdout ---\n{check.Result}";
            if (!string.IsNullOrWhiteSpace(check.Error)) outputText += $"\n--- stderr ---\n{check.Error}";
            return new FailFirstCheckResult(check.ExitStatus ?? -1, outputText.Length <= 20_000 ? outputText : outputText[^20_000..], timer.Elapsed.TotalSeconds);
        }
        finally
        {
            try { await RunRemoteRawAsync(BuildScratchDeleteCommand(_config.Platform, scratch), CancellationToken.None); }
            catch { }
        }
    }

    // The scratch folder is a sibling of the plan folder, never inside it or equal to it, so a delete can only name the scratch folder.
    internal static string ScratchPath(string remoteRoot)
    {
        string root = remoteRoot.TrimEnd('/', '\\');
        if (root.Length == 0) throw new ArgumentException("The worker plan folder has no path.", nameof(remoteRoot));
        return root + "-scratch";
    }

    internal static string BuildScratchCopyCommand(string platform, string remoteRoot, string scratch)
    {
        if (platform != "windows")
            return $"rm -rf -- {QuotePosix(scratch)} && cp -a -- {QuotePosix(remoteRoot)} {QuotePosix(scratch)}";
        string source = PowerShellLiteral(WindowsPath(remoteRoot));
        string target = PowerShellLiteral(WindowsPath(scratch));
        return $"if (Test-Path -LiteralPath {target}) {{ Remove-Item -LiteralPath {target} -Recurse -Force }}; " +
            $"New-Item -ItemType Directory -Path {target} | Out-Null; robocopy {source} {target} /E /COPY:DAT /R:1 /W:1; " +
            "if ($LASTEXITCODE -gt 7) { exit $LASTEXITCODE }; exit 0";
    }

    internal static string BuildScratchDeleteCommand(string platform, string scratch) =>
        platform == "windows"
            ? $"Remove-Item -LiteralPath {PowerShellLiteral(WindowsPath(scratch))} -Recurse -Force -ErrorAction SilentlyContinue"
            : $"rm -rf -- {QuotePosix(scratch)}";

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private async Task RunRemoteRawAsync(string commandText, CancellationToken cancellationToken)
    {
        using SshCommand command = _ssh!.CreateCommand(commandText);
        command.CommandTimeout = _commandTimeout;
        await command.ExecuteAsync(cancellationToken);
        if (command.ExitStatus != 0) throw new IOException("Could not prepare the worker scratch copy.");
    }

    public Task<string> RunGitCommandAsync(string repositoryPath, string arguments, CancellationToken cancellationToken) =>
        RunCommandAsync("git " + arguments, repositoryPath, cancellationToken, "run_git_command");

    public async Task<string> ReadFileAsync(string path, int? startLine, int? endLine, CancellationToken cancellationToken)
    {
        string remote = ResolveRemotePath(path);
        Audit("read_file", path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!RemoteFileExists(remote)) return $"Error: file not found in worker workspace: {path}";
            string content = await ReadRemoteFileAsync(remote, cancellationToken);
            if (startLine is null && endLine is null) return Truncate(content);
            string[] lines = content.Split('\n');
            int first = Math.Max(startLine ?? 1, 1), last = Math.Min(endLine ?? lines.Length, lines.Length);
            if (first > lines.Length) return $"Error: startLine {first} is past the end of the file ({lines.Length} lines).";
            if (last < first) return $"Error: endLine {last} is before startLine {first}.";
            return Truncate($"[lines {first}-{last} of {lines.Length}]\n" + string.Join("\n", lines[(first - 1)..last]).TrimEnd('\r'));
        }
        finally { _gate.Release(); }
    }

    public async Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (FrozenRefusal(covering: false, path) is { } frozenRefusal) return frozenRefusal;
        string remote = ResolveRemotePath(path); Audit("write_file", path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (RemoteFileExists(remote) && await ReadRemoteFileAsync(remote, cancellationToken) == content)
                return $"Unchanged: {path} already has exactly this content; nothing was written. Run the tests with run_command to see what actually fails.";

            await WriteRemoteFileAsync(remote, content, cancellationToken);
            return $"Wrote {content.Length} characters to {path} in worker workspace {Machine}.";
        }
        finally { _gate.Release(); }
    }

    public async Task<string> EditFileAsync(string path, string oldText, string newText, bool? replaceAll, CancellationToken cancellationToken)
    {
        if (FrozenRefusal(covering: false, path) is { } frozenRefusal) return frozenRefusal;
        if (string.IsNullOrEmpty(oldText)) return "Error: oldText must not be empty.";
        // The whole file: the read tool's text is cut at 50,000 characters, and an edit written back from that text lost the rest.
        string target = ResolveRemotePath(path);
        string current;
        Audit("read_file", path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!RemoteFileExists(target)) return $"Error: file not found in worker workspace: {path}";
            current = await ReadRemoteFileAsync(target, cancellationToken);
        }
        finally { _gate.Release(); }

        bool crlf = current.Contains("\r\n", StringComparison.Ordinal);
        string source = crlf ? NormalizeLines(current) : current;
        if (crlf) { oldText = NormalizeLines(oldText); newText = NormalizeLines(newText); }
        int count = CountOccurrences(source, oldText);
        string note = string.Empty;
        string updated;
        if (count == 0)
        {
            (int lenient, int start, int end) = EditMatching.FindLenient(source, oldText);
            if (lenient == 0) return EditMatching.NotFoundReply(path, path, source, oldText);
            if (lenient > 1) return EditMatching.AmbiguousReply(path, lenient);
            count = 1;
            updated = string.Concat(source.AsSpan(0, start), newText, source.AsSpan(end));
            note = ", " + EditMatching.LenientNote;
        }
        else
        {
            updated = source.Replace(oldText, newText, StringComparison.Ordinal);
        }
        if (updated == source)
            return $"Unchanged: this edit changes nothing in {path} (oldText and newText are the same, or the file already contains the new text). The file is as it was. Run the tests with run_command to see what actually fails, or read the file and change something else.";
        if (count > 1 && replaceAll != true) return $"Error: oldText matches {count} places in {path}; include more surrounding lines or set replaceAll to true.";
        if (crlf) updated = updated.Replace("\n", "\r\n", StringComparison.Ordinal);
        Audit("edit_file", path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteRemoteFileAsync(target, updated, cancellationToken);
            return $"Edited {path}: replaced {(replaceAll == true ? count : 1)} occurrence(s) in worker workspace {Machine}{note}.";
        }
        finally { _gate.Release(); }
    }

    public string ListDirectory(string path)
    {
        string remote = ResolveRemotePath(path); Audit("list_directory", path);
        lock (_sftp!)
        {
            if (!_sftp!.Exists(remote) || !_sftp.GetAttributes(remote).IsDirectory) return $"Error: directory not found in worker workspace: {path}";
            string[] names = _sftp.ListDirectory(remote).Where(file => file.Name is not ("." or ".."))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.Name + (file.IsDirectory ? "/" : string.Empty)).ToArray();
            return names.Length == 0 ? "(empty directory)" : string.Join('\n', names);
        }
    }

    public string FindFiles(string pattern, string? path)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Error: pattern must not be empty.";
        string remoteRoot = ResolveRemotePath(path); Audit("find_files", $"{pattern} {path}");
        Regex matcher = GlobRegex(pattern);
        bool fullPath = pattern.Contains('/') || pattern.Contains('\\');
        string[] matches;
        lock (_sftp!)
        {
            if (!_sftp!.Exists(remoteRoot) || !_sftp.GetAttributes(remoteRoot).IsDirectory) return $"Error: directory not found in worker workspace: {path}";
            matches = EnumerateRemoteFiles(remoteRoot).Select(file => RelativeRemote(remoteRoot, file))
                .Where(relative => matcher.IsMatch(fullPath ? relative : BaseName(relative))).Take(301).ToArray();
        }
        return matches.Length == 0 ? $"No files matching \"{pattern}\" in worker workspace {Machine}." : string.Join('\n', matches.Take(300)) + (matches.Length > 300 ? "\n[showing first 300]" : string.Empty);
    }

    public string SearchFiles(string pattern, string? path, string? fileGlob, bool? ignoreCase)
    {
        Regex expression;
        try { expression = new Regex(pattern, (ignoreCase == true ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException exception) { return $"Error: invalid regular expression: {exception.Message}"; }
        string remoteRoot = ResolveRemotePath(path); Audit("search_files", $"/{pattern}/ {path}");
        Regex? nameFilter = string.IsNullOrWhiteSpace(fileGlob) ? null : GlobRegex(fileGlob);
        var result = new List<string>();
        lock (_sftp!)
        {
            string[] files = _sftp!.Exists(remoteRoot) && !_sftp.GetAttributes(remoteRoot).IsDirectory
                ? [remoteRoot]
                : EnumerateRemoteFiles(remoteRoot).ToArray();
            foreach (string file in files)
            {
                if (nameFilter is not null && !nameFilter.IsMatch(BaseName(file))) continue;
                try
                {
                    var attributes = _sftp.GetAttributes(file);
                    if (attributes.Size > 2 * 1024 * 1024) continue;
                    using var stream = _sftp.OpenRead(file);
                    using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                    int lineNumber = 0;
                    while (reader.ReadLine() is { } line)
                    {
                        lineNumber++;
                        if (line.Contains('\0')) break;
                        if (!expression.IsMatch(line)) continue;
                        result.Add($"{RelativeRemote(remoteRoot, file)}:{lineNumber}: {line.Trim()}");
                        if (result.Count >= 100) break;
                    }
                }
                catch (RegexMatchTimeoutException) { return "Error: the pattern took too long to match."; }
                catch (Exception exception) when (exception is IOException or SftpException) { }
                if (result.Count >= 100) break;
            }
        }
        return result.Count == 0 ? $"No matches for /{pattern}/ in worker workspace {Machine}." : string.Join('\n', result) + (result.Count >= 100 ? "\n[stopped at 100 matches]" : string.Empty);
    }

    public string MoveFile(string source, string destination, bool? overwrite)
    {
        if (FrozenRefusal(covering: true, source, destination) is { } frozenRefusal) return frozenRefusal;
        string from = ResolveRemotePath(source), to = ResolveRemotePath(destination); Audit("move_file", $"{source} -> {destination}");
        lock (_sftp!)
        {
            if (!_sftp!.Exists(from)) return $"Error: not found in worker workspace: {source}";
            if (_sftp.Exists(to) && overwrite != true) return $"Error: destination already exists: {destination}";
            CreateDirectoryTree(Parent(to));
            if (_sftp.Exists(to) && overwrite == true) DeleteTree(to);
            _sftp.RenameFile(from, to);
            return $"Moved {source} to {destination} on worker {Machine}.";
        }
    }

    public string DeleteFile(string path)
    {
        if (FrozenRefusal(covering: true, path) is { } frozenRefusal) return frozenRefusal;
        string remote = ResolveRemotePath(path); Audit("delete_file", path);
        lock (_sftp!)
        {
            if (!_sftp!.Exists(remote)) return $"Error: not found in worker workspace: {path}";
            if (_sftp.GetAttributes(remote).IsDirectory)
            {
                if (_sftp.ListDirectory(remote).Any(file => file.Name is not ("." or ".."))) return "Error: delete_file removes only empty folders.";
                _sftp.DeleteDirectory(remote);
            }
            else _sftp.DeleteFile(remote);
            return $"Deleted {path} on worker {Machine}.";
        }
    }

    public string ProjectOverview(string path, int? depth)
    {
        string root = ResolveRemotePath(path); Audit("project_overview", path);
        int maxDepth = Math.Clamp(depth ?? 3, 1, 6);
        lock (_sftp!)
        {
            if (!_sftp!.Exists(root) || !_sftp.GetAttributes(root).IsDirectory) return $"Error: project folder not found in worker workspace: {path}";
            var lines = new List<string> { $"Worker {Machine}: {root}" };
            AddTree(root, 0, maxDepth, "");
            return string.Join('\n', lines.Take(350));

            void AddTree(string directory, int level, int limit, string indent)
            {
                if (lines.Count >= 350 || level >= limit) return;
                foreach (var entry in _sftp.ListDirectory(directory).Where(item => item.Name is not ("." or ".."))
                             .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (entry.IsDirectory && SkippedDirectories.Contains(entry.Name)) continue;
                    lines.Add(indent + entry.Name + (entry.IsDirectory ? "/" : string.Empty));
                    if (entry.IsDirectory) AddTree(entry.FullName, level + 1, limit, indent + "  ");
                    if (lines.Count >= 350) return;
                }
            }
        }
    }

    // Puts back every frozen test file that the worker no longer holds as accepted: changed or deleted, typically by a command the
    // model ran, which no file tool sees. Runs before each check. Returns the project-relative paths it put back.
    public async Task<IReadOnlyList<string>> RestoreFrozenTestsAsync(CancellationToken cancellationToken)
    {
        var restored = new List<string>();
        if (_frozenFiles.Count == 0) return restored;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (PlanFrozenTestFile frozen in _frozenFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string remote = JoinRemote(RemoteRoot, frozen.Path);
                EnsureNoLinkComponents(remote);
                byte[]? current = await ReadRemoteBytesAsync(remote, cancellationToken);
                if (current is not null && string.Equals(HashBytes(current), frozen.Sha256, StringComparison.OrdinalIgnoreCase)) continue;

                // The hub copy is the one the tests step was accepted with. It is checked here too: a copy that no longer matches
                // must never be written to the worker.
                string local = SafeHubPath(frozen.Path);
                if (!File.Exists(local))
                    throw new IOException($"Frozen test file {frozen.Path} is missing from the hub copy, so it cannot be put back on {Machine}.");
                byte[] hubBytes = await File.ReadAllBytesAsync(local, cancellationToken);
                if (!string.Equals(HashBytes(hubBytes), frozen.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Frozen test file {frozen.Path} on the hub copy no longer matches the accepted test, so it cannot be put back on {Machine}.");
                if (_fileAccess is not null)
                {
                    await WriteRemoteFileAsync(remote, Encoding.UTF8.GetString(hubBytes), cancellationToken);
                }
                else
                {
                    CreateDirectoryTree(Parent(remote));
                    UploadHubFile(local, remote);
                }

                restored.Add(frozen.Path);
            }

            return restored;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFrozen(string relative) => IsFrozenFile(relative, _frozenFiles, _config.Platform);

    // A frozen test file is fixed for the rest of the plan, so a file tool refuses to change it under any of the paths given.
    // Paths are compared in project-relative form. A move or a delete also refuses a path that holds a frozen file, since it would
    // carry that file away or replace it. On a Windows worker a short (8.3) name is refused too: it cannot be matched by name.
    private string? FrozenRefusal(bool covering, params string[] paths)
    {
        if (_frozenFiles.Count == 0) return null;
        bool windowsNames = IsWindowsPlatform(_config.Platform);
        foreach (string path in paths)
        {
            string relative;
            try
            {
                relative = WorkerWorkspacePath.RelativeToProject(path, _hubRoot, RemoteRoot);
            }
            catch (WorkerPathRefusedException)
            {
                // The path is outside the project; ResolveRemotePath reports that itself.
                continue;
            }

            if (windowsNames && FrozenTestFiles.HasShortName(relative)) return FrozenTestFiles.ShortNameRefusal;
            bool hit = covering ? FrozenTestFiles.Covers(_frozenFiles, relative, windowsNames) : FrozenTestFiles.Contains(_frozenFiles, relative, windowsNames);
            if (hit) return FrozenTestRefusal;
        }

        return null;
    }

    private async Task<byte[]?> ReadRemoteBytesAsync(string path, CancellationToken cancellationToken)
    {
        if (!RemoteFileExists(path)) return null;
        if (_fileAccess is not null) return Encoding.UTF8.GetBytes(await _fileAccess.ReadTextAsync(path, cancellationToken));

        using var content = new MemoryStream();
        _sftp!.DownloadFile(path, content);
        return content.ToArray();
    }

    // The edit check (see EditCheckGate) reads and writes the bytes of a file exactly as the file tools would reach it: the same
    // path resolution and refusals, and no text decoding, so a byte order mark and the line breaks come back as they went in.
    public async Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        string remote = CheckedRemotePath(path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadRemoteBytesAsync(remote, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteBytesAsync(string path, byte[]? contents, CancellationToken cancellationToken)
    {
        string remote = CheckedRemotePath(path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (contents is null)
            {
                DeleteRemoteFile(remote);
                return;
            }

            if (_fileAccess is not null)
            {
                _fileAccess.CreateDirectoryTree(Parent(remote));
                await _fileAccess.WriteTextAsync(remote, Encoding.UTF8.GetString(contents), cancellationToken);
                return;
            }

            CreateDirectoryTree(Parent(remote));
            using var output = _sftp!.Open(remote, FileMode.Create, FileAccess.Write);
            await output.WriteAsync(contents, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    // One key per file: the resolved remote path, lower-cased on a Windows worker, where the same name can be spelled two ways.
    public string Canonical(string path)
    {
        string remote = ResolveRemotePath(path);
        return IsWindowsPlatform(_config.Platform) ? remote.ToLowerInvariant() : remote;
    }

    // The refusals the file tools apply, and the sync's rule for which files belong to the project at all. A path refused here
    // throws, so the edit check switches itself off rather than write to it.
    private string CheckedRemotePath(string path)
    {
        string relative = WorkerWorkspacePath.RelativeToProject(path, _hubRoot, RemoteRoot);
        if (!AllowedRelative(relative)) throw new WorkerPathRefusedException($"The edit check does not read or write {path}.");
        if (FrozenRefusal(covering: false, path) is { } frozenRefusal) throw new WorkerPathRefusedException(frozenRefusal);
        return ResolveRemotePath(path);
    }

    private void DeleteRemoteFile(string remote)
    {
        if (!RemoteFileExists(remote)) return;
        if (_fileAccess is not null) throw new NotSupportedException("The in-memory file layer cannot delete a file.");
        _sftp!.DeleteFile(remote);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sftp?.Dispose();
        _ssh?.Dispose();
        _gate.Dispose();
    }

    private string ResolveRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return RemoteRoot;
        string relative = WorkerWorkspacePath.RelativeToProject(path, _hubRoot, RemoteRoot);
        string resolved = JoinRemote(RemoteRoot, relative);
        try { EnsureNoLinkComponents(resolved); }
        catch (UnauthorizedAccessException exception) when (exception.Message != "Worker tool paths must be absolute on the remote machine.")
        {
            throw new WorkerPathRefusedException(exception.Message);
        }
        return resolved;
    }

    private string LocalRelativePath(string path)
    {
        try { return WorkerWorkspacePath.RelativeToProject(path, _hubRoot); }
        catch (WorkerPathRefusedException) { return string.Empty; }
    }

    // A file the worker deleted may go from the hub when the step names it, or when it was created on the hub after the plan
    // started: it is a leftover of this run (a scratch script the worker wrote and removed), not something the project had before.
    private bool MayDeleteOnHub(string relative) =>
        IsNamedByStep(relative) || CreatedSincePlanStarted(SafeHubPath(relative), _planStartedUtc);

    internal static bool CreatedSincePlanStarted(string localPath, DateTimeOffset? planStartedUtc) =>
        planStartedUtc is { } started && File.Exists(localPath) && new DateTimeOffset(File.GetCreationTimeUtc(localPath), TimeSpan.Zero) >= started;

    private bool IsNamedByStep(string relative)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_hubRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return _stepFiles.Any(declared => FleetPlanContext.Names(
            Path.GetFullPath(Path.Combine(_hubRoot, declared.Replace('/', Path.DirectorySeparatorChar))), fullPath));
    }

    private void EnsureNoLinkComponents(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Worker tool paths must be absolute on the remote machine.");

        // File-access fakes represent a trusted in-memory workspace and have no link metadata.
        if (_fileAccess is not null) return;

        string[] paths = WorkerWorkspacePath.PathsForLinkCheck(path, _config.Platform);
        for (int i = 0; i < paths.Length; i++)
        {
            string current = paths[i];
            if (!_sftp!.Exists(current)) continue;
            var attributes = _sftp.GetAttributes(current);
            if (attributes.IsSymbolicLink || (i < paths.Length - 1 && !attributes.IsDirectory))
                throw new UnauthorizedAccessException("Worker tool path passes through a symbolic link or non-directory and was refused.");
        }
    }

    private string SafeHubPath(string relative)
    {
        string full = Path.GetFullPath(Path.Combine(_hubRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = _hubRoot.EndsWith(Path.DirectorySeparatorChar) ? _hubRoot : _hubRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Worker returned a path outside the project.");
        return full;
    }

    // Uploads one hub file. On a Linux worker a script that is meant to be run keeps the right to run (see UploadKeepingMode).
    private void UploadHubFile(string source, string destination) =>
        UploadKeepingMode(source, destination, string.Equals(_config.Platform, "linux", StringComparison.OrdinalIgnoreCase),
            (stream, path) => _sftp!.UploadFile(stream, path, canOverride: true),
            (path, mode) => _sftp!.ChangePermissions(path, mode));

    /// <summary>
    /// Uploads a file and, for a Linux worker, makes it executable when it was meant to be: an upload gives a new file the default
    /// mode, so a project's own script (`./gradlew`, `scripts/check.sh`) would fail with "Permission denied" on the worker. On a
    /// Windows hub there is no mode to copy, so a file that starts with `#!` is taken to be a script; on a Unix hub the file's own
    /// execute bit decides. Other files are left as the upload made them, and nothing is changed for a Windows worker.
    /// </summary>
    internal static void UploadKeepingMode(string source, string destination, bool linuxWorker, Action<Stream, string> upload, Action<string, short> setMode)
    {
        using FileStream input = File.OpenRead(source);
        bool executable = linuxWorker && IsMeantToBeExecuted(input, source);
        input.Position = 0;
        upload(input, destination);
        if (executable)
        {
            setMode(destination, 755);
        }
    }

    internal static bool IsMeantToBeExecuted(FileStream input, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
        }

        return input.ReadByte() == '#' && input.ReadByte() == '!';
    }

    private IEnumerable<string> EnumerateStageFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string subdirectory in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(subdirectory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || SkippedDirectories.Contains(info.Name)) continue;
                pending.Push(subdirectory);
            }
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || !AllowedRelative(Path.GetRelativePath(root, file).Replace('\\', '/'))) continue;
                yield return file;
            }
        }
    }

    private IEnumerable<string> EnumerateRemoteFiles(string directory)
    {
        foreach (var entry in _sftp!.ListDirectory(directory).Where(item => item.Name is not ("." or "..")))
        {
            if (entry.IsSymbolicLink) continue;
            if (entry.IsDirectory)
            {
                if (!SkippedDirectories.Contains(entry.Name))
                    foreach (string nested in EnumerateRemoteFiles(entry.FullName)) yield return nested;
            }
            else if (AllowedRelative(RelativeRemote(RemoteRoot, entry.FullName))) yield return entry.FullName;
        }
    }

    private IEnumerable<string> EnumerateRemoteFilesForRecovery(string directory, ICollection<string> linkConflicts)
    {
        foreach (var entry in _sftp!.ListDirectory(directory).Where(item => item.Name is not ("." or "..")))
        {
            if (entry.IsSymbolicLink)
            {
                string relative = RelativeRemote(RemoteRoot, entry.FullName);
                if (AllowedRelative(relative)) linkConflicts.Add(relative);
                continue;
            }

            if (entry.IsDirectory)
            {
                if (!SkippedDirectories.Contains(entry.Name))
                    foreach (string nested in EnumerateRemoteFilesForRecovery(entry.FullName, linkConflicts)) yield return nested;
            }
            else if (AllowedRelative(RelativeRemote(RemoteRoot, entry.FullName)))
            {
                yield return entry.FullName;
            }
        }
    }

    private void CreateDirectoryTree(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return;
        EnsureNoLinkComponents(path);
        if (_sftp!.Exists(path)) return;
        string parent = Parent(path);
        if (parent != path && !_sftp.Exists(parent)) CreateDirectoryTree(parent);
        if (!_sftp.Exists(path)) _sftp.CreateDirectory(path);
    }

    private bool RemoteFileExists(string path) => _fileAccess?.Exists(path) ?? _sftp!.Exists(path);

    private async Task<string> ReadRemoteFileAsync(string path, CancellationToken cancellationToken)
    {
        if (_fileAccess is not null) return await _fileAccess.ReadTextAsync(path, cancellationToken);
        using var stream = _sftp!.OpenRead(path);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private async Task WriteRemoteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (_fileAccess is not null)
        {
            _fileAccess.CreateDirectoryTree(Parent(path));
            await _fileAccess.WriteTextAsync(path, content, cancellationToken);
            return;
        }

        CreateDirectoryTree(Parent(path));
        using var output = _sftp!.Open(path, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(output, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private void InitializeWorkspaceGit()
    {
        string gitDirectory = JoinRemote(RemoteRoot, ".git");
        if (_sftp!.Exists(gitDirectory)) return;

        const string initialize = "git init -q && git config user.name \"AgentFleet Worker\" && " +
                                  "git config user.email agentfleet@localhost";
        string remoteCommand = _config.Platform == "windows"
            ? BuildWindowsCommand(RemoteRoot, initialize)
            : $"cd {QuotePosix(RemoteRoot)} && {initialize}";
        using SshCommand command = _ssh!.CreateCommand(remoteCommand);
        command.CommandTimeout = TimeSpan.FromSeconds(60);
        command.Execute();
        if (command.ExitStatus != 0)
        {
            _logger.LogInformation("Worker {Machine} has no usable git executable; the isolated workspace remains available without git history.", Machine);
        }
        else
        {
            _logger.LogInformation("Created isolated git metadata for the staged project on worker {Machine}; no project files were committed.", Machine);
        }
    }

    private void DeleteTree(string path)
    {
        if (_sftp is null || !_sftp.Exists(path)) return;
        var attributes = _sftp.GetAttributes(path);
        if (attributes.IsDirectory && !attributes.IsSymbolicLink)
        {
            foreach (var entry in _sftp.ListDirectory(path).Where(item => item.Name is not ("." or ".."))) DeleteTree(entry.FullName);
            _sftp.DeleteDirectory(path);
        }
        else _sftp.DeleteFile(path);
    }

    private void Audit(string tool, string detail) => _logger.LogInformation("Worker tool {Tool} invoked on {Machine}.", tool, Machine);
    private static string JoinRemote(params string[] parts)
    {
        string[] present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        if (present.Length == 0) return string.Empty;
        bool rooted = present[0].StartsWith("/", StringComparison.Ordinal);
        string joined = string.Join('/', present.Select(part => part.Trim('/')));
        return rooted ? "/" + joined : joined;
    }
    private static string Parent(string path) => path.LastIndexOf('/') is int index && index > 0 ? path[..index] : "/";
    private static string RelativeRemote(string root, string path) => path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal) ? path[(root.TrimEnd('/').Length + 1)..] : string.Empty;
    private static string BaseName(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string SafePart(string value) => Regex.Replace(value, "[^A-Za-z0-9_-]", "_");
    private static bool AllowedRelative(string relative) => !string.IsNullOrWhiteSpace(relative) &&
        !relative.Split('/').Any(SkippedDirectories.Contains) && !relative.Split('/').Any(PrivateDirectories.Contains) &&
        !relative.Split('/').Any(PrivateFiles.Contains) &&
        !relative.Split('/').Any(part => SecretEnvironmentFile.IsMatch(part) || PrivateExtensions.Contains(Path.GetExtension(part))) &&
        !relative.Split('/').Any(IsWindowsDeviceName);

    // A Linux worker can create a file called nul (a command ending "> nul" does), but a Windows hub cannot hold one: its path
    // resolves to the nul device, outside the project, and the whole sync failed with "Worker returned a path outside the
    // project" (measured on a worker: the step was parked as an environment fault). Such a file stays on the worker and is not
    // synced; nothing on the hub can depend on a file it cannot have.
    private static bool IsWindowsDeviceName(string part) => OperatingSystem.IsWindows() && WindowsDeviceName.IsMatch(part.TrimEnd(' ', '.'));

    private static bool IsSafeRelative(string relative)
    {
        string normalized = relative.Replace('\\', '/');
        return AllowedRelative(normalized) && !Path.IsPathRooted(relative) &&
            !Regex.IsMatch(normalized, "^[A-Za-z]:") && !normalized.Contains('\0') &&
            !normalized.Split('/').Any(part => part is "" or "." or "..");
    }
    internal static bool IsAllowedProjectFile(string relative) => AllowedRelative(relative);
    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string HashBytes(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string QuoteWindows(string value)
    {
        return "\"" + WindowsPath(value).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
    private static string WindowsPath(string value)
    {
        if (Regex.IsMatch(value, "^/[A-Za-z]:/")) value = value[1..];
        return value.Replace('/', '\\');
    }
    internal static string BuildWindowsCommand(string directory, string command)
    {
        if (!command.Contains("&&", StringComparison.Ordinal) && !command.Contains("||", StringComparison.Ordinal))
        {
            string powershellPath = WindowsPath(directory).Replace("'", "''", StringComparison.Ordinal);
            return $"Set-Location -LiteralPath '{powershellPath}' -ErrorAction Stop; [Environment]::CurrentDirectory = (Get-Location).ProviderPath; {command}";
        }

        string cmdCommand = $"cd /d {QuoteWindows(directory)} && {command}";
        return $"cmd.exe /d /s /c '{cmdCommand.Replace("'", "''", StringComparison.Ordinal)}'";
    }
    private static string Truncate(string value) => value.Length <= 50_000 ? value : value[..50_000] + "\n[truncated at 50000 characters]";
    private static string NormalizeLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
        return count;
    }
    private static Regex GlobRegex(string pattern)
    {
        string expression = "^" + Regex.Escape(pattern.Replace('\\', '/'))
            .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }
}

internal static class WorkerWorkspacePath
{
    internal static string[] PathsForLinkCheck(string path, string platform)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Worker tool paths must be absolute on the remote machine.");

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = "/";
        int start = 0;
        if (string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase) &&
            parts.Length > 0 && Regex.IsMatch(parts[0], "^[A-Za-z]:$", RegexOptions.CultureInvariant))
        {
            // Windows SFTP exposes drive roots as pseudo-directories such as /C:/. Stat the
            // real path components below that root; some servers list /C: but reject stat(/C:).
            current = $"/{parts[0]}/";
            start = 1;
        }

        var paths = new List<string>(parts.Length - start);
        for (int i = start; i < parts.Length; i++)
        {
            current = current.TrimEnd('/') + "/" + parts[i];
            paths.Add(current);
        }

        return paths.ToArray();
    }

    public static string RelativeToProject(string path, string hubRoot, string? remoteRoot = null)
    {
        string normalized = NormalizePath(path);
        string[] roots = remoteRoot is null ? [NormalizePath(hubRoot)] : [NormalizePath(hubRoot), NormalizePath(remoteRoot)];
        foreach (string root in roots)
        {
            StringComparison comparison = IsDriveRoot(root) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(normalized, root, comparison))
            {
                normalized = string.Empty;
                break;
            }

            if (normalized.StartsWith(root + "/", comparison))
            {
                normalized = normalized[(root.Length + 1)..];
                break;
            }
        }

        if (Regex.IsMatch(normalized, "^[A-Za-z]:/") || normalized.StartsWith("/", StringComparison.Ordinal))
            throw new WorkerPathRefusedException("Absolute paths outside the staged project are not available to worker tools.");

        var parts = new List<string>();
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new WorkerPathRefusedException("Path escapes the worker project workspace.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static string NormalizePath(string path)
    {
        string normalized = path.Trim().Replace('\\', '/').TrimEnd('/');
        return Regex.IsMatch(normalized, "^/[A-Za-z]:($|/)", RegexOptions.CultureInvariant)
            ? normalized[1..]
            : normalized;
    }

    private static bool IsDriveRoot(string path) => Regex.IsMatch(path, "^[A-Za-z]:($|/)", RegexOptions.CultureInvariant);
}

/// <summary>One dispatch point keeps normal chat on the hub and approved plan tools on their selected worker.</summary>
internal static class WorkspaceTools
{
    public static Task<string> ReadFileAsync(string path, int? startLine, int? endLine, CancellationToken cancellationToken) =>
        WorkerWorkspaceContext.Current is { } worker
            ? worker.ReadFileAsync(path, startLine, endLine, cancellationToken)
            : HubFileSystemTools.ReadFileAsync(path, startLine, endLine, cancellationToken);

    // The placeholder the runner puts where it shortened old tool output (see ContextSizeChatClient.RemovedMarker) is not part
    // of any file. A small model that copied it into a write was left with a file cut off after its first lines, found only
    // when the check failed on a syntax error. Refusing the write says what is wrong at once.
    internal static bool CarriesHistoryPlaceholder(string? text) =>
        text?.Contains(ContextSizeChatClient.RemovedMarker, StringComparison.Ordinal) == true;

    internal static string HistoryPlaceholderRefusal(string path) =>
        $"Error: nothing was written to {path}. The text contains \"{ContextSizeChatClient.RemovedMarker}\", which only stands in for old " +
        "output in this conversation and is not part of any file. Write the complete, real content (read the file first if you need it).";

    public static Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken) =>
        CarriesHistoryPlaceholder(content)
            ? Task.FromResult(HistoryPlaceholderRefusal(path))
            : WorkerWorkspaceContext.Current is { } worker
                ? WithEditCheck(path, cancellationToken, token => worker.WriteFileAsync(path, content, token))
                : HubFileSystemTools.WriteFileAsync(path, content, cancellationToken);

    public static Task<string> EditFileAsync(string path, string oldText, string newText, bool? replaceAll, CancellationToken cancellationToken) =>
        CarriesHistoryPlaceholder(newText)
            ? Task.FromResult(HistoryPlaceholderRefusal(path))
            : WorkerWorkspaceContext.Current is { } worker
                ? WithEditCheck(path, cancellationToken, token => worker.EditFileAsync(path, oldText, newText, replaceAll, token))
                : HubFileSystemTools.EditFileAsync(path, oldText, newText, replaceAll, cancellationToken);

    // Without a gate in scope the edit is the same call as before. With one, the check runs before and after the edit; a reply
    // that starts with "Error:" changed nothing, so there is nothing to check.
    private static Task<string> WithEditCheck(string path, CancellationToken cancellationToken, Func<CancellationToken, Task<string>> edit) =>
        EditCheckContext.Current is { } gate ? CheckedEditAsync(gate, path, cancellationToken, edit) : edit(cancellationToken);

    private static async Task<string> CheckedEditAsync(EditCheckGate gate, string path, CancellationToken cancellationToken, Func<CancellationToken, Task<string>> edit)
    {
        await gate.BeforeMutationAsync(path, cancellationToken);
        string reply = await edit(cancellationToken);
        return reply.StartsWith("Error:", StringComparison.Ordinal) ? reply : await gate.AfterMutationAsync(path, reply, cancellationToken);
    }

    public static string ListDirectory(string path) =>
        WorkerWorkspaceContext.Current is { } worker ? worker.ListDirectory(path) : HubFileSystemTools.ListDirectory(path);

    public static string FindFiles(string pattern, string? path) =>
        WorkerWorkspaceContext.Current is { } worker ? worker.FindFiles(pattern, path) : HubFileSystemTools.FindFiles(pattern, path);

    public static string SearchFiles(string pattern, string? path, string? fileGlob, bool? ignoreCase) =>
        WorkerWorkspaceContext.Current is { } worker ? worker.SearchFiles(pattern, path, fileGlob, ignoreCase) : HubFileSystemTools.SearchFiles(pattern, path, fileGlob, ignoreCase);

    public static Task<string> RunGitCommandAsync(string repositoryPath, string arguments, HubShellOptions options, ILogger logger, CancellationToken cancellationToken) =>
        WorkerWorkspaceContext.Current is { } worker
            ? worker.RunGitCommandAsync(repositoryPath, arguments, cancellationToken)
            : HubShellTools.RunGitCommandAsync(repositoryPath, arguments, options, logger, cancellationToken);

    public static Task<string> RunCommandAsync(string command, string? workingDirectory, HubShellOptions options, ILogger logger, CancellationToken cancellationToken) =>
        WorkerWorkspaceContext.Current is { } worker
            ? worker.RunCommandAsync(command, workingDirectory, cancellationToken)
            : HubShellTools.RunCommandAsync(command, workingDirectory, options, logger, cancellationToken);

    public static string ProjectOverview(string path, int? depth) =>
        WorkerWorkspaceContext.Current is { } worker ? worker.ProjectOverview(path, depth) : ProjectTools.ProjectOverview(path, depth);

    public static string MoveFile(string source, string destination, bool? overwrite)
    {
        if (WorkerWorkspaceContext.Current is not { } worker) return ProjectTools.MoveFile(source, destination, overwrite);
        // The edit check follows writes and edits only; after a move its picture of the project is out of date.
        EditCheckContext.Current?.Disable("a file was moved or deleted");
        return worker.MoveFile(source, destination, overwrite);
    }

    public static string DeleteFile(string path)
    {
        if (WorkerWorkspaceContext.Current is not { } worker) return ProjectTools.DeleteFile(path);
        EditCheckContext.Current?.Disable("a file was moved or deleted");
        return worker.DeleteFile(path);
    }
}
