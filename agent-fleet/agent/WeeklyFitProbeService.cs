using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>What a weekly measurement found worse than the one before. It stays open until a later weekly run finds no regression.</summary>
internal sealed record WeeklyFitRegression(string Model, DateTimeOffset Since, string Summary);

/// <summary>An open regression as the status endpoint shows it.</summary>
internal sealed record WeeklyFitRegressionView(DateTimeOffset Since, string Summary);

internal sealed record WeeklyFitNodeStatus(string Name, string Model, DateTimeOffset? LastMeasuredUtc, WeeklyFitRegressionView? Regression);

/// <summary>The weekly measurement's settings, when its next window opens, when it last completed, and each machine it covers.</summary>
internal sealed record WeeklyFitStatus(
    bool Enabled,
    string DayOfWeek,
    int StartHour,
    int Hours,
    DateTimeOffset? NextWindowStartUtc,
    DateTimeOffset? LastRunUtc,
    IReadOnlyList<WeeklyFitNodeStatus> Nodes);

/// <summary>A regression that is new. It is sent once, and not again while the same regression stays open.</summary>
internal sealed record WeeklyFitNotice(string Node, string Model, string Summary, DateTimeOffset SinceUtc);

internal interface IWeeklyFitNoticeSender
{
    Task SendAsync(WeeklyFitNotice notice);
}

