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

        IReadOnlyList<string> items = FailingItems(checkOutput);
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
    /// Every failing item of a check, with no limit, for the dispute rules. The shared reading of the failing lines is used on
    /// the output without the spec reporter's suite summaries and failing-tests header, which name no test. The dispute path
    /// adds the jest failure headings and the TAP "not ok" test lines, which the shared reading does not read. Only the dispute
    /// path does this, so the stored repair signatures do not change.
    /// </summary>
    internal static IReadOnlyList<string> FailingItems(string? checkOutput)
    {
        string output = AnsiEscape().Replace(checkOutput ?? string.Empty, string.Empty);
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var items = new SortedSet<string>(PlanFailure.FailingItems(WithoutRunnerSummaries(lines)), StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            if (JestFailureHeading().Match(line) is { Success: true } heading)
            {
                items.Add(heading.Groups["name"].Value.Trim());
            }
        }

        foreach (string name in TapFailedTests(lines))
        {
            items.Add(name);
        }

        return items.ToArray();
    }

    // The spec reporter prints a suite as "▶ name" with its tests under it, and repeats a failing suite as "✖ name (time)" at the
    // start of the line. That line and the "✖ failing tests:" header name no test, so they are left out of the reading. Jest's
    // "FAIL ./file" header names no test either; its "● suite › test" headings carry the failures. A file that did not load keeps
    // its line (vitest prints it with a bracketed name), since the step's own test file may be the one that did not load.
    private static string WithoutRunnerSummaries(string[] lines)
    {
        var suites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            if (SpecSuiteHeading().Match(line) is { Success: true } suite)
            {
                suites.Add(suite.Groups["name"].Value.Trim());
            }
        }

        var kept = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            if (SpecFailingHeader().IsMatch(line) || JestFileHeader().IsMatch(line)) continue;
            if (SpecSuiteSummary().Match(line) is { Success: true } summary && suites.Contains(summary.Groups["name"].Value.Trim())) continue;
            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    // A TAP test is a "not ok N - name" line, indented under its suite. A suite's own line, and a file-level line that names a
    // test file, name no test; the suite's diagnostic block says it is a suite.
    private static IEnumerable<string> TapFailedTests(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            if (TapNotOk().Match(lines[index]) is not { Success: true } failed) continue;
            string name = failed.Groups["name"].Value.Trim();
            if (BareTestFile().IsMatch(name) || IsTapSuite(lines, index)) continue;
            yield return name;
        }
    }

    private static bool IsTapSuite(string[] lines, int index)
    {
        for (int next = index + 1; next < lines.Length; next++)
        {
            if (lines[next].Trim() == "...") return false;
            if (TapNotOk().IsMatch(lines[next]) || TapOk().IsMatch(lines[next])) return false;
            if (lines[next].Trim() == "type: 'suite'") return true;
        }

        return false;
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
        text = TrimTrailingGroups(message > 0 ? text[..message] : text);
        // A vitest tree line ("× name 6ms") reads as "name <time>" once its timing is normalised.
        return TrailingTime().Replace(text, string.Empty).TrimEnd();
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

    [GeneratedRegex(@"\s+<time>$")]
    private static partial Regex TrailingTime();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"^\s*●\s+(?<name>.+?)\s*$")]
    private static partial Regex JestFailureHeading();

    [GeneratedRegex(@"^▶\s+(?<name>.+?)\s*$")]
    private static partial Regex SpecSuiteHeading();

    [GeneratedRegex(@"^✖ failing tests:\s*$")]
    internal static partial Regex SpecFailingHeader();

    [GeneratedRegex(@"^\s*FAIL\s+\./\S+\s*$")]
    private static partial Regex JestFileHeader();

    [GeneratedRegex(@"^✖ (?<name>.+?)\s*(?:\(\s*\d+(?:\.\d+)?\s*(?:ms|s)\s*\))?\s*$")]
    private static partial Regex SpecSuiteSummary();

    [GeneratedRegex(@"^\s*not ok \d+(?:\s*-\s*|\s+)(?<name>.+?)\s*$")]
    private static partial Regex TapNotOk();

    [GeneratedRegex(@"^\s*ok \d+\b")]
    private static partial Regex TapOk();

    [GeneratedRegex(@"(?i)(?:^|[/\\])\S+\.(?:[cm]?[jt]sx?|py|cs)(?::\d+)?$")]
    internal static partial Regex BareTestFile();
}
