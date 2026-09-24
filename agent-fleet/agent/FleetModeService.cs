namespace AgentFleet;

internal enum FleetMode
{
    /// <summary>Default. Hub is reserved for genuinely complex/architecture work,
    /// protecting its GPU for the user's own foreground use (daytime).</summary>
    Conservative,

    /// <summary>Hub also takes ordinary tasks that would otherwise go to the standard tier,
    /// making use of its GPU headroom when the user isn't actively using it (night).</summary>
    Aggressive
}

/// <summary>
/// Runtime-mutable toggle for how much of the fleet's workload the hub takes on.
/// Persisted through FleetConfigStore (fleet.config.json), so unlike before, it
/// survives a service restart instead of always resetting to Conservative.
/// </summary>
internal sealed class FleetModeService
{
    private readonly FleetConfigStore _configStore;
    private volatile FleetMode _mode;

    public FleetModeService(FleetConfigStore configStore)
    {
        _configStore = configStore;
        _mode = Enum.TryParse(configStore.Current.Mode, ignoreCase: true, out FleetMode parsed)
            ? parsed
            : FleetMode.Conservative;
    }

    public FleetMode Mode => _mode;

    public void SetMode(FleetMode mode)
    {
        _mode = mode;
        _configStore.UpdateMode(mode.ToString().ToLowerInvariant());
    }
}
