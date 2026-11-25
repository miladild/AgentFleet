using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

internal sealed record OllamaAssetInfo(string Name, string Url, string? Sha256);

/// <summary>What the last "Check for updates" click found. Kept in memory only.</summary>
internal sealed record OllamaReleaseInfo(string Version, DateTimeOffset? PublishedAtUtc, DateTimeOffset CheckedAtUtc, IReadOnlyList<OllamaAssetInfo> Assets);

/// <summary>The download and the commands for a person who updates a machine by hand.</summary>
internal sealed record OllamaManualStep(string Platform, string Asset, string Url, string? Sha256, IReadOnlyList<string> Commands, string Note);

internal sealed record OllamaMachineStatus(
    string Url,
    IReadOnlyList<string> Nodes,
    bool Answers,
    string? Version,
    string? Platform,
    string UpdateMethod,
    bool UpToDate,
    bool CanUpdate,
    string? BlockedReason,
    IReadOnlyList<OllamaManualStep> ManualSteps);

internal sealed record OllamaVersionsReport(IReadOnlyList<OllamaMachineStatus> Machines, OllamaReleaseInfo? Latest, bool PlanRunning, bool UpdateRunning);

internal sealed record OllamaUpdateStatus(
    string Id,
    string Url,
    IReadOnlyList<string> Nodes,
    string Method,
    string State,
    string Status,
    string? FromVersion,
    string TargetVersion,
    string? FinalVersion,
    string? Error,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc);

/// <summary>How long each part of an update may take. Tests shorten them.</summary>
internal sealed record OllamaUpdateTimings(
    TimeSpan VersionPoll,
    TimeSpan LocalVersionWait,
    TimeSpan RemoteVersionWait,
    TimeSpan ServeGrace,
    TimeSpan DownloadLimit,
    TimeSpan InstallerLimit,
    TimeSpan RemoteStepLimit)
{
    public static OllamaUpdateTimings Default { get; } = new(
        VersionPoll: TimeSpan.FromSeconds(2),
        LocalVersionWait: TimeSpan.FromMinutes(5),
        RemoteVersionWait: TimeSpan.FromMinutes(3),
        ServeGrace: TimeSpan.FromSeconds(30),
        DownloadLimit: TimeSpan.FromMinutes(45),
        InstallerLimit: TimeSpan.FromMinutes(10),
        RemoteStepLimit: TimeSpan.FromMinutes(2));
}

/// <summary>
/// Updates Ollama on fleet machines when the owner clicks Update in the web UI, one machine at a time, in the background.
/// Nothing here runs by itself: no timer, no start-up call. GitHub is asked only by <see cref="CheckLatestAsync"/> (the
/// Check for updates button), and a download or an install starts only from <see cref="StartAsync"/>. What is downloaded
/// is decided on this machine from the release data (a plain version number and a fixed list of file names), never
/// from the request, and nothing runs before the file's SHA-256 matches the release's.
/// </summary>
internal sealed class OllamaUpdateService
{
    public const string ReleaseClientName = "ollama-release-check";
    public const string DownloadClientName = "ollama-update-download";
    public const string Running = "running";
    public const string Queued = "queued";
    public const string Done = "done";
    public const string Failed = "failed";

    private const int MaxReleaseBytes = 2 * 1024 * 1024;
    private const long MaxDownloadBytes = 8L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _clients;
    private readonly Func<FleetConfig> _config;
    private readonly IOllamaVersionProbe _probe;
    private readonly IOllamaHostActions _host;
    private readonly IRemoteShellFactory _remote;
    private readonly Func<bool> _planRunning;
    private readonly Func<Uri, bool> _isThisMachine;
    private readonly ILogger _logger;
    private readonly OllamaUpdateTimings _timings;
    private readonly TimeProvider _clock;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private OllamaRelease? _release;
    private DateTimeOffset _checkedAtUtc;
    private bool _updating;

