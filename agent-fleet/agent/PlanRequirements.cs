using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// The review of a planner's requirement list (test-first on): a plan whose goal is more than one sentence must say what
/// its tests have to cover, and each requirement must be a real, distinct line of text.
/// </summary>
internal static partial class PlanRequirements
{
    internal const int LongGoalCharacters = 160;

    /// <summary>A goal is long when it runs to two or more sentences or is over 160 characters.</summary>
    internal static bool IsLongGoal(string goal) =>
        goal.Trim().Length > LongGoalCharacters || SentenceEnd().Matches(goal).Count >= 2;

    internal static IReadOnlyList<string> Problems(string goal, IReadOnlyList<string> texts)
    {
        var problems = new List<string>();
        if (texts.Count == 0 && IsLongGoal(goal))
        {
            problems.Add("The goal has more than one sentence, so the plan needs a requirements list: pass requirements, one per must or " +
                         "should clause, named edge case or example in the task, each written so a test can check it.");
        }

        for (int index = 0; index < texts.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(texts[index]))
            {
                problems.Add($"Requirement {index + 1} is empty: write it in words or leave it out.");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string text in texts.Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            if (!seen.Add(Collapse(text)))
            {
                problems.Add($"Requirement \"{text.Trim()}\" is listed twice: keep one.");
            }
        }

        return problems;
    }

    private static string Collapse(string text) => Spaces().Replace(text.Trim(), " ").ToLowerInvariant();

    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