/// <summary>
/// Measures the worker machines' context fit once a week, inside the window the config sets, and says so when a machine's
/// fit got worse since its previous measurement. Each measurement goes through FitProbeService, one at a time. The hub is
/// never measured here, and no new measurement starts once the window has closed.
/// </summary>
internal sealed class WeeklyFitProbeService : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    // A machine whose newest measurement is this recent is left alone, so each window measures each machine about once.
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(6);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const string StateFileName = "weekly-fit-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly FitProbeService _fitProbes;
    private readonly FleetConfigStore _config;
    private readonly FleetOptions _options;
    private readonly FleetPlanStore _plans;
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly IWeeklyFitNoticeSender? _sender;
    private readonly ILogger _logger;
    private readonly string _statePath;
    private readonly object _gate = new();
    private readonly HashSet<string> _weeklyJobs = new(StringComparer.Ordinal);
    private readonly WeeklyFitState _state;

    public WeeklyFitProbeService(
        FitProbeService fitProbes,
        FleetConfigStore config,
        FleetOptions options,
        FleetPlanStore plans,
        IHttpClientFactory clients,
        TimeProvider time,
        IWeeklyFitNoticeSender? sender,
        ILogger logger)
    {
        _fitProbes = fitProbes;
        _config = config;
        _options = options;
        _plans = plans;
        _clients = clients;
        _time = time;
        _sender = sender;
        _logger = logger;
        _statePath = Path.Combine(fitProbes.LabDirectory, StateFileName);
        _state = LoadState();
        _fitProbes.Finished += OnFitFinished;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "The weekly fit measurement check failed; it will try again at the next wake-up.");
            }
        }
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One wake-up: starts the next machine's measurement when the window is open and nothing else is measuring.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        FleetWeeklyProbeConfig settings = Settings();
        if (!settings.Enabled || !WeeklyFitWindow.IsOpen(LocalNow(), settings))
        {
            return;
        }

        // FitProbeService refuses a measurement during a plan as well. Checking here first means no machine is contacted meanwhile.
        if (_plans.List().Any(plan => plan.Status is PlanStatus.Running or PlanStatus.Approved))
        {
            return;
        }

        if (_fitProbes.List().Any(job => job.State == "running"))
        {
            return;
        }

        foreach (FleetNodeDefinition node in Candidates(_time.GetUtcNow()))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A machine with a model in memory may be in use, or may not answer. Either way it is tried again at the next wake-up.
            if (await LoadedAsync(node, cancellationToken) is not false)
            {
                continue;
            }

            lock (_gate)
            {
                FitProbeStartResult result = _fitProbes.Start(node.Name, force: true);
                if (result.Job is null)
                {
                    _logger.LogDebug("The weekly fit measurement of {Node} waits: {Reason}", node.Name, result.Reason);
                    continue;
                }

                _weeklyJobs.Add(result.Job.Id);
                _logger.LogInformation("The weekly fit measurement of {Node} started.", node.Name);
                return;
            }
        }
    }

    /// <summary>What the status endpoint shows.</summary>
    public WeeklyFitStatus Status()
    {
        FleetWeeklyProbeConfig settings = Settings();
        DateTime local = LocalNow();
        DateTimeOffset? lastRunUtc;
        lock (_gate)
        {
            lastRunUtc = _state.LastRunUtc;
        }

        WeeklyFitNodeStatus[] nodes = _options.Nodes
            .Where(IsWorker)
            .Select(node => new WeeklyFitNodeStatus(node.Name, node.Model, LastMeasuredUtc(node), RegressionFor(node)))
            .ToArray();
        return new WeeklyFitStatus(
            settings.Enabled,
            settings.DayOfWeek,
            settings.StartHour,
            settings.Hours,
            settings.Enabled ? UtcOf(WeeklyFitWindow.NextOpening(local, settings)) : null,
            lastRunUtc,
            nodes);
    }

    /// <summary>The Setup line for the weekly measurement. Null while it is off, and then the line is not shown.</summary>
    public SetupCheck? Check()
    {
        WeeklyFitStatus status = Status();
        return status.Enabled ? WeeklyFitCheck.Build(status.Nodes, status.LastRunUtc) : null;
    }

    private void OnFitFinished(FitProbeJobSnapshot job)
    {
        try
        {
            lock (_gate)
            {
                if (!_weeklyJobs.Remove(job.Id))
                {
                    return;
                }

                // The notice is started under the gate, so a status read never shows a regression ahead of its notice.
                // The webhook call itself runs in the background.
                WeeklyFitNotice? notice = Record(job);
                SaveState();
                if (notice is not null)
                {
                    _ = SendNoticeAsync(notice);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The weekly fit measurement of {Node} could not be compared with the one before.", job.Node);
        }
    }

    // Compares a finished weekly run with the previous measurement of the same machine and model. A notice is returned only
    // when the regression is new; one that is still open from an earlier week is not sent again. Call under the gate.
    private WeeklyFitNotice? Record(FitProbeJobSnapshot job)
    {
        // A run that was stopped or cut short is not a measurement of the machine's fit, so it neither counts as a run nor compares.
        if (job.State != "complete")
        {
            return null;
        }

        DateTimeOffset finishedUtc = job.FinishedAtUtc ?? _time.GetUtcNow();
        _state.LastRunUtc = finishedUtc;

        // The history is kept in the order it was written, so the last entry of another run is that run's latest.
        FitProbeHistoryEntry[] history = _fitProbes.HistoryFor(job.Node, job.Model);
        string? previousJob = history.LastOrDefault(entry => entry.JobId != job.Id)?.JobId;
        if (previousJob is null)
        {
            return null;
        }

        FitPoint[] previous = history.Where(entry => entry.JobId == previousJob).Select(FitProbeService.ToPoint).ToArray();
        FitPoint[] current = history.Where(entry => entry.JobId == job.Id).Select(FitProbeService.ToPoint).ToArray();
        if (!WeeklyFitRegressions.CanCompare(previous) || !WeeklyFitRegressions.CanCompare(current))
        {
            return null;
        }

        IReadOnlyList<string> reasons = WeeklyFitRegressions.Find(previous, current, _fitProbes.Thresholds());
        if (reasons.Count == 0)
        {
            _state.Regressions.Remove(job.Node);
            return null;
        }

        string summary = string.Join("; ", reasons);
        bool isNew = !_state.Regressions.TryGetValue(job.Node, out WeeklyFitRegression? open) || open.Model != job.Model;
        DateTimeOffset since = isNew ? finishedUtc : open!.Since;
        _state.Regressions[job.Node] = new WeeklyFitRegression(job.Model, since, summary);
        return isNew ? new WeeklyFitNotice(job.Node, job.Model, summary, since) : null;
    }

    private async Task SendNoticeAsync(WeeklyFitNotice notice)
    {
        if (_sender is null)
        {
            _logger.LogInformation(
                "A weekly fit regression on {Node} is shown in the setup check only: no notice address is set.", notice.Node);
            return;
        }

        try
        {
            await _sender.SendAsync(notice);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The weekly fit notice for {Node} could not be sent.", notice.Node);
        }
    }

    // Not measured here: the machine the backend runs on, the node the plans fall back to (the hub), a node named hub,
    // vision machines and OpenAI-compatible servers.
    private static bool IsWorker(FleetNodeDefinition node) =>
        !node.Vision && !node.Fallback &&
        !string.Equals(node.Name, "hub", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(node.Api, "openai", StringComparison.OrdinalIgnoreCase) &&
        OllamaAddress.TryGetRoot(node.OpenAiEndpoint.AbsoluteUri, out Uri root) &&
        !OllamaAddress.IsThisMachine(root);

    // Machines never measured come first, then the ones measured longest ago.
    private IReadOnlyList<FleetNodeDefinition> Candidates(DateTimeOffset nowUtc) => _options.Nodes
        .Where(IsWorker)
        .Select(node => (Node: node, LastMeasuredUtc: LastMeasuredUtc(node)))
        .Where(item => item.LastMeasuredUtc is null || nowUtc - item.LastMeasuredUtc.Value > MaximumAge)
        .OrderBy(item => item.LastMeasuredUtc.HasValue)
        .ThenBy(item => item.LastMeasuredUtc)
        .Select(item => item.Node)
        .ToArray();

    private DateTimeOffset? LastMeasuredUtc(FleetNodeDefinition node) =>
        _fitProbes.HistoryFor(node.Name, node.Model).Select(entry => (DateTimeOffset?)entry.WhenUtc).Max();

    private async Task<bool?> LoadedAsync(FleetNodeDefinition node, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        return await new FitProbe(_clients.CreateClient(FleetHealthMonitor.HttpClientName))
            .AnyModelLoadedAsync(node.OllamaRoot, timeout.Token);
    }

    private WeeklyFitRegressionView? RegressionFor(FleetNodeDefinition node)
    {
        lock (_gate)
        {
            // A regression found for an earlier model says nothing about the model the machine runs now.
            return _state.Regressions.TryGetValue(node.Name, out WeeklyFitRegression? regression) && regression.Model == node.Model
                ? new WeeklyFitRegressionView(regression.Since, regression.Summary)
                : null;
        }
    }

    private FleetWeeklyProbeConfig Settings() => _config.Current.WeeklyProbeSettings.Normalized();

    private DateTime LocalNow() => _time.GetLocalNow().DateTime;

    private DateTimeOffset UtcOf(DateTime local) =>
        new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local)).ToUniversalTime();

    private WeeklyFitState LoadState()
    {
        try
        {
            if (File.Exists(_statePath) && JsonSerializer.Deserialize<WeeklyFitState>(File.ReadAllText(_statePath), JsonOptions) is { } saved)
            {
                return saved;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Could not read the weekly fit measurement state; starting without it.");
        }

        return new WeeklyFitState();
    }

    // Written through a temporary file, so a crash mid-write cannot leave a half-written state behind.
    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            string temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temporary, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not save the weekly fit measurement state.");
        }
    }

    private sealed class WeeklyFitState
    {
        public DateTimeOffset? LastRunUtc { get; set; }

        public Dictionary<string, WeeklyFitRegression> Regressions { get; set; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// The weekly window in the machine's local time. It opens on the configured day at the configured hour and stays open for
/// the configured number of hours, so it can run past midnight or on into the next week.
/// </summary>
internal static class WeeklyFitWindow
{
    /// <summary>When the window last opened, at or before the given local time.</summary>
    public static DateTime OpenedAt(DateTime local, FleetWeeklyProbeConfig settings)
    {
        int daysBack = ((int)local.DayOfWeek - (int)DayOf(settings) + 7) % 7;
        DateTime opened = local.Date.AddDays(-daysBack).AddHours(settings.StartHour);
        return opened <= local ? opened : opened.AddDays(-7);
    }

    public static bool IsOpen(DateTime local, FleetWeeklyProbeConfig settings) =>
        local < OpenedAt(local, settings).AddHours(settings.Hours);

    /// <summary>When the window opens next. Always later than the given local time.</summary>
    public static DateTime NextOpening(DateTime local, FleetWeeklyProbeConfig settings) => OpenedAt(local, settings).AddDays(7);

    private static DayOfWeek DayOf(FleetWeeklyProbeConfig settings) =>
        FleetWeeklyProbeConfig.ParseDay(settings.DayOfWeek) ?? DayOfWeek.Sunday;
}

/// <summary>
/// How a weekly measurement compares with the previous measurement of the same machine and model. A regression is one of:
/// the recommended context went down by at least one size either run probed, the model no longer fully fits on the
/// graphics card, or the speed at one size fell by more than a quarter.
/// </summary>
internal static class WeeklyFitRegressions
{
    /// <summary>A speed that falls by more than this share, at the same context size, is a regression.</summary>
    public const double MaximumSpeedDrop = 0.25;

    /// <summary>A run can be compared only when it measured at least one speed.</summary>
    public static bool CanCompare(IReadOnlyList<FitPoint> points) => points.Any(IsMeasuredSpeed);

    /// <summary>The ways the current run is worse than the previous one. Empty when it is not worse.</summary>
    public static IReadOnlyList<string> Find(IReadOnlyList<FitPoint> previous, IReadOnlyList<FitPoint> current, FitThresholds thresholds)
    {
        FitRecommendation before = FitProbe.Recommend(previous, thresholds);
        FitRecommendation now = FitProbe.Recommend(current, thresholds);
        var reasons = new List<string>();

        if (before.Context is int earlier)
        {
            int[] sizes = previous.Concat(current).Select(point => point.Context).Distinct().Order().ToArray();
            int earlierPlace = Array.IndexOf(sizes, earlier);
            int laterPlace = now.Context is int later ? Array.IndexOf(sizes, later) : -1;
            if (laterPlace < earlierPlace)
            {
                reasons.Add(now.Context is int lowered
                    ? $"the recommended context went from {earlier} to {lowered}"
                    : $"no context size is recommended any more (it was {earlier})");
            }
        }

        if (before.Context is not null && !before.DoesNotFullyFit && now.DoesNotFullyFit)
        {
            reasons.Add("the model no longer fully fits on the graphics card");
        }

        string? drop = SpeedDrop(previous, current);
        if (drop is not null)
        {
            reasons.Add(drop);
        }

        return reasons;
    }

    // Reports the largest fall, when one size lost more than MaximumSpeedDrop of its speed.
    private static string? SpeedDrop(IReadOnlyList<FitPoint> previous, IReadOnlyList<FitPoint> current)
    {
        FitPoint? worstEarlier = null;
        FitPoint? worstLater = null;
        foreach (FitPoint earlier in previous.Where(IsMeasuredSpeed))
        {
            FitPoint? later = current.FirstOrDefault(point => point.Context == earlier.Context && IsMeasuredSpeed(point));
            if (later is null || later.TokensPerSecond!.Value >= earlier.TokensPerSecond!.Value * (1 - MaximumSpeedDrop))
            {
                continue;
            }

            if (worstLater is null ||
                later.TokensPerSecond!.Value / earlier.TokensPerSecond!.Value <
                worstLater.TokensPerSecond!.Value / worstEarlier!.TokensPerSecond!.Value)
            {
                worstEarlier = earlier;
                worstLater = later;
            }
        }

        if (worstEarlier is null || worstLater is null)
        {
            return null;
        }

        double was = worstEarlier.TokensPerSecond!.Value;
        double nowSpeed = worstLater.TokensPerSecond!.Value;
        string percent = Math.Round((1 - nowSpeed / was) * 100).ToString("0", CultureInfo.InvariantCulture);
        return $"speed at {worstLater.Context} went from {was.ToString("0.0", CultureInfo.InvariantCulture)} to " +
               $"{nowSpeed.ToString("0.0", CultureInfo.InvariantCulture)} tokens per second (down {percent} percent)";
    }

    private static bool IsMeasuredSpeed(FitPoint point) => point.Status == FitStatus.Ok && point.TokensPerSecond is not null;
}

/// <summary>The Setup line for the weekly measurement: a warning for a regression, ok when the last run found none.</summary>
internal static class WeeklyFitCheck
{
    public static SetupCheck Build(IReadOnlyList<WeeklyFitNodeStatus> nodes, DateTimeOffset? lastRunUtc)
    {
        string[] regressed = nodes
            .Where(node => node.Regression is not null)
            .Select(node => $"{node.Name} ({node.Model}): {node.Regression!.Summary}. Speed only, not coding skill.")
            .ToArray();
        if (regressed.Length > 0)
        {
            return new SetupCheck("weekly-fit", "machines", FleetSetupService.Warn, "Weekly fit measurement", string.Join(" ", regressed));
        }

        return lastRunUtc is { } last
            ? new SetupCheck("weekly-fit", "machines", FleetSetupService.Ok, "Weekly fit measurement",
                $"No regression in the last weekly measurement ({last:yyyy-MM-dd} UTC).")
            : new SetupCheck("weekly-fit", "machines", FleetSetupService.Info, "Weekly fit measurement",
                "No weekly measurement has run yet.");
    }
}
