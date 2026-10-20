using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// The review of a planner's requirement list (test-first on): a plan whose goal is more than one sentence must say what
/// its tests have to cover, and each requirement must be a real, distinct line of text.
/// </summary>
internal static partial class PlanRequirements
{
    internal const int LongGoalCharacters = 160;

    /// <summary>The most requirements a plan may list, and the longest one: they go into every tests step prompt and retry.</summary>
    internal const int MaxRequirements = 40;
    internal const int MaxRequirementCharacters = 200;

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

        if (texts.Count > MaxRequirements)
        {
            problems.Add($"The plan lists {texts.Count} requirements, and at most {MaxRequirements} are allowed: merge the ones that are about the same behaviour.");
        }

        for (int index = 0; index < texts.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(texts[index]))
            {
                problems.Add($"Requirement {index + 1} is empty: write it in words or leave it out.");
            }
            else if (texts[index].Trim().Length > MaxRequirementCharacters)
            {
                problems.Add($"Requirement {index + 1} is longer than {MaxRequirementCharacters} characters: shorten it or split it in two.");
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

    // Two requirements are the same when they differ only in case, spacing and punctuation.
    private static string Collapse(string text) => NonWords().Replace(text.ToLowerInvariant(), " ").Trim();

    // The end of a sentence is a full stop, ! or ? followed by a space or the end, or an ideographic full stop or its
    // relatives. A few abbreviations end in a full stop without ending a sentence.
    [GeneratedRegex(@"(?<!\b(?:e\.g|i\.e|etc|vs|cf|approx))[.!?](?=\s|$)|[\u3002\uFF01\uFF1F]", RegexOptions.IgnoreCase)]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWords();
}
