using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

internal sealed record FitProbeSizeResult(
    int Context,
    string Status,
    double? GpuShare,
    double? TokensPerSecond,
    double? LoadSeconds,
    string? Detail,
    DateTimeOffset CompletedAtUtc,
    bool Reused = false);

internal sealed record FitProbeJobSnapshot(
    string Id,
    string Node,
    string Model,
    string State,
    int? CurrentContext,
    IReadOnlyList<FitProbeSizeResult> Results,
    FitRecommendation Recommendation,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? Reason);

internal sealed record FitProbeStartResult(FitProbeJobSnapshot? Job, string? Reason);

internal sealed record FitProbeLatestResult(
    string Node,
    string Model,
    IReadOnlyList<FitProbeSizeResult> Results,
    FitRecommendation Recommendation,
    DateTimeOffset MeasuredAtUtc,
    bool Stale,
    string? StaleReason);

internal sealed record FitProbeHistoryEntry(
    string JobId,
    DateTimeOffset WhenUtc,
    string Address,
    string Node,
    string Model,
    string? Digest,
    string? Version,
    int Context,
    string Status,
    double? GpuShare,
    double? TokensPerSecond,
    double? LoadSeconds,
    string? Detail);

internal sealed class FitProbeService
{
    private static readonly int[] CandidateContexts = [12288, 16384, 24576, 32768];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    private readonly FleetOptions _options;
    private readonly FleetPlanStore _plans;
    private readonly FleetConfigStore _config;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger _logger;
    private readonly MeasuringMachines _measuringMachines;
    private readonly string _labDirectory;
    private readonly string _historyPath;
    private readonly TimeSpan _sizeCap;
    private readonly TimeSpan _jobCap;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyGate = new();

