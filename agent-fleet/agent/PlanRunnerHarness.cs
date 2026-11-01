namespace AgentFleet;

internal sealed partial class PlanRunner
{
    /// <summary>The run-log kind of the line that names the hardening options a run used.</summary>
    private const string HarnessOptionsKind = "harness-options";

    /// <summary>The run-log kind of the line that says, for one round, how long the model spent reading the prompt and writing the answer.</summary>
    private const string ModelTimingsKind = "model-timings";

    /// <summary>The run-log kind of the line that says a repair round was ended for reading without writing (HarnessOptions.EnableBudgetFilter).</summary>
    private const string ReadBudgetKind = "read-budget-ended";

    /// <summary>The run-log kind of the line that names the hub mode at the start of a run, or a change to it during the run.</summary>
    private const string FleetModeKind = "fleet-mode";

    // The hub mode each running plan last wrote to its log, so a change made while it runs is logged where it took effect.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FleetMode> _modeLogged = new(StringComparer.Ordinal);

    // Written once at the start of every run, so two runs can be compared knowing which parts were on.
    private void LogHarnessOptions(string planId)
    {
        _store.AddEvent(planId, null, null, HarnessOptionsKind, detail: _harness.Describe());
        if (_modeService is not null)
        {
            _modeLogged[planId] = _modeService.Mode;
            _store.AddEvent(planId, null, null, FleetModeKind, detail: $"Hub mode at the start of the run: {ModeName(_modeService.Mode)}.");
        }
    }

    // Before each attempt: the mode decides which machines a round may use, so a change mid-run explains a change of machine.
    private void LogFleetModeChange(string planId, int stepId, int attempt)
    {
        if (_modeService is null)
        {
            return;
        }

        FleetMode now = _modeService.Mode;
        if (_modeLogged.TryGetValue(planId, out FleetMode before) && before != now)
        {
            _store.AddEvent(planId, stepId, attempt, FleetModeKind,
                detail: $"Hub mode changed from {ModeName(before)} to {ModeName(now)} during the run; rounds from here on follow the new mode.");
        }

        _modeLogged[planId] = now;
    }

    private static string ModeName(FleetMode mode) => mode.ToString().ToLowerInvariant();
}