    public OllamaUpdateService(
        IHttpClientFactory clients,
        Func<FleetConfig> config,
        IOllamaVersionProbe probe,
        IOllamaHostActions host,
        IRemoteShellFactory remote,
        Func<bool> planRunning,
        ILogger logger,
        OllamaUpdateTimings? timings = null,
        Func<Uri, bool>? isThisMachine = null,
        TimeProvider? clock = null)
    {
        _clients = clients;
        _config = config;
        _probe = probe;
        _host = host;
        _remote = remote;
        _planRunning = planRunning;
        _logger = logger;
        _timings = timings ?? OllamaUpdateTimings.Default;
        _isThisMachine = isThisMachine ?? OllamaAddress.IsThisMachine;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised when an update finishes, successfully or not, so cached health can be refreshed.</summary>
    public event Action<OllamaUpdateStatus>? Finished;

    // ---- Reading ---------------------------------------------------------------------------------------------

    /// <summary>Every distinct Ollama address, what runs there, and what the web UI may do about it.</summary>
    public async Task<OllamaVersionsReport> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Endpoint> endpoints = Endpoints();
        string?[] versions = await Task.WhenAll(endpoints.Select(endpoint => _probe.GetVersionAsync(endpoint.Root, cancellationToken)));
        OllamaRelease? release = Latest(out DateTimeOffset checkedAt);
        bool planRunning = SafePlanRunning();

        List<OllamaMachineStatus> machines = [];
        for (int index = 0; index < endpoints.Count; index++)
        {
            machines.Add(Describe(endpoints[index], versions[index], release, planRunning));
        }

        return new OllamaVersionsReport(machines, release is null ? null : ToInfo(release, checkedAt), planRunning, IsUpdating);
    }

    public IReadOnlyList<OllamaUpdateStatus> List()
    {
        Prune();
        return _jobs.Values.Select(job => job.Snapshot()).OrderBy(status => status.StartedAtUtc).ThenBy(status => status.Url, StringComparer.Ordinal).ToList();
    }

    public bool IsUpdating
    {
        get
        {
            lock (_gate)
            {
                return _updating;
            }
        }
    }

    // ---- Checking for a newer release (the only call to GitHub) ----------------------------------------------

