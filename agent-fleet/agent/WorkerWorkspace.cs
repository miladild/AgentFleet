using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace AgentFleet;

/// <summary>Creates isolated per-plan copies on a selected, explicitly configured worker.</summary>
internal sealed class WorkerWorkspaceManager(FleetOptions fleet, ILogger logger, TimeSpan commandTimeout)
{
    public async Task<WorkerWorkspaceSession> StageAsync(PlanRecord plan, PlanStep step, string machine, CancellationToken cancellationToken)
    {
        FleetNodeDefinition node = fleet.Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, machine, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Worker workspace unavailable: machine '{machine}' is not configured.");
        if (string.Equals(node.Name, "hub", StringComparison.OrdinalIgnoreCase) || node.Vision)
            throw new InvalidOperationException($"Worker workspace unavailable: '{machine}' is not a coding worker; plan work must run on a configured worker machine.");
        if (!string.Equals(node.Tier, step.Tier, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Worker workspace unavailable: '{machine}' is configured for the {node.Tier ?? "unspecified"} tier, not this step's approved {step.Tier} tier.");
        FleetWorkerWorkspaceConfig config = node.Workspace
            ?? throw new InvalidOperationException($"Worker workspace unavailable: machine '{machine}' has no worker workspace configured. Configure a dedicated low-privilege SSH account and pinned host key in Config.");
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !Directory.Exists(plan.WorkingDirectory))
        {
            throw new InvalidOperationException("Worker workspace unavailable: the plan's project folder does not exist on the hub.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var session = new WorkerWorkspaceSession(machine, config, Path.GetFullPath(plan.WorkingDirectory), logger, commandTimeout);
        try
        {
            session.Connect();
            session.Stage(plan.Id, step, cancellationToken);
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
internal sealed class WorkerWorkspaceSession : IDisposable
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
    private SftpClient? _sftp;
    private SshClient? _ssh;
    private bool _disposed;

    internal WorkerWorkspaceSession(string machine, FleetWorkerWorkspaceConfig config, string hubRoot, ILogger logger, TimeSpan commandTimeout)
    {
        Machine = machine;
        _config = config;
        _hubRoot = hubRoot;
        _logger = logger;
        _commandTimeout = commandTimeout;
        RemoteRoot = JoinRemote(config.Root.TrimEnd('/'), "agentfleet", "plans", "workspace", SafePart(machine), SafePart("plan"));
    }

    public string Machine { get; }
    public string Platform => _config.Platform;
    public string RemoteRoot { get; private set; }
    public string HubRoot => _hubRoot;

    internal string SnapshotId()
    {
        string contents = string.Join('\n', _baselineHashes
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => $"{file.Key}\0{file.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)))[..16].ToLowerInvariant();
    }

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
        _sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        _ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        SandboxSsh.Guard(_sftp, _config.HostKey, null);
        SandboxSsh.Guard(_ssh, _config.HostKey, null);
        _sftp.Connect();
        _ssh.Connect();
        ValidateWorkerAccount();
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

    internal void Stage(string planId, PlanStep step, CancellationToken cancellationToken)
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
            if (!currentFiles.Contains(relative))
                throw new InvalidOperationException($"Worker workspace unavailable: existing worker file '{relative}' is absent from the hub checkout. It may contain unsynced work, so it was preserved.");

            using var content = new MemoryStream();
            _sftp.DownloadFile(remote, content);
            if (!string.Equals(HashBytes(content.ToArray()), HashFile(sourceByRelative[relative]), StringComparison.Ordinal))
                throw new InvalidOperationException($"Worker workspace unavailable: existing worker file '{relative}' differs from the hub checkout. It may contain unsynced work, so the workspace was preserved.");
        }

        foreach (string source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(_hubRoot, source).Replace('\\', '/');
            string destination = JoinRemote(RemoteRoot, relative);
            EnsureNoLinkComponents(destination);
            CreateDirectoryTree(Parent(destination));
            using FileStream input = File.OpenRead(source);
            _sftp!.UploadFile(input, destination, canOverride: true);
            _stagedFiles.Add(relative);
            _baselineHashes[relative] = HashFile(source);
        }

        InitializeWorkspaceGit();
    }

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
            foreach (string path in remoteFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = RelativeRemote(RemoteRoot, path);
                if (!AllowedRelative(relative)) continue;
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
            foreach (string relative in _stagedFiles.Where(path => !remaining.Contains(path) && IsNamedByStep(path)))
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
                using FileStream input = File.OpenRead(source);
                _sftp!.UploadFile(input, destination, canOverride: true);
                _stagedFiles.Add(relative);
                _baselineHashes[relative] = HashFile(source);
            }
        }
        finally
        {
            _gate.Release();
        }
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
        await remote.ExecuteAsync(cancellationToken);
        string stdout = remote.Result;
        string result = $"Exit code: {remote.ExitStatus}\n--- stdout ---\n{stdout}";
        if (!string.IsNullOrWhiteSpace(remote.Error)) result += $"\n--- stderr ---\n{remote.Error}";
        return result;
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
            if (!_sftp!.Exists(remote)) return $"Error: file not found in worker workspace: {path}";
            using var stream = _sftp.OpenRead(remote);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            string content = await reader.ReadToEndAsync(cancellationToken);
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
        string remote = ResolveRemotePath(path); Audit("write_file", path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CreateDirectoryTree(Parent(remote));
            using var output = _sftp!.Open(remote, FileMode.Create, FileAccess.Write);
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
            return $"Wrote {content.Length} characters to {path} in worker workspace {Machine}.";
        }
        finally { _gate.Release(); }
    }

    public async Task<string> EditFileAsync(string path, string oldText, string newText, bool? replaceAll, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(oldText)) return "Error: oldText must not be empty.";
        string current = await ReadFileAsync(path, null, null, cancellationToken);
        if (current.StartsWith("Error:", StringComparison.Ordinal)) return current;
        string target = ResolveRemotePath(path);
        bool crlf = current.Contains("\r\n", StringComparison.Ordinal);
        string source = crlf ? NormalizeLines(current) : current;
        if (crlf) { oldText = NormalizeLines(oldText); newText = NormalizeLines(newText); }
        int count = CountOccurrences(source, oldText);
        if (count == 0) return $"Error: oldText was not found in {path}; read the file and copy the exact text.";
        if (count > 1 && replaceAll != true) return $"Error: oldText matches {count} places in {path}; include more surrounding lines or set replaceAll to true.";
        string updated = source.Replace(oldText, newText, StringComparison.Ordinal);
        if (crlf) updated = updated.Replace("\n", "\r\n", StringComparison.Ordinal);
        Audit("edit_file", path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var output = _sftp!.Open(target, FileMode.Create, FileAccess.Write);
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            await writer.WriteAsync(updated.AsMemory(), cancellationToken);
            return $"Edited {path}: replaced {(replaceAll == true ? count : 1)} occurrence(s) in worker workspace {Machine}.";
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
        EnsureNoLinkComponents(resolved);
        return resolved;
    }

    private string LocalRelativePath(string path)
    {
        try { return WorkerWorkspacePath.RelativeToProject(path, _hubRoot); }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    private bool IsNamedByStep(string relative) => _stepFiles.Any(declared =>
        string.Equals(declared, relative, StringComparison.OrdinalIgnoreCase) ||
        declared.EndsWith("/", StringComparison.Ordinal) && relative.StartsWith(declared, StringComparison.OrdinalIgnoreCase));

    private void EnsureNoLinkComponents(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Worker tool paths must be absolute on the remote machine.");

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

    private void CreateDirectoryTree(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return;
        EnsureNoLinkComponents(path);
        if (_sftp!.Exists(path)) return;
        string parent = Parent(path);
        if (parent != path && !_sftp.Exists(parent)) CreateDirectoryTree(parent);
        if (!_sftp.Exists(path)) _sftp.CreateDirectory(path);
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
        !relative.Split('/').Any(part => SecretEnvironmentFile.IsMatch(part) || PrivateExtensions.Contains(Path.GetExtension(part)));
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
        string normalized = path.Trim().Replace('\\', '/');
        string hub = hubRoot.Replace('\\', '/').TrimEnd('/');
        if (normalized.StartsWith(hub + "/", StringComparison.OrdinalIgnoreCase)) normalized = normalized[(hub.Length + 1)..];
        else if (string.Equals(normalized, hub, StringComparison.OrdinalIgnoreCase)) normalized = string.Empty;
        else if (remoteRoot is not null)
        {
            string remote = remoteRoot.TrimEnd('/');
            if (normalized.StartsWith(remote + "/", StringComparison.Ordinal)) normalized = normalized[(remote.Length + 1)..];
            else if (string.Equals(normalized, remote, StringComparison.Ordinal)) normalized = string.Empty;
        }

        if (Regex.IsMatch(normalized, "^[A-Za-z]:/") || normalized.StartsWith("/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Absolute paths outside the staged project are not available to worker tools.");

        var parts = new List<string>();
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new UnauthorizedAccessException("Path escapes the worker project workspace.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }

        return string.Join('/', parts);
    }
}

/// <summary>One dispatch point keeps normal chat on the hub and approved plan tools on their selected worker.</summary>
internal static class WorkspaceTools
{
    public static Task<string> ReadFileAsync(string path, int? startLine, int? endLine, CancellationToken cancellationToken) =>
        WorkerWorkspaceContext.Current is { } worker
            ? worker.ReadFileAsync(path, startLine, endLine, cancellationToken)
            : HubFileSystemTools.ReadFileAsync(path, startLine, endLine, cancellationToken);

    public static Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken) =>
        WorkerWorkspaceContext.Current is { } worker
            ? worker.WriteFileAsync(path, content, cancellationToken)
            : HubFileSystemTools.WriteFileAsync(path, content, cancellationToken);

    public static Task<string> EditFileAsync(string path, string oldText, string newText, bool? replaceAll, CancellationToken cancellationToken) =>
        WorkerWorkspaceContext.Current is { } worker
            ? worker.EditFileAsync(path, oldText, newText, replaceAll, cancellationToken)
            : HubFileSystemTools.EditFileAsync(path, oldText, newText, replaceAll, cancellationToken);

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

    public static string MoveFile(string source, string destination, bool? overwrite) =>
        WorkerWorkspaceContext.Current is { } worker ? worker.MoveFile(source, destination, overwrite) : ProjectTools.MoveFile(source, destination, overwrite);

    public static string DeleteFile(string path) =>
        WorkerWorkspaceContext.Current is { } worker ? worker.DeleteFile(path) : ProjectTools.DeleteFile(path);
}
