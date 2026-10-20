using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>What a step's model said with dispute_test: the frozen tests it believes are wrong, and the specific reason.</summary>
internal sealed record TestDisputeCall(string Tests, string Reason);

/// <summary>Whether an independent ruling on a disputed frozen test is due, and the short reason either way (logged).</summary>
internal sealed record TestRulingDue(bool Due, string Reason);

/// <summary>
/// A model that carries out a step may say that a frozen test an earlier step fixed is wrong (dispute_test). Such a dispute
/// is ruled on by a model that did not write either side, but only when the failure is one the frozen tests show and nothing
/// else: a build error is the code's fault, and a failure in a test the step added is not disputed at all.
/// </summary>
internal static partial class TestDispute
{
    public const string Tool = "dispute_test";

    /// <summary>How many ruler calls ending in no usable answer or a refused correction a step may make before disputes stop.</summary>
    public const int MaxUnresolvedAsks = 2;

    /// <summary>
    /// A ruling is due only when every condition holds: the setting is on, the step is an ordinary one, the plan has frozen
    /// files, the model disputed a test in this attempt, no ruling was made for the step yet, every failing item names a test
    /// in a frozen file, and none of the failing items is a compiler or build error.
    /// </summary>
    public static TestRulingDue Evaluate(bool testFirstOn, bool ordinaryStep, bool hasFrozenFiles, bool disputed, int rulingsMade,
        int unresolvedAsks, string? checkOutput, IReadOnlyList<TestFileText> frozenFiles)
    {
        if (!testFirstOn) return new TestRulingDue(false, "off");
        if (!ordinaryStep) return new TestRulingDue(false, "not-ordinary");
        if (!hasFrozenFiles) return new TestRulingDue(false, "no-frozen-files");
        if (!disputed) return new TestRulingDue(false, "no-dispute");
        if (rulingsMade > 0) return new TestRulingDue(false, "already-ruled");
        // A ruler that gives no usable answer, or a correction the guard refuses, costs a call each time: two are enough.
        if (unresolvedAsks >= MaxUnresolvedAsks) return new TestRulingDue(false, "too-many-asks");

        IReadOnlyList<string> items = PlanFailure.FailureSignature(checkOutput).Items;
        if (items.Count == 0) return new TestRulingDue(false, "no-failing-items");
        if (items.Any(PlanFailure.IsBuildErrorCode)) return new TestRulingDue(false, "build-error");

        var names = frozenFiles.SelectMany(TestNamesOf).ToArray();
        if (items.Any(item => !names.Any(name => Matches(item, name)))) return new TestRulingDue(false, "not-a-frozen-test");
        return new TestRulingDue(true, "due");
    }

    /// <summary>The dispute the model made in this reply, or null when it made none.</summary>
    public static TestDisputeCall? DisputeFrom(StepAgentReply reply)
    {
        AgentToolCall? call = reply.Calls?.FirstOrDefault(candidate => candidate.Name == Tool);
        if (call is null)
        {
            return null;
        }

        call.Arguments.TryGetValue("tests", out string? tests);
        call.Arguments.TryGetValue("reason", out string? reason);
        return new TestDisputeCall(tests?.Trim() ?? string.Empty, reason?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// Whether a failing item names a frozen test: the name's words (ignoring case and punctuation) are the last words of the
    /// item, once the parameters or duration at its end and any message after " - " are left out. A name that is only the
    /// start of a longer test name does not match, and a name with no letters or digits never matches.
    /// </summary>
    internal static bool Matches(string item, string testName)
    {
        IReadOnlyList<string> name = Words(testName);
        if (name.Count == 0) return false;
        IReadOnlyList<string> words = Words(Identifier(item));
        return words.Count >= name.Count && words.Skip(words.Count - name.Count).SequenceEqual(name);
    }

    internal static IReadOnlyList<string> TestNamesOf(TestFileText file) => RequirementCoverage.TestNames(file.Path, file.Text) ?? [];

    /// <summary>The frozen files that hold a test one of the failing items names.</summary>
    internal static IReadOnlyList<TestFileText> Holding(IReadOnlyList<TestFileText> files, IReadOnlyList<string> items) =>
        files.Where(file => TestNamesOf(file).Any(name => items.Any(item => Matches(item, name)))).ToArray();

    // A failing item may end with a parameter list ("(value: 0)") or a duration ("[1 ms]"), or with the runner's message after " - ".
    private static string Identifier(string item)
    {
        string text = TrimTrailingGroups(item.Trim());
        int message = text.IndexOf(" - ", StringComparison.Ordinal);
        return TrimTrailingGroups(message > 0 ? text[..message] : text);
    }

    private static string TrimTrailingGroups(string text)
    {
        string previous;
        do
        {
            previous = text;
            text = TrailingGroup().Replace(text, string.Empty).TrimEnd();
        } while (text != previous);

        return text;
    }

    private static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    [GeneratedRegex(@"\s*(?:\([^()]*\)|\[[^\[\]]*\])$")]
    private static partial Regex TrailingGroup();
}
