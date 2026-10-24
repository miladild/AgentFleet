namespace AgentFleet;

/// <summary>
/// When a vision machine is contacted. "ondemand" (the default) asks the routine health check only for the model list and
/// sends no inference, so the model is loaded by an image and leaves after Ollama's own idle time. "off" contacts nothing,
/// so the machine takes no graphics memory and images get a note saying how to turn it on. "always" is the health check
/// as text machines get it, which keeps the model loaded.
/// </summary>
internal static class NodeVisionMode
{
    public const string Off = "off";
    public const string OnDemand = "ondemand";
    public const string Always = "always";

    /// <summary>The choices as a person reads them in a refusal.</summary>
    public const string Choices = "off, ondemand (the default) or always";

    /// <summary>Said when a request would contact a vision machine that is turned off.</summary>
    public const string TurnedOffRefusal =
        "That vision machine is turned off, so it is not contacted. Set its visionMode to ondemand under Machines, or run Set-FleetVision.ps1.";

    /// <summary>Lower case, with the spellings "on-demand", "on_demand" and "on demand" read as ondemand. Anything else is kept, so a refusal can name it.</summary>
    public static string Canonical(string value)
    {
        string lower = value.Trim().ToLowerInvariant();
        return lower is "on-demand" or "on_demand" or "on demand" ? OnDemand : lower;
    }

    public static bool IsKnown(string canonical) => canonical is Off or OnDemand or Always;

    /// <summary>A machine that never set a mode gets ondemand, the new default for vision machines.</summary>
    public static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? OnDemand : Canonical(value);

    /// <summary>
    /// The mode a saved machine keeps when a save leaves it out. A vision machine keeps its previous mode; a text machine has none,
    /// so a machine that was a vision machine and is now a text machine does not carry the old mode across.
    /// </summary>
    public static string? Resolve(bool vision, string? requested, string? previous) => vision ? requested ?? previous : requested;

    /// <summary>
    /// True when a request for this address would reach nothing but turned-off vision machines. A vision machine and a text
    /// machine can share one Ollama, so the address stays usable while any machine there is on. An address no machine uses is not refused.
    /// </summary>
    public static bool IsTurnedOffAt(IEnumerable<FleetNodeDefinition> nodes, Uri address)
    {
        FleetNodeDefinition[] atAddress = nodes
            .Where(node => string.Equals(node.OpenAiEndpoint.Host, address.Host, StringComparison.OrdinalIgnoreCase) &&
                node.OpenAiEndpoint.Port == address.Port)
            .ToArray();
        return atAddress.Length > 0 && atAddress.All(node => node.IsTurnedOff);
    }
}