    /// <summary>Asks GitHub for the newest stable release and keeps the answer in memory. Called only by the owner's click.</summary>
    public async Task<OllamaReleaseInfo> CheckLatestAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReleaseTimeout);
        try
        {
            HttpClient client = _clients.CreateClient(ReleaseClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, OllamaUpdateAssets.LatestReleaseUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("agent-fleet", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                string hint = (int)response.StatusCode is 403 or 429 ? " (GitHub may be limiting requests; try again later)" : string.Empty;
                throw new OllamaUpdateException($"GitHub answered HTTP {(int)response.StatusCode}{hint}.", StatusCodes.Status502BadGateway);
            }

            OllamaRelease release = OllamaUpdateAssets.ParseRelease(await ReadLimitedAsync(response.Content, MaxReleaseBytes, timeout.Token));
            DateTimeOffset now = _clock.GetUtcNow();
            lock (_gate)
            {
                _release = release;
                _checkedAtUtc = now;
            }

            _logger.LogInformation("Checked GitHub for the newest stable Ollama release: {Version}.", release.Version);
            return ToInfo(release, now);
        }
        catch (OllamaReleaseException exception)
        {
            throw new OllamaUpdateException(exception.Message, StatusCodes.Status502BadGateway);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidDataException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new OllamaUpdateException($"Could not reach GitHub: {exception.Message}", StatusCodes.Status502BadGateway);
        }
    }

    // ---- Starting updates -------------------------------------------------------------------------------------

    /// <summary>
    /// Starts updating the machine at <paramref name="url"/>, or every machine that may be updated when it is null.
    /// Refuses when nothing was checked, a plan is running, another update is running, or the machine may not be updated.
    /// </summary>
    public async Task<IReadOnlyList<OllamaUpdateStatus>> StartAsync(string? url, CancellationToken cancellationToken)
    {
        OllamaRelease release = Latest(out _) ?? throw new OllamaUpdateException("Check for updates first: the newest release has not been looked up yet.");
        if (SafePlanRunning())
        {
            throw new OllamaUpdateException(PlanRunningMessage);
        }

        if (IsUpdating)
        {
            throw new OllamaUpdateException(UpdateRunningMessage);
        }

        IReadOnlyList<Endpoint> endpoints = Endpoints();
        if (url is not null)
        {
            if (!OllamaAddress.TryGetRoot(url, out Uri wanted))
            {
                throw new OllamaUpdateException("url must be the machine's address, for example http://192.0.2.20:11434.", StatusCodes.Status400BadRequest);
            }

            endpoints = endpoints.Where(endpoint => endpoint.Root.AbsoluteUri == wanted.AbsoluteUri).ToList();
            if (endpoints.Count == 0)
            {
                throw new OllamaUpdateException("That address is not one of the fleet's machines.", StatusCodes.Status400BadRequest);
            }
        }

        string?[] versions = await Task.WhenAll(endpoints.Select(endpoint => _probe.GetVersionAsync(endpoint.Root, cancellationToken)));
        List<Job> jobs = [];
        for (int index = 0; index < endpoints.Count; index++)
        {
            OllamaMachineStatus status = Describe(endpoints[index], versions[index], release, planRunning: false);
            if (!status.CanUpdate)
            {
                if (url is not null)
                {
                    throw new OllamaUpdateException(status.BlockedReason ?? "That machine cannot be updated from here.");
                }

                continue;
            }

            jobs.Add(Job.Begin(endpoints[index], status.UpdateMethod, versions[index]!, release, _clock.GetUtcNow()));
        }

        if (jobs.Count == 0)
        {
            throw new OllamaUpdateException("No machine needs an update that can be run from here.");
        }

        lock (_gate)
        {
            // Checked again under the lock: two clicks must not start two updates.
            if (_updating)
            {
                throw new OllamaUpdateException(UpdateRunningMessage);
            }

            _updating = true;
        }

        Prune();
        foreach (Job job in jobs)
        {
            _jobs[job.Id] = job;
        }

        _ = Task.Run(() => RunQueueAsync(jobs));
        return jobs.Select(job => job.Snapshot()).ToList();
    }

    internal const string PlanRunningMessage = "A plan is running or approved. Wait until it has finished before updating Ollama: an update restarts it and interrupts model use.";
    internal const string UpdateRunningMessage = "An Ollama update is already running. Wait for it to finish.";

    private async Task RunQueueAsync(IReadOnlyList<Job> jobs)
    {
        try
        {
            foreach (Job job in jobs)
            {
                if (SafePlanRunning())
                {
                    job.Fail("A plan started in the meantime, so this machine was not updated.");
                    Announce(job);
                    continue;
                }

                await RunOneAsync(job);
            }
        }
        finally
        {
            lock (_gate)
            {
                _updating = false;
            }
        }
    }

    private async Task RunOneAsync(Job job)
    {
        job.MarkRunning();
        string machine = $"{string.Join(", ", job.Nodes)} ({job.Root.Host}:{job.Root.Port})";
        _logger.LogInformation("Ollama update started on {Machine}: {From} to {To} by {Method}.", machine, job.FromVersion, job.Release.Version, job.Method);
        try
        {
            string final = job.Method switch
            {
                FleetOllamaUpdateConfig.Local => await UpdateThisMachineAsync(job, CancellationToken.None),
                FleetOllamaUpdateConfig.SshLinux => await UpdateOverSshAsync(job, CancellationToken.None),
                _ => throw new OllamaUpdateException("This machine has no update method.")
            };
            job.Succeed(final);
        }
        catch (OllamaUpdateException exception)
        {
            job.Fail(exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException
            or OperationCanceledException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or Renci.SshNet.Common.SshException)
        {
            job.Fail($"The update stopped: {exception.Message}");
        }
        catch (Exception exception)
        {
            // Whatever else goes wrong, the job must end as failed: a job left "running" would block every later update.
            _logger.LogError(exception, "The Ollama update on {Machine} stopped unexpectedly.", machine);
            job.Fail($"The update stopped unexpectedly: {exception.Message}");
        }
        finally
        {
            OllamaUpdateStatus final = job.Snapshot();
            _logger.LogInformation("Ollama update on {Machine}: {State}, {From} to {To}, now {Final}{Error}.",
                machine, final.State, final.FromVersion, final.TargetVersion, final.FinalVersion ?? "unknown",
                final.Error is null ? string.Empty : $" ({final.Error})");
            Announce(job);
        }
    }

    private void Announce(Job job)
    {
        try
        {
            Finished?.Invoke(job.Snapshot());
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A listener of the finished update failed.");
        }
    }

    // ---- The machine this backend runs on (Windows) ---------------------------------------------------------

    private async Task<string> UpdateThisMachineAsync(Job job, CancellationToken cancellationToken)
    {
        if (!_host.IsWindows)
        {
            throw new OllamaUpdateException("Updating the machine the fleet runs on is supported on Windows only. Use the manual steps for this machine.");
        }

        if (_host.IsWindowsService)
        {
            throw new OllamaUpdateException("The fleet runs as a Windows service, whose account is not yours, so an installer would put Ollama in the wrong profile. Run the fleet as a scheduled task, or update Ollama by hand.");
        }

        if (!_isThisMachine(job.Root))
        {
            throw new OllamaUpdateException("The update method \"local\" only works for the machine the fleet itself runs on.");
        }

        (OllamaReleaseAsset asset, Uri url) = Verified(job.Release, OllamaUpdateAssets.WindowsInstaller);
        DirectoryInfo folder = Directory.CreateTempSubdirectory("agentfleet-ollama-update-");
        try
        {
            string installer = Path.Combine(folder.FullName, OllamaUpdateAssets.WindowsInstaller);
            job.Report($"Downloading {asset.Name}");
            string actual = await DownloadAsync(url, installer, cancellationToken);
            if (!string.Equals(actual, OllamaUpdateAssets.Hex(asset.Digest!), StringComparison.Ordinal))
            {
                throw new OllamaUpdateException("The downloaded installer's SHA-256 does not match the one GitHub lists for the release, so it was not run. Nothing on this machine was changed.");
            }

            job.Report("Stopping Ollama");
            _host.StopProcesses(["ollama app", "ollama"]);

            job.Report("Running the installer");
            int exit = await _host.RunInstallerAsync(installer, ["/SILENT", "/NORESTART"], _timings.InstallerLimit, cancellationToken);
            string? installerError = exit switch
            {
                0 => null,
                -1 => $"The installer did not finish within {_timings.InstallerLimit.TotalMinutes:0} minutes and was ended.",
                _ => $"The installer exited with code {exit}."
            };

            job.Report($"Waiting for Ollama to report {job.Release.Version}");
            (string? matched, string? lastSeen) = await WaitForVersionAsync(job.Root, job.Release.Version, installerError is null ? _timings.LocalVersionWait : _timings.ServeGrace * 2, startServe: true, cancellationToken);
            if (installerError is not null)
            {
                throw new OllamaUpdateException($"{installerError} Ollama {(lastSeen is null ? "is not answering" : $"is running version {lastSeen}")}.");
            }

            return matched ?? throw new OllamaUpdateException(
                $"Ollama {(lastSeen is null ? "did not answer" : $"still reports version {lastSeen}")} and not {job.Release.Version}, {_timings.LocalVersionWait.TotalMinutes:0} minutes after the installer finished.");
        }
        finally
        {
            DeleteQuietly(folder);
        }
    }

    // ---- A Linux machine over SSH -----------------------------------------------------------------------------

    private async Task<string> UpdateOverSshAsync(Job job, CancellationToken cancellationToken)
    {
        FleetOllamaUpdateConfig config = job.UpdateConfig ?? throw new OllamaUpdateException("This machine has no SSH update account configured.");
        (OllamaReleaseAsset asset, Uri url) = Verified(job.Release, OllamaUpdateAssets.LinuxAmd64);

        using IRemoteShell shell = await _remote.OpenAsync(config, cancellationToken);
        job.Report("Checking the machine");
        await RequireAsync(shell, OllamaLinuxUpdate.CheckSudo, "sudo -n true", "The update account cannot use sudo without a password. Allow it the update commands in sudoers (see the Ollama updates section of the configuration guide).", cancellationToken);
        await RequireAsync(shell, OllamaLinuxUpdate.CheckCurl, "curl", "curl is not installed on that machine.", cancellationToken);
        await RequireAsync(shell, OllamaLinuxUpdate.CheckZstd, "zstd", "zstd is not installed on that machine (it is in the zstd package).", cancellationToken);

        RemoteResult home = await shell.RunAsync(OllamaLinuxUpdate.ReadHome, _timings.RemoteStepLimit, cancellationToken);
        if (home.ExitCode != 0 || !OllamaLinuxUpdate.TryWorkFolder(home.Output, out string folder))
        {
            throw new OllamaUpdateException("The update account's home folder could not be read, or its name has characters an update does not accept.");
        }

        string file = OllamaLinuxUpdate.FilePath(folder);
        string[] script = OllamaLinuxUpdate.Commands(folder, url, OllamaUpdateAssets.Hex(asset.Digest!));
        bool stopped = false;
        try
        {
            job.Report("Preparing a download folder");
            await RequireAsync(shell, script[OllamaLinuxUpdate.MakeFolder], "preparing the download folder", null, cancellationToken);
            job.Report($"Downloading {asset.Name}");
            await RequireAsync(shell, script[OllamaLinuxUpdate.Download], "the download", null, cancellationToken, _timings.DownloadLimit);
            job.Report("Verifying the download");
            await RequireAsync(shell, script[OllamaLinuxUpdate.Verify], "the SHA-256 check", "The SHA-256 check of the downloaded file failed: it does not match the one GitHub lists for the release, or sha256sum is not available. Nothing on that machine was changed.", cancellationToken, _timings.InstallerLimit);

            stopped = true;
            job.Report("Stopping Ollama");
            await RequireAsync(shell, script[OllamaLinuxUpdate.Stop], "stopping Ollama", null, cancellationToken);
            job.Report("Replacing Ollama's files");
            await RequireAsync(shell, script[OllamaLinuxUpdate.RemoveOld], "removing the old files", null, cancellationToken);
            await RequireAsync(shell, script[OllamaLinuxUpdate.Extract], "unpacking the new files", null, cancellationToken, _timings.InstallerLimit);
            job.Report("Starting Ollama");
            await RequireAsync(shell, script[OllamaLinuxUpdate.Start], "starting Ollama", null, cancellationToken);
            stopped = false;

            job.Report($"Waiting for Ollama to report {job.Release.Version}");
            (string? matched, string? lastSeen) = await WaitForVersionAsync(job.Root, job.Release.Version, _timings.RemoteVersionWait, startServe: false, cancellationToken);
            return matched ?? throw new OllamaUpdateException(
                $"Ollama was started again but {(lastSeen is null ? "does not answer" : $"still reports version {lastSeen}")} and not {job.Release.Version}, {_timings.RemoteVersionWait.TotalMinutes:0} minutes later.");
        }
        catch (Exception exception) when (stopped && exception is OllamaUpdateException or IOException or Renci.SshNet.Common.SshException)
        {
            // Ollama was stopped for the update; leave the machine serving rather than down, and say what went wrong first.
            RemoteResult restart = await TryAsync(shell, script[OllamaLinuxUpdate.Start], cancellationToken);
            throw new OllamaUpdateException(restart.ExitCode == 0
                ? $"{exception.Message} Ollama was started again, but it may not be running the expected version."
                : $"{exception.Message} Starting Ollama again also failed, so it may be stopped: run 'sudo systemctl start ollama' on that machine.");
        }
        finally
        {
            await TryAsync(shell, OllamaLinuxUpdate.Remove(file), CancellationToken.None);
        }
    }

    private async Task<RemoteResult> TryAsync(IRemoteShell shell, string command, CancellationToken cancellationToken)
    {
        try
        {
            return await shell.RunAsync(command, _timings.RemoteStepLimit, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
        {
            return new RemoteResult(-1, string.Empty, exception.Message);
        }
    }

    private async Task RequireAsync(IRemoteShell shell, string command, string what, string? explanation, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        RemoteResult result = await shell.RunAsync(command, limit ?? _timings.RemoteStepLimit, cancellationToken);
        if (result.ExitCode == 0)
        {
            return;
        }

        string detail = FirstLine(result.Error.Length > 0 ? result.Error : result.Output);
        throw new OllamaUpdateException(explanation is not null
            ? $"{explanation}{(detail.Length > 0 ? $" ({detail})" : string.Empty)}"
            : $"Failed at {what} (exit {result.ExitCode}){(detail.Length > 0 ? $": {detail}" : ".")}");
    }

    // ---- Shared steps -----------------------------------------------------------------------------------------

    private static (OllamaReleaseAsset Asset, Uri Url) Verified(OllamaRelease release, string assetName)
    {
        if (!OllamaUpdateAssets.TryDownloadUrl(release.Version, assetName, out Uri url))
        {
            throw new OllamaUpdateException("The release's version or file name is not one an update accepts, so nothing was downloaded.");
        }

        OllamaReleaseAsset? asset = release.Asset(assetName);
        if (asset is null)
        {
            throw new OllamaUpdateException($"The release does not list {assetName}, so there is nothing to install for this machine.");
        }

        if (asset.Digest is null)
        {
            throw new OllamaUpdateException($"GitHub lists no SHA-256 for {assetName}, so it cannot be verified and was not downloaded.");
        }

        return (asset, url);
    }

    /// <summary>Polls the machine's version until it is <paramref name="target"/> or <paramref name="total"/> passes. Matched is the version once it is the target; LastSeen is the last version that answered, if any.</summary>
    private async Task<(string? Matched, string? LastSeen)> WaitForVersionAsync(Uri root, string target, TimeSpan total, bool startServe, CancellationToken cancellationToken)
    {
        long started = _clock.GetTimestamp();
        bool serveStarted = false;
        string? lastSeen = null;
        OllamaVersion.TryParse(target, out OllamaVersion wanted);
        while (true)
        {
            string? version = await _probe.GetVersionAsync(root, cancellationToken);
            TimeSpan elapsed = _clock.GetElapsedTime(started);
            if (version is not null)
            {
                lastSeen = version;
                if (OllamaVersion.TryParse(version, out OllamaVersion current) && current.CompareTo(wanted) == 0)
                {
                    return (version, version);
                }
            }
            else if (startServe && !serveStarted && elapsed >= _timings.ServeGrace && _host.OllamaExecutable() is { } executable)
            {
                // The installer relaunches Ollama's app, which starts the server. When nothing answers after a grace
                // period, start the server directly rather than leave the machine without one.
                serveStarted = true;
                _host.StartServeHidden(executable);
            }

            if (elapsed >= total)
            {
                return (null, lastSeen);
            }

            await Task.Delay(_timings.VersionPoll, _clock, cancellationToken);
        }
    }

    private async Task<string> DownloadAsync(Uri url, string path, CancellationToken cancellationToken)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timings.DownloadLimit);
        try
        {
            HttpClient client = _clients.CreateClient(DownloadClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("agent-fleet", "1"));
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new OllamaUpdateException($"The download failed: HTTP {(int)response.StatusCode}. Nothing on this machine was changed.");
            }

            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                throw new OllamaUpdateException("The download is larger than an Ollama installer should be, so it was refused.");
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream source = await response.Content.ReadAsStreamAsync(limit.Token);
            await using FileStream target = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, limit.Token)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                {
                    throw new OllamaUpdateException("The download is larger than an Ollama installer should be, so it was stopped.");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), limit.Token);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OllamaUpdateException($"The download did not finish within {_timings.DownloadLimit.TotalMinutes:0} minutes. Nothing on this machine was changed.");
        }
        catch (HttpRequestException exception)
        {
            throw new OllamaUpdateException($"The download failed: {exception.Message}. Nothing on this machine was changed.");
        }
    }

    internal static async Task<string> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("The answer was larger than expected and was not read.");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private void DeleteQuietly(DirectoryInfo folder)
    {
        try
        {
            folder.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The temporary update folder could not be removed: {Reason}", exception.Message);
        }
    }

    private static string FirstLine(string text)
    {
        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length <= 200 ? line : line[..200] + "...";
    }

    private bool SafePlanRunning()
    {
        try
        {
            return _planRunning();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // If the plans cannot be read, treat one as running: an update must not interrupt work that cannot be seen.
            return true;
        }
    }

    private OllamaRelease? Latest(out DateTimeOffset checkedAt)
    {
        lock (_gate)
        {
            checkedAt = _checkedAtUtc;
            return _release;
        }
    }

    private void Prune()
    {
        DateTimeOffset cutoff = _clock.GetUtcNow() - KeepFinished;
        foreach ((string id, Job job) in _jobs)
        {
            if (job.State is Done or Failed && job.FinishedAtUtc < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }

    // ---- Describing machines ----------------------------------------------------------------------------------

    private sealed record Endpoint(Uri Root, IReadOnlyList<FleetNodeConfig> Nodes)
    {
        /// <summary>The first machine at this address that has an update method.</summary>
        public FleetNodeConfig? Updatable => Nodes.FirstOrDefault(node => node.OllamaUpdate?.CanUpdate == true);
    }

    /// <summary>The configured machines grouped by their Ollama address. A vision machine that is turned off is never contacted, so it is left out.</summary>
    private IReadOnlyList<Endpoint> Endpoints()
    {
        var groups = new Dictionary<string, List<FleetNodeConfig>>(StringComparer.Ordinal);
        var roots = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (FleetNodeConfig node in _config().Nodes)
        {
            if (node.Vision && NodeVisionMode.Normalize(node.VisionMode) == NodeVisionMode.Off)
            {
                continue;
            }

            if (!OllamaAddress.TryGetRoot(node.Url, out Uri root))
            {
                continue;
            }

            if (!groups.TryGetValue(root.AbsoluteUri, out List<FleetNodeConfig>? list))
            {
                groups[root.AbsoluteUri] = list = [];
                roots[root.AbsoluteUri] = root;
            }

            list.Add(node);
        }

        return groups.Select(pair => new Endpoint(roots[pair.Key], pair.Value)).ToList();
    }

    private OllamaMachineStatus Describe(Endpoint endpoint, string? version, OllamaRelease? release, bool planRunning)
    {
        FleetNodeConfig? updatable = endpoint.Updatable;
        string method = updatable?.OllamaUpdate?.EffectiveMethod ?? FleetOllamaUpdateConfig.None;
        string? platform = PlatformOf(endpoint, method);
        bool answers = version is not null;
        bool older = OllamaVersion.IsOlder(version, release?.Version);
        bool upToDate = release is not null && answers && OllamaVersion.TryParse(version, out OllamaVersion current) &&
            OllamaVersion.TryParse(release.Version, out OllamaVersion latest) && current.CompareTo(latest) >= 0;

        string? blocked = null;
        if (release is null)
        {
            blocked = "Check for updates first.";
        }
        else if (!answers)
        {
            blocked = "This machine does not answer, so its version is unknown.";
        }
        else if (upToDate)
        {
            blocked = "Already up to date.";
        }
        else if (!older)
        {
            blocked = "Its version could not be compared with the release.";
        }
        else if (method == FleetOllamaUpdateConfig.None)
        {
            blocked = "No update method is configured for this machine. Use the manual steps below.";
        }
        else if (method == FleetOllamaUpdateConfig.Local && !_isThisMachine(endpoint.Root))
        {
            blocked = "The method \"local\" only works for the machine the fleet runs on.";
        }
        else if (method == FleetOllamaUpdateConfig.Local && !_host.IsWindows)
        {
            blocked = "Updating this machine from here is supported on Windows only. Use the manual steps below.";
        }
        else if (method == FleetOllamaUpdateConfig.Local && _host.IsWindowsService)
        {
            blocked = "The fleet runs as a Windows service, so it cannot run Ollama's installer for your account.";
        }
        else if (release.Asset(method == FleetOllamaUpdateConfig.Local ? OllamaUpdateAssets.WindowsInstaller : OllamaUpdateAssets.LinuxAmd64) is not { Digest: not null })
        {
            blocked = "The release lists no SHA-256 for this machine's file, so it cannot be verified.";
        }
        else if (planRunning)
        {
            blocked = PlanRunningMessage;
        }
        else if (IsUpdating)
        {
            blocked = UpdateRunningMessage;
        }

        bool manual = release is not null && answers && older && method == FleetOllamaUpdateConfig.None;
        return new OllamaMachineStatus(
            endpoint.Root.AbsoluteUri,
            endpoint.Nodes.Select(node => node.Name).ToList(),
            answers,
            version,
            platform,
            method,
            upToDate,
            blocked is null,
            blocked,
            manual ? ManualSteps(release!, platform) : []);
    }

    private string? PlatformOf(Endpoint endpoint, string method)
    {
        if (method == FleetOllamaUpdateConfig.SshLinux)
        {
            return "linux";
        }

        if (endpoint.Nodes.Select(node => node.Workspace?.Platform).FirstOrDefault(platform => platform is "linux" or "windows") is { } known)
        {
            return known;
        }

        if (_isThisMachine(endpoint.Root))
        {
            return OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : null;
        }

        return null;
    }

    /// <summary>The verified download, its SHA-256 and the install commands for the machine's platform, or for every platform when it is not known.</summary>
    internal static IReadOnlyList<OllamaManualStep> ManualSteps(OllamaRelease release, string? platform)
    {
        List<OllamaManualStep> steps = [];
        void Add(string name, string asset, Func<Uri, string?, IReadOnlyList<string>> commands, string note)
        {
            if (platform is not null && platform != name)
            {
                return;
            }

            if (!OllamaUpdateAssets.TryDownloadUrl(release.Version, asset, out Uri url))
            {
                return;
            }

            string? sha = release.Asset(asset)?.Digest is { } digest ? OllamaUpdateAssets.Hex(digest) : null;
            steps.Add(new OllamaManualStep(name, asset, url.AbsoluteUri, sha, commands(url, sha), note));
        }

        Add("windows", OllamaUpdateAssets.WindowsInstaller,
            (_, _) => [$".\\{OllamaUpdateAssets.WindowsInstaller} /SILENT /NORESTART"],
            "Compare the SHA-256 of the download before running it (Get-FileHash).");
        Add("linux", OllamaUpdateAssets.LinuxAmd64,
            (url, sha) => OllamaLinuxUpdate.ManualCommands(url, sha),
            "Needs curl, zstd and sudo.");
        Add("macos", OllamaUpdateAssets.MacApp,
            (_, _) => [],
            "Open the download and move Ollama to Applications, replacing the old one. Compare the SHA-256 first (shasum -a 256).");
        return steps;
    }

    private static OllamaReleaseInfo ToInfo(OllamaRelease release, DateTimeOffset checkedAt) =>
        new(
            release.Version,
            release.PublishedAtUtc,
            checkedAt,
            OllamaUpdateAssets.Allowed
                .Select(name => (Name: name, Asset: release.Asset(name)))
                .Where(entry => entry.Asset is not null && OllamaUpdateAssets.TryDownloadUrl(release.Version, entry.Name, out _))
                .Select(entry =>
                {
                    OllamaUpdateAssets.TryDownloadUrl(release.Version, entry.Name, out Uri url);
                    return new OllamaAssetInfo(entry.Name, url.AbsoluteUri, entry.Asset!.Digest is { } digest ? OllamaUpdateAssets.Hex(digest) : null);
                })
                .ToList());

    // ---- A job -----------------------------------------------------------------------------------------------

    private sealed class Job
    {
        private readonly object _gate = new();

        private Job(string id, Endpoint endpoint, string method, string fromVersion, OllamaRelease release, DateTimeOffset queuedAt)
        {
            Id = id;
            Root = endpoint.Root;
            Nodes = endpoint.Nodes.Select(node => node.Name).ToList();
            Method = method;
            FromVersion = fromVersion;
            Release = release;
            UpdateConfig = endpoint.Updatable?.OllamaUpdate;
            StartedAtUtc = queuedAt;
        }

        public string Id { get; }

        public Uri Root { get; }

        public IReadOnlyList<string> Nodes { get; }

        public string Method { get; }

        public string FromVersion { get; }

        public OllamaRelease Release { get; }

        public FleetOllamaUpdateConfig? UpdateConfig { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public string State { get; private set; } = Queued;

        public string Status { get; private set; } = "waiting for its turn";

        public string? FinalVersion { get; private set; }

        public string? Error { get; private set; }

        public DateTimeOffset? FinishedAtUtc { get; private set; }

        public static Job Begin(Endpoint endpoint, string method, string fromVersion, OllamaRelease release, DateTimeOffset queuedAt) =>
            new(Guid.NewGuid().ToString("N")[..16], endpoint, method, fromVersion, release, queuedAt);

        public void MarkRunning()
        {
            lock (_gate)
            {
                State = Running;
                Status = "starting";
            }
        }

        public void Report(string status)
        {
            lock (_gate)
            {
                Status = status;
            }
        }

        public void Succeed(string finalVersion)
        {
            lock (_gate)
            {
                State = Done;
                Status = $"updated to {finalVersion}";
                FinalVersion = finalVersion;
                FinishedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public void Fail(string error)
        {
            lock (_gate)
            {
                State = Failed;
                Status = "failed";
                Error = error;
                FinishedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public OllamaUpdateStatus Snapshot()
        {
            lock (_gate)
            {
                return new OllamaUpdateStatus(Id, Root.AbsoluteUri, Nodes, Method, State, Status, FromVersion, Release.Version, FinalVersion, Error, StartedAtUtc, FinishedAtUtc);
            }
        }

    }
}

/// <summary>
/// The commands that update Ollama on a Linux machine, as text. Every value put into a command has been checked against a
/// strict pattern first (a plain version, 64 hex digits, a plain folder name), so nothing in a command can come from a request.
/// The files go in a private folder of the update account, so the sudo rules can name the exact file.
/// </summary>
internal static partial class OllamaLinuxUpdate
{
    public const string CheckSudo = "sudo -n true";
    public const string CheckCurl = "command -v curl";
    public const string CheckZstd = "command -v zstd";
    public const string ReadHome = "printf '%s' \"$HOME\"";

    // Positions in the list Commands returns.
    public const int MakeFolder = 0;
    public const int Download = 1;
    public const int Verify = 2;
    public const int Stop = 3;
    public const int RemoveOld = 4;
    public const int Extract = 5;
    public const int Start = 6;

    private const string FolderName = ".ollama-update";

    [GeneratedRegex(@"^/[A-Za-z0-9._/-]{1,200}\z")]
    private static partial Regex HomePattern();

    /// <summary>The update account's private folder under its home, when the home is a plain path.</summary>
    public static bool TryWorkFolder(string home, out string folder)
    {
        folder = string.Empty;
        string trimmed = home.Trim();
        if (!HomePattern().IsMatch(trimmed) || trimmed.Contains("..", StringComparison.Ordinal) || trimmed.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        folder = trimmed.TrimEnd('/') + "/" + FolderName;
        return true;
    }

    public static string FilePath(string folder) => $"{folder}/{OllamaUpdateAssets.LinuxAmd64}";

    public static string Remove(string file) => $"rm -f '{file}'";

    /// <summary>The steps, in order, with the positions named above. <paramref name="sha256"/> is 64 hex digits and <paramref name="url"/> a vetted download address.</summary>
    public static string[] Commands(string folder, Uri url, string sha256)
    {
        string file = FilePath(folder);
        return
        [
            $"mkdir -p -m 700 '{folder}'",
            $"curl -fsSL -o '{file}' '{url.AbsoluteUri}'",
            $"echo '{sha256}  {file}' | sha256sum -c -",
            "sudo -n systemctl stop ollama",
            "sudo -n rm -rf /usr/lib/ollama",
            $"sudo -n tar -I zstd -xf '{file}' -C /usr",
            "sudo -n systemctl start ollama"
        ];
    }

    /// <summary>The same steps for a person at a terminal: the download in the current folder, no private folder, plain sudo.</summary>
    public static IReadOnlyList<string> ManualCommands(Uri url, string? sha256)
    {
        string name = OllamaUpdateAssets.LinuxAmd64;
        List<string> commands = [$"curl -fsSL -o {name} {url.AbsoluteUri}"];
        if (sha256 is not null)
        {
            commands.Add($"echo '{sha256}  {name}' | sha256sum -c -");
        }

        commands.AddRange(
        [
            "sudo systemctl stop ollama",
            "sudo rm -rf /usr/lib/ollama",
            $"sudo tar -I zstd -xf {name} -C /usr",
            "sudo systemctl start ollama"
        ]);
        return commands;
    }
}
