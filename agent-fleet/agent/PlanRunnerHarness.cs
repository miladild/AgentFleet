namespace AgentFleet;

internal sealed partial class PlanRunner
{
    /// <summary>The run-log kind of the line that names the hardening options a run used.</summary>
    private const string HarnessOptionsKind = "harness-options";

    /// <summary>The run-log kind of the line that says, for one round, how long the model spent reading the prompt and writing the answer.</summary>
    private const string ModelTimingsKind = "model-timings";

    // Written once at the start of every run, so two runs can be compared knowing which parts were on.
    private void LogHarnessOptions(string planId) =>
        _store.AddEvent(planId, null, null, HarnessOptionsKind, detail: _harness.Describe());
}