    public FitProbeService(
        FleetOptions options,
        FleetPlanStore plans,
        FleetConfigStore config,
        IHttpClientFactory clients,
        IConfiguration configuration,
        ILogger<FitProbeService> logger,
        MeasuringMachines measuringMachines,
        TimeSpan? sizeCap = null,
        TimeSpan? jobCap = null)
    {
        _options = options;
        _plans = plans;
        _config = config;
        _clients = clients;
        _logger = logger;
        _measuringMachines = measuringMachines;
        _sizeCap = sizeCap ?? TimeSpan.FromMinutes(5);
        _jobCap = jobCap ?? TimeSpan.FromMinutes(20);
        string plansDirectory = Path.GetFullPath(configuration["FLEET_PLANS_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "plans"));
        string defaultLabDirectory = Path.Combine(Path.GetDirectoryName(plansDirectory) ?? AppContext.BaseDirectory, "lab");
        string labDirectory = Path.GetFullPath(configuration["FLEET_LAB_DIR"] ?? defaultLabDirectory);
        Directory.CreateDirectory(labDirectory);
        _labDirectory = labDirectory;
        _historyPath = Path.Combine(labDirectory, "fit-history.jsonl");
        LoadInterruptedJobs();
    }

    public event Action<FitProbeJobSnapshot>? Finished;

    internal string LabDirectory => _labDirectory;

    public FitProbeStartResult Start(string? requestedNode, bool force = false, bool confirmHub = false)
    {
        string name = requestedNode?.Trim() ?? string.Empty;
        if (!_options.TryGetNode(name, out FleetNodeDefinition? node))
        {
            return Refused("That machine is not in the saved settings.");
        }

        if (string.Equals(node.Api, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return Refused("This machine uses a compatible API, so context fit cannot be measured here.");
        }

        if (node.Vision)
        {
            return Refused("This vision machine is not available for a context-fit measurement.");
        }

        Uri root = node.OllamaRoot;
        if (OllamaAddress.IsThisMachine(root) && !confirmHub)
        {
            return Refused("Measuring this computer needs confirmation.");
        }

        if (_plans.List().Any(plan => plan.Status is PlanStatus.Running or PlanStatus.Approved))
        {
            return Refused("A plan is running or approved. Wait until it has finished before measuring.");
        }

        string address = NormalizeAddress(root);
        lock (_gate)
        {
            if (_activeAddresses.Contains(address))
            {
                return Refused("A measurement is already running on this machine.");
            }

            Job job = new(Guid.NewGuid().ToString("N"), node.Name, node.Model, root, address, force, Thresholds());
            WriteJobMarker(job);
            _jobs.Add(job.Id, job);
            _activeAddresses.Add(address);
            PruneJobs();
            _ = Task.Run(() => RunAsync(job));
            return new FitProbeStartResult(job.Snapshot(), null);
        }
    }

    public IReadOnlyList<FitProbeJobSnapshot> List()
    {
        lock (_gate)
        {
            PruneJobs();
            return _jobs.Values
                .Select(job => job.Snapshot())
                .OrderByDescending(job => job.StartedAtUtc)
                .Take(20)
                .ToArray();
        }
    }

    public bool Cancel(string id)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(id, out Job? job) && job.RequestCancel("Stopped by request.");
        }
    }

    public int CancelAll(string reason)
    {
        lock (_gate)
        {
            int cancelled = 0;
            foreach (Job job in _jobs.Values)
            {
                if (job.RequestCancel(reason)) cancelled++;
            }

            return cancelled;
        }
    }

    public async Task<FitProbeLatestResult?> LatestAsync(string? requestedNode, CancellationToken cancellationToken)
    {
        string name = requestedNode?.Trim() ?? string.Empty;
        if (!_options.TryGetNode(name, out FleetNodeDefinition? node)) return null;

        FitProbeHistoryEntry[] history = ReadHistory()
            .Where(entry => string.Equals(entry.Node, node.Name, StringComparison.Ordinal))
            .ToArray();
        FitProbeHistoryEntry? newest = history.OrderByDescending(entry => entry.WhenUtc).FirstOrDefault();
        if (newest is null) return null;

        FitProbeHistoryEntry[] run = history
            .Where(entry => entry.JobId == newest.JobId)
            .OrderBy(entry => entry.Context)
            .ToArray();
        string? staleReason = null;
        if (!string.Equals(node.Model, newest.Model, StringComparison.Ordinal))
        {
            staleReason = "The model changed since this was measured.";
        }
        else
        {
            Uri root = node.OllamaRoot;
            (string? digest, string? version) = await ReadIdentityAsync(_clients.CreateClient(FleetHealthMonitor.HttpClientName), root, node.Model, cancellationToken);
            if (newest.Digest is not null && digest is not null && !string.Equals(newest.Digest, digest, StringComparison.Ordinal))
            {
                staleReason = "The model data changed since this was measured.";
            }
            else if (newest.Version is not null && version is not null && !string.Equals(newest.Version, version, StringComparison.Ordinal))
            {
                staleReason = "The server was updated since this was measured.";
            }
        }

        FitPoint[] points = run.Select(ToPoint).ToArray();
        return new FitProbeLatestResult(
            node.Name,
            newest.Model,
            run.Select(entry => ToResult(entry)).ToArray(),
            FitProbe.Recommend(points, Thresholds()),
            newest.WhenUtc,
            staleReason is not null,
            staleReason);
    }

    private async Task RunAsync(Job job)
    {
        string finalState = "complete";
        string? finalReason = null;
        bool measuring = false;
        using CancellationTokenSource jobLimit = new();
        jobLimit.CancelAfter(_jobCap);
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, jobLimit.Token);
        HttpClient client = _clients.CreateClient(FleetHealthMonitor.HttpClientName);
        client.Timeout = Timeout.InfiniteTimeSpan;
        try
        {
            (string? digest, string? version) = await ReadIdentityAsync(client, job.Root, job.Model, work.Token);
            int? maximum = await ReadModelMaximumAsync(client, job.Root, job.Model, work.Token);
            int[] contexts = CandidateContexts.Where(context => maximum is null || context <= maximum.Value).ToArray();
            _measuringMachines.Begin(job.Root);
            measuring = true;
            FitProbe probe = new(client);
            FitProbeHistoryEntry[] history = ReadHistory()
                .Where(entry => entry.Node == job.Node && entry.Model == job.Model)
                .ToArray();

            foreach (int context in contexts)
            {
                work.Token.ThrowIfCancellationRequested();
                job.SetCurrent(context);
                FitProbeHistoryEntry? previous = history
                    .Where(entry => entry.Context == context)
                    .OrderByDescending(entry => entry.WhenUtc)
                    .FirstOrDefault();
                if (!job.Force && IsFresh(previous, digest, version))
                {
                    job.AddResult(ToResult(previous!) with { Reused = true });
                    continue;
                }

                using CancellationTokenSource sizeLimit = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
                sizeLimit.CancelAfter(_sizeCap);
                FitPoint point;
                try
                {
                    point = await probe.MeasureAsync(job.Root, job.Model, context, sizeLimit.Token);
                }
                catch (OperationCanceledException) when (sizeLimit.IsCancellationRequested && !work.IsCancellationRequested)
                {
                    point = new FitPoint(context, FitStatus.Error, null, null, null, "timed out");
                }

                FitProbeHistoryEntry entry = new(
                    job.Id,
                    DateTimeOffset.UtcNow,
                    HostPort(job.Root),
                    job.Node,
                    job.Model,
                    digest,
                    version,
                    context,
                    StatusName(point.Status),
                    point.GpuShare,
                    point.TokensPerSecond,
                    point.LoadSeconds,
                    point.Detail);
                job.AddResult(ToResult(entry));
                AppendHistory(entry);
            }
        }
        catch (OperationCanceledException)
        {
            if (job.CancelReason is { } reason)
            {
                finalState = "cancelled";
                finalReason = reason;
            }
            else if (jobLimit.IsCancellationRequested)
            {
                finalState = "incomplete";
                finalReason = "The measurement reached its 20 minute limit.";
            }
            else
            {
                finalState = "cancelled";
                finalReason = "The measurement was stopped.";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            finalState = "failed";
            finalReason = exception.Message;
            _logger.LogWarning(exception, "A fit measurement for {Node} stopped unexpectedly.", job.Node);
        }
        finally
        {
            if (measuring)
            {
                _measuringMachines.End(job.Root);
            }

            if (job.CancelReason is { } reason)
            {
                finalState = "cancelled";
                finalReason = reason;
            }
            else if (jobLimit.IsCancellationRequested && finalState == "complete")
            {
                finalState = "incomplete";
                finalReason = "The measurement reached its 20 minute limit.";
            }

            job.Finish(finalState, finalReason);
            DeleteJobMarker(job.Id);
            lock (_gate)
            {
                _activeAddresses.Remove(job.Address);
            }

            FitProbeJobSnapshot snapshot = job.Snapshot();
            try
            {
                Finished?.Invoke(snapshot);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A fit measurement completion listener failed.");
            }
        }
    }

    private async Task<int?> ReadModelMaximumAsync(HttpClient client, Uri root, string model, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MetadataTimeout);
            using HttpResponseMessage response = await client.PostAsJsonAsync(OllamaAddress.Endpoint(root, "api/show"), new { model }, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using JsonDocument show = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return ContextSizeChatClient.MaxContext(show.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(string? Digest, string? Version)> ReadIdentityAsync(HttpClient client, Uri root, string model, CancellationToken cancellationToken)
    {
        string? digest = null;
        string? version = null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            using HttpResponseMessage response = await client.GetAsync(OllamaAddress.Endpoint(root, "api/tags"), timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument tags = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (tags.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
                {
                    string expected = NormalizeModel(model);
                    foreach (JsonElement item in models.EnumerateArray())
                    {
                        string? name = item.TryGetProperty("name", out JsonElement nameValue) && nameValue.ValueKind == JsonValueKind.String
                            ? nameValue.GetString()
                            : null;
                        if (name is not null && string.Equals(NormalizeModel(name), expected, StringComparison.OrdinalIgnoreCase) &&
                            item.TryGetProperty("digest", out JsonElement digestValue) && digestValue.ValueKind == JsonValueKind.String)
                        {
                            digest = digestValue.GetString();
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
        }

        if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using HttpResponseMessage response = await client.GetAsync(OllamaAddress.Endpoint(root, "api/version"), timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (document.RootElement.TryGetProperty("version", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    version = value.GetString();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
        }

        if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        return (digest, version);
    }

    private bool IsFresh(FitProbeHistoryEntry? previous, string? digest, string? version) =>
        previous is not null && digest is not null && version is not null &&
        string.Equals(previous.Digest, digest, StringComparison.Ordinal) &&
        string.Equals(previous.Version, version, StringComparison.Ordinal);

    private void AppendHistory(FitProbeHistoryEntry entry)
    {
        try
        {
            string line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            lock (_historyGate) File.AppendAllText(_historyPath, line);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not save a fit measurement for {Node}.", entry.Node);
        }
    }

    private void WriteJobMarker(Job job)
    {
        string path = MarkerPath(job.Id);
        File.WriteAllText(path, JsonSerializer.Serialize(new PersistedJob(
            job.Id, job.Node, job.Model, job.Root.AbsoluteUri, job.Address, job.Force, job.StartedAtUtc), JsonOptions));
    }

    private void LoadInterruptedJobs()
    {
        foreach (string path in Directory.EnumerateFiles(_labDirectory, "fit-active-*.json"))
        {
            try
            {
                PersistedJob? persisted = JsonSerializer.Deserialize<PersistedJob>(File.ReadAllText(path), JsonOptions);
                if (persisted is null || !Uri.TryCreate(persisted.Root, UriKind.Absolute, out Uri? root)) continue;
                Job recovered = new(persisted.Id, persisted.Node, persisted.Model, root, persisted.Address, persisted.Force,
                    Thresholds(), persisted.StartedAtUtc);
                foreach (FitProbeHistoryEntry entry in ReadHistory().Where(entry => entry.JobId == persisted.Id).OrderBy(entry => entry.Context))
                    recovered.AddResult(ToResult(entry));
                recovered.Finish("incomplete", "The service restarted while this measurement was running.");
                _jobs[persisted.Id] = recovered;
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "Could not recover an interrupted fit measurement.");
            }
        }
    }

    private void DeleteJobMarker(string id)
    {
        try
        {
            string path = MarkerPath(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not clear a fit measurement marker.");
        }
    }

    private string MarkerPath(string id) => Path.Combine(_labDirectory, $"fit-active-{id}.json");

    private FitProbeHistoryEntry[] ReadHistory()
    {
        lock (_historyGate)
        {
            if (!File.Exists(_historyPath)) return [];
            try
            {
                return File.ReadLines(_historyPath)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(TryReadHistoryLine)
                    .OfType<FitProbeHistoryEntry>()
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not read fit measurement history.");
                return [];
            }
        }
    }

    private static FitProbeHistoryEntry? TryReadHistoryLine(string line)
    {
        try { return JsonSerializer.Deserialize<FitProbeHistoryEntry>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private FitThresholds Thresholds()
    {
        FleetLabConfig settings = _config.Current.Lab ?? new FleetLabConfig();
        return new FitThresholds(settings.MinSpeedFraction, settings.FullFitShare);
    }

    private void PruneJobs()
    {
        foreach (Job old in _jobs.Values
                     .Where(job => job.State != "running" && job.FinishedAtUtc < DateTimeOffset.UtcNow - TimeSpan.FromMinutes(15))
                     .ToArray())
        {
            _jobs.Remove(old.Id);
        }
    }

    private static FitProbeStartResult Refused(string reason) => new(null, reason);

    private static string NormalizeAddress(Uri root) => MeasuringMachines.NormalizeAddress(root);

    private static string HostPort(Uri root) => root.HostNameType == UriHostNameType.IPv6
        ? $"[{root.Host}]:{root.Port.ToString(CultureInfo.InvariantCulture)}"
        : $"{root.IdnHost}:{root.Port.ToString(CultureInfo.InvariantCulture)}";

    private static string NormalizeModel(string model)
    {
        string trimmed = model.Trim();
        return trimmed.LastIndexOf(':') > trimmed.LastIndexOf('/') ? trimmed : trimmed + ":latest";
    }

    private static string StatusName(FitStatus status) => status switch
    {
        FitStatus.Ok => "ok",
        FitStatus.DoesNotFit => "doesNotFit",
        _ => "error"
    };

    private static FitStatus ParseStatus(string status) => status switch
    {
        "ok" => FitStatus.Ok,
        "doesNotFit" => FitStatus.DoesNotFit,
        _ => FitStatus.Error
    };

    private static FitPoint ToPoint(FitProbeHistoryEntry entry) => new(
        entry.Context,
        ParseStatus(entry.Status),
        entry.GpuShare,
        entry.TokensPerSecond,
        entry.LoadSeconds,
        entry.Detail);

    private static FitProbeSizeResult ToResult(FitProbeHistoryEntry entry) => new(
        entry.Context,
        entry.Status,
        entry.GpuShare,
        entry.TokensPerSecond,
        entry.LoadSeconds,
        entry.Detail,
        entry.WhenUtc);

    private static FitProbeSizeResult ToResult(FitPoint point) => new(
        point.Context,
        StatusName(point.Status),
        point.GpuShare,
        point.TokensPerSecond,
        point.LoadSeconds,
        point.Detail,
        DateTimeOffset.UtcNow);

    private sealed record PersistedJob(string Id, string Node, string Model, string Root, string Address, bool Force, DateTimeOffset StartedAtUtc);

    private sealed class Job(string id, string node, string model, Uri root, string address, bool force, FitThresholds thresholds,
        DateTimeOffset? startedAtUtc = null)
    {
        private readonly object _gate = new();
        private readonly List<FitProbeSizeResult> _results = [];
        private int? _currentContext;
        private string _state = "running";
        private DateTimeOffset? _finishedAtUtc;
        private string? _reason;

        public string Id { get; } = id;
        public string Node { get; } = node;
        public string Model { get; } = model;
        public Uri Root { get; } = root;
        public string Address { get; } = address;
        public bool Force { get; } = force;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc ?? DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();

        public string State { get { lock (_gate) return _state; } }
        public DateTimeOffset? FinishedAtUtc { get { lock (_gate) return _finishedAtUtc; } }
        public string? CancelReason { get { lock (_gate) return _reason; } }

        public void SetCurrent(int context) { lock (_gate) _currentContext = context; }
        public void AddResult(FitProbeSizeResult result) { lock (_gate) _results.Add(result); }

        public bool RequestCancel(string reason)
        {
            lock (_gate)
            {
                if (_state != "running") return false;
                _reason = reason;
            }
            Cancellation.Cancel();
            return true;
        }

        public void Finish(string state, string? reason)
        {
            lock (_gate)
            {
                _state = state;
                _reason = reason;
                _currentContext = null;
                _finishedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public FitProbeJobSnapshot Snapshot()
        {
            lock (_gate)
            {
                FitPoint[] points = _results.Select(result => new FitPoint(
                    result.Context, ParseStatus(result.Status), result.GpuShare, result.TokensPerSecond,
                    result.LoadSeconds, result.Detail)).ToArray();
                return new FitProbeJobSnapshot(Id, Node, Model, _state, _currentContext, _results.ToArray(),
                    FitProbe.Recommend(points, thresholds), StartedAtUtc, _finishedAtUtc, _reason);
            }
        }
    }
}
