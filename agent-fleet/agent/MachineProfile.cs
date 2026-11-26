namespace AgentFleet;

/// <summary>
/// How a plan run treats one machine, worked out from the node's own settings and never from its name. Only what a run measured
/// to matter is in it; a field is added when a run shows a machine needs different handling.
/// </summary>
internal sealed record MachineProfile(bool RulerReadsFiles, string Reason, int? RulerPromptCharacters = null)
{
    /// <summary>
    /// What a ruling needs in the window besides its prompt, in tokens: the first answer (cap 1 500), the verdict follow-up and its answer
    /// (about 700) and the corrected files' answer (cap 3 000), all of which stay in the conversation, plus a margin.
    /// </summary>
    internal const int RulerAnswerReserveTokens = 5500;

    /// <summary>Characters per token assumed for code and test output, on the low side so that a prompt is not underestimated.</summary>
    internal const int CharactersPerToken = 3;

    /// <summary>The least a ruler prompt is cut to, so a very small window still gets the failing test and its output.</summary>
    internal const int LeastRulerPromptCharacters = 8000;

    /// <summary>The most characters a ruler's prompt may have for a window, or null when the window is not known.</summary>
    internal static int? RulerPromptBudget(int? window) =>
        window is { } tokens ? Math.Max(LeastRulerPromptCharacters, (tokens - RulerAnswerReserveTokens) * CharactersPerToken) : null;

    /// <summary>
    /// The largest context window, in tokens, at which a machine rules on a disputed test from the prompt alone. A ruling that reads
    /// files makes one model call for each read and each call carries the whole conversation again: measured on a machine with this
    /// window, one ruling summed 90 000 to 250 000 prompt tokens, took 2 to 4 minutes, and twice ended with no text at all. The prompt
    /// already holds the disputed tests, the check output and the files the step changed.
    /// </summary>
    internal const int SmallWindow = 16384;

    /// <summary>The environment setting that overrides the rule for every machine: "on" always reads, "off" never reads, anything else decides by window.</summary>
    internal const string RulerReadsSetting = "FLEET_RULER_READS";

    /// <summary>The profile of a machine, or of a machine nothing is known about (it reads, as every ruler did before).</summary>
    public static MachineProfile For(FleetNodeDefinition? node, Func<string, string?>? setting = null)
    {
        string? forced = (setting ?? Environment.GetEnvironmentVariable)(RulerReadsSetting)?.Trim();
        int? budget = RulerPromptBudget(node?.ContextLength);
        if (string.Equals(forced, "on", StringComparison.OrdinalIgnoreCase))
        {
            return new MachineProfile(true, $"{RulerReadsSetting} is on", budget);
        }

        if (string.Equals(forced, "off", StringComparison.OrdinalIgnoreCase))
        {
            return new MachineProfile(false, $"{RulerReadsSetting} is off", budget);
        }

        return node?.ContextLength is { } window && window <= SmallWindow
            ? new MachineProfile(false, $"its context window is {window} tokens, {SmallWindow} or less", budget)
            : new MachineProfile(true, node?.ContextLength is { } larger ? $"its context window is {larger} tokens" : "its context window is not set", budget);
    }
}
