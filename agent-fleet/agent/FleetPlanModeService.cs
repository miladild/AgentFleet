namespace AgentFleet;

/// <summary>
/// Runtime-mutable toggle: when on, specialists are told to describe a plan and wait
/// for an explicit "CONFIRM" before calling any tool. This is a prompt-level hint, not
/// a code-enforced gate - the model could still ignore it, same caveat as the routing
/// classifier's own instruction-following reliability. Persisted through
/// FleetConfigStore (fleet.config.json), so it survives a service restart instead of
/// always resetting to off.
/// </summary>
internal sealed class FleetPlanModeService
{
    private readonly FleetConfigStore _configStore;
    private volatile bool _enabled;

    public FleetPlanModeService(FleetConfigStore configStore)
    {
        _configStore = configStore;
        _enabled = configStore.Current.PlanModeEnabled;
    }

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _configStore.UpdatePlanMode(enabled);
    }
}
