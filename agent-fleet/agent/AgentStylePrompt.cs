namespace AgentFleet;

internal static class AgentStylePrompt
{
    public const string Off = "off";
    private static readonly HashSet<string> Levels = new(StringComparer.Ordinal) { Off, "lite", "full", "ultra" };

    public static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? Off : value.Trim().ToLowerInvariant();

    public static bool IsValid(string? value) => value is not null && Levels.Contains(value);

    public static string? Build(FleetNodeDefinition node)
    {
        var rules = new List<string>();
        switch (node.Caveman)
        {
            case "lite":
                rules.Add("Caveman lite: answer directly and remove filler, while keeping normal grammar and every useful detail.");
                break;
            case "full":
                rules.Add("Caveman full: use very concise language and short sentences. Drop filler and articles only when meaning stays clear. Keep technical detail, negation, and safety conditions.");
                break;
            case "ultra":
                rules.Add("Caveman ultra: use the fewest clear words. Never omit a fact, negation, qualifier, safety condition, or technical distinction. Do not abbreviate technical terms. Leave code, commands, identifiers, and quoted errors unchanged.");
                break;
        }

        switch (node.Ponytail)
        {
            case "lite":
                rules.Add("Ponytail lite: for code tasks, prefer the smallest correct change. Reuse existing code and standard tools; avoid speculative work.");
                break;
            case "full":
                rules.Add("Ponytail full: for code tasks, trace the relevant flow and callers, fix the root cause, and choose the smallest correct change. Prefer existing helpers, standard libraries, and platform features. Avoid unrequested abstractions and dependencies. Keep validation, error handling, security, and every explicit requirement.");
                break;
            case "ultra":
                rules.Add("Ponytail ultra: for code tasks, do only the smallest correct requested change after tracing the root cause. Do not add speculative work, abstractions, or dependencies. Never simplify away validation, error handling, security, or an explicit requirement.");
                break;
        }

        return rules.Count == 0
            ? null
            : "Machine-configured style for this model call. Apply when relevant; preserve the user's request, correctness, and security.\n" + string.Join("\n", rules);
    }
}
