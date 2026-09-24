using System.Text;

namespace AgentFleet;

/// <summary>
/// The two tools that let a model use the durable record on purpose: pin something the next agent must
/// respect, and look back through what was actually said and done. Both touch only the fleet's own
/// journal, so they are safe in every plan phase.
/// </summary>
internal sealed partial class ContextTools
{
    /// <summary>
    /// Whether to offer a context tool on this request. Small models call a "pin a decision" tool on a
    /// plain greeting ("Use a direct response."), and a pinned line is then handed to every later agent,
    /// so record_decision is offered only where a decision is likely: planning, plan steps, or a user
    /// message that asks to remember, decide or keep something. Both need a durable record to exist.
    /// </summary>
    public static bool ShouldOffer(
        string toolName,
        bool hasContext,
        bool planModeOn,
        bool fromPlanRunner,
        string? lastUserText)
    {
        if (toolName == "search_context")
        {
            return hasContext;
        }

        if (toolName != "record_decision")
        {
            return true;
        }

        return hasContext && (planModeOn || fromPlanRunner || AsksToRemember(lastUserText));
    }

    internal static bool AsksToRemember(string? text) =>
        !string.IsNullOrWhiteSpace(text) && RememberRequest().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(remember|pin|note that|keep in mind|from now on|decid(e|ed|ing)|decision|constraint|always use|never use|don't (ever )?use|do not (ever )?use|we (use|chose|will use|are using))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RememberRequest();

    private readonly FleetContextJournal _journal;

    public ContextTools(FleetContextJournal journal) => _journal = journal;

    public string RecordDecision(string decision, string? reason = null, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(decision))
        {
            return "Error: say what was decided.";
        }

        if (_journal.Current is null)
        {
            return "This conversation has no durable record attached, so nothing was saved. State the decision in your reply instead.";
        }

        string kind = string.Equals(category?.Trim(), "constraint", StringComparison.OrdinalIgnoreCase) ? "constraint" : "decision";
        long? id = _journal.RecordDecision(decision, reason, kind);
        return id is null
            ? "Error: the decision could not be saved."
            : $"Pinned as #{id} ({kind}). It is handed to every agent that continues this work, even after a machine change or a restart.";
    }

    public string SearchContext(string query, int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: give a word or phrase to look for.";
        }

        FleetRequestIdentity? identity = _journal.Current;
        if (identity is null)
        {
            return "This conversation has no durable record attached, so there is nothing to search.";
        }

        int count = Math.Clamp(limit ?? 12, 1, 40);
        // The context blocks the fleet hands out are also recorded, and would match everything they quote.
        List<FleetContextEvent> matches = _journal.Store.SearchEvents(identity.ContextId, query.Trim(), 200)
            .Where(e => e.Kind != FleetContextEventKind.ContextAssembled)
            .TakeLast(count)
            .ToList();
        if (matches.Count == 0)
        {
            return $"Nothing in this conversation's record mentions \"{query.Trim()}\".";
        }

        var text = new StringBuilder();
        text.AppendLine($"{matches.Count} match(es), oldest first. Event ids can be quoted; the full record is kept.");
        foreach (FleetContextEvent e in matches)
        {
            text.AppendLine(ContextText.Describe(e));
        }

        return text.ToString().TrimEnd();
    }
}
