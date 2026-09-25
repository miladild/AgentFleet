namespace AgentFleet;

/// <summary>
/// Plan mode: the switch in the web UI, persisted through FleetConfigStore (fleet.config.json) so it survives a
/// restart, plus a per-request override. A client can ask for plan mode for its own conversation only ("@fleet
/// /plan" in VS Code sends forwardedProps.fleetPlanMode), without turning it on for every other chat.
/// Enforcement lives in PlanGate; this only says whether it applies.
/// </summary>
internal sealed class FleetPlanModeService
{
    private readonly FleetConfigStore _configStore;
    private readonly FleetRequestContext? _requestContext;
    private volatile bool _enabled;

    public FleetPlanModeService(FleetConfigStore configStore, FleetRequestContext? requestContext = null)
    {
        _configStore = configStore;
        _requestContext = requestContext;
        _enabled = configStore.Current.PlanModeEnabled;
    }

    /// <summary>The global switch.</summary>
    public bool Enabled => _enabled;

    /// <summary>What applies to the request being handled: its own choice when it made one, else the switch.</summary>
    public bool Effective => _requestContext?.Current?.PlanMode ?? _enabled;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _configStore.UpdatePlanMode(enabled);
    }
}