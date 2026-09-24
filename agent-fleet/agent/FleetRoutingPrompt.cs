using System.Text;
using System.Text.Json;

namespace AgentFleet;

/// <summary>
/// Builds the triage classifier's instructions and output schema from the tiers that
/// actually exist in fleet.config.json, instead of a prompt written around three fixed
/// machine names. The classifier still answers with a node name (the vocabulary the
/// prompt was tuned with), so a heavy/standard/light setup produces the same prompt as
/// before with that config's own names dropped in.
///
/// A tier that is missing is not an error: its work goes to the nearest tier that
/// exists, and the prompt only describes the tiers present.
/// </summary>
internal static class FleetRoutingPrompt
{
    // When a desired tier has no node, try these in order.
    private static readonly IReadOnlyDictionary<string, string[]> Fallbacks = new Dictionary<string, string[]>
    {
        [FleetTiers.Heavy] = [FleetTiers.Heavy, FleetTiers.Standard, FleetTiers.Light],
        [FleetTiers.Standard] = [FleetTiers.Standard, FleetTiers.Heavy, FleetTiers.Light],
        [FleetTiers.Light] = [FleetTiers.Light, FleetTiers.Standard, FleetTiers.Heavy]
    };

    private static readonly (string Text, string Tier)[] ConservativeExamples =
    [
        ("Refactor this function to remove duplicate error handling", FleetTiers.Standard),
        ("Fix this off-by-one bug in my loop", FleetTiers.Standard),
        ("Extract this repeated validation into a helper function", FleetTiers.Standard),
        ("Can you build Blazor apps?", FleetTiers.Standard),
        ("Can you plan a task if I give you a scenario?", FleetTiers.Standard),
        ("What does the % operator do in Python?", FleetTiers.Light),
        ("Write a one-line function that reverses a string", FleetTiers.Light),
        ("\"hey\" / \"how's your day?\"", FleetTiers.Light),
        ("Design a microservice architecture for an e-commerce checkout system", FleetTiers.Heavy)
    ];

    private static readonly (string Text, string Tier)[] AggressiveExamples =
    [
        ("Refactor this function to remove duplicate error handling", FleetTiers.Heavy),
        ("Fix this off-by-one bug in my loop", FleetTiers.Heavy),
        ("Extract this repeated validation into a helper function", FleetTiers.Heavy),
        ("Can you build Blazor apps?", FleetTiers.Heavy),
        ("Can you plan a task if I give you a scenario?", FleetTiers.Heavy),
        ("What does the % operator do in Python?", FleetTiers.Light),
        ("Write a one-line function that reverses a string", FleetTiers.Light),
        ("\"hey\" / \"how's your day?\"", FleetTiers.Light),
        ("Design a microservice architecture for an e-commerce checkout system", FleetTiers.Heavy)
    ];

    public static string ResolveTier(string desiredTier, IReadOnlyDictionary<string, string> nodeByTier)
    {
        foreach (string tier in Fallbacks[desiredTier])
        {
            if (nodeByTier.TryGetValue(tier, out string? node))
            {
                return node;
            }
        }

        throw new InvalidOperationException("No tier is available to route to.");
    }

    public static string BuildInstructions(FleetMode mode, IReadOnlyDictionary<string, string> nodeByTier)
    {
        bool hasHeavy = nodeByTier.TryGetValue(FleetTiers.Heavy, out string? heavy);
        bool hasStandard = nodeByTier.TryGetValue(FleetTiers.Standard, out string? standard);
        bool hasLight = nodeByTier.TryGetValue(FleetTiers.Light, out string? light);
        bool aggressive = mode == FleetMode.Aggressive && hasHeavy;

        var text = new StringBuilder();
        text.AppendLine(
            $"Classify the user's coding request for a local {CountWord(nodeByTier.Count)}-node fleet into exactly one node.");
        if (aggressive)
        {
            text.AppendLine($"The {heavy} machine's GPU is currently free to use, so prefer it.");
        }

        text.AppendLine();

        if (hasHeavy)
        {
            text.AppendLine(aggressive
                ? $"- {heavy}: any coding task beyond a trivial one-liner - bug fixes, refactors, adding a function, complex or multi-file work, architecture. Default here whenever the task involves real thought."
                : $"- {heavy}: complex, multi-file, architecture, or uncertain work");
        }

        if (hasStandard)
        {
            text.AppendLine(aggressive
                ? $"- {standard}: only pick this if you have a specific reason to avoid {heavy} for this one request - {heavy} is preferred and has GPU headroom right now."
                : $"- {standard}: an ordinary, self-contained coding task - a bug fix, a refactor, adding a function - that takes real thought but fits in one file");
        }

        if (hasLight)
        {
            text.AppendLine(
                $"- {light}: a trivial, near-instant question - a syntax lookup, a one-line snippet, a factual question about code, or a plain greeting/small talk with no task attached");
        }

        text.AppendLine();
        text.AppendLine(
            "A short, casually-phrased question is not automatically trivial - judge it by what it's actually " +
            "asking about, not by how short or conversational it sounds. \"Can you build X\", \"are you able to Y\", " +
            "\"can you plan Z if I give you a scenario\" are capability/planning questions about a real task - " +
            "route them the same as if the user were actually asking you to do that task, not as small talk.");
        text.AppendLine();
        text.AppendLine("Examples:");
        foreach ((string example, string tier) in aggressive ? AggressiveExamples : ConservativeExamples)
        {
            text.AppendLine($"{example} -> {ResolveTier(tier, nodeByTier)}");
        }

        text.AppendLine();
        if (hasLight)
        {
            text.Append(aggressive
                ? $"When in doubt, prefer {heavy} - {light} is reserved for genuinely trivial requests only. "
                : hasStandard
                    ? $"When in doubt between {standard} and {light}, prefer {standard} - {light} is reserved for genuinely trivial requests only. "
                    : $"When in doubt, prefer {ResolveTier(FleetTiers.Standard, nodeByTier)} - {light} is reserved for genuinely trivial requests only. ");
        }

        text.Append("Never claim to call tools.");
        return text.ToString();
    }

    // Constrains the classifier's output at the sampler level (Ollama honors JSON-schema
    // response_format via grammar-constrained decoding), not just via prompt instructions.
    // Without this, a small general-purpose model like llama3.2 will sometimes ignore a
    // "reply with one word" instruction entirely and answer the user's question instead,
    // which ParseRoute can't recover a route from and silently falls back to the fallback node.
    public static JsonElement BuildSchema(IEnumerable<string> nodeNames) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                node = new { type = "string", @enum = nodeNames.ToArray() }
            },
            required = new[] { "node" }
        });

    private static string CountWord(int count) => count switch
    {
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        _ => count.ToString()
    };
}
