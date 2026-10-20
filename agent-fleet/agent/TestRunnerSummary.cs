using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// What the final summaries of pytest, jest and vitest say about the tests that were counted. Total includes the skipped tests.
/// Found is set when one of those summaries was read; Jest and Vitest say which runner printed it.
/// </summary>
internal sealed record TestRunnerTotals(int Total, int Skipped, bool Found, bool Jest, bool Vitest, bool NoTestFiles, bool CollectionFailed);

/// <summary>
/// Reads the totals from the summaries of pytest, jest and vitest, the runners a tests step can use besides dotnet test and node --test.
/// Only lines that look exactly like a runner's summary count, so an assertion message or a test's own printed text does not change the totals.
/// </summary>
internal static partial class TestRunnerSummary
{
    // pytest: "2 failed, 2 passed, 1 skipped in 0.08s", framed by "=====" or bare with -q, or "no tests ran in 0.02s".
    [GeneratedRegex(@"(?im)^=*\s*(?<body>(?:\d+ (?:passed|failed|errors?|skipped|xfailed|xpassed|deselected|warnings?)(?:, )?)+)\s+in\s+\d+(?:\.\d+)?s(?:\s+\(\S+\))?\s*=*\s*$")]
    private static partial Regex PytestSummary();

    [GeneratedRegex(@"(?im)^=*\s*no tests ran in \d+(?:\.\d+)?s\s*=*\s*$")]
    private static partial Regex PytestNoTests();

    // jest: "Tests:       2 failed, 1 skipped, 2 passed, 5 total"; vitest: "      Tests  1 failed | 1 passed | 1 skipped (3)" or "Tests  no tests".
    [GeneratedRegex(@"(?m)^Tests:[ \t]+(?<body>[^\r\n]*\S)[ \t]*$")]
    private static partial Regex JestTests();

    [GeneratedRegex(@"(?m)^[ \t]*Tests[ \t]+(?<body>(?:\d+ (?:passed|failed|skipped|todo)(?:[ \t]*\|[ \t]*|[ \t]+)?)+(?:\(\d+\))?|no tests)[ \t]*$")]
    private static partial Regex VitestTests();

    [GeneratedRegex(@"(?<count>\d+) (?<word>passed|failed|skipped|todo|pending|total)\b")]
    private static partial Regex CountItem();

    [GeneratedRegex(@"(?im)(?<count>\d+) (?<word>passed|failed|xfailed|xpassed|skipped|errors?|deselected|warnings?)\b")]
    private static partial Regex PytestItem();

    [GeneratedRegex(@"^No tests? (?:files? )?found, exiting with code", RegexOptions.Multiline)]
    private static partial Regex NoTestFilesFound();

    // A collection error is a test file that could not be loaded, so none of its tests ran. The markers are printed beside the summary.
    [GeneratedRegex(@"ERROR collecting |error during collection|^\s*●\s+Test suite failed to run|Failed Suites \d+", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CollectionFailure();

    internal static TestRunnerTotals Read(string? output)
    {
        string text = output ?? string.Empty;
        int total = 0;
        int skipped = 0;
        bool found = false;
        bool jest = false;
        bool vitest = false;

        foreach (Match summary in PytestSummary().Matches(text))
        {
            found = true;
            foreach (Match item in PytestItem().Matches(summary.Groups["body"].Value))
            {
                if (!int.TryParse(item.Groups["count"].Value, out int count)) continue;
                switch (item.Groups["word"].Value.ToLowerInvariant())
                {
                    // An xfailed or xpassed test ran; a deselected test was not collected and a collection error is not a test.
                    case "passed" or "failed" or "xfailed" or "xpassed":
                        total += count;
                        break;
                    case "skipped":
                        total += count;
                        skipped += count;
                        break;
                }
            }
        }

        if (PytestNoTests().IsMatch(text)) found = true;

        foreach (Match summary in JestTests().Matches(text))
        {
            found = jest = true;
            (int count, int skip) = CountSummary(summary.Groups["body"].Value);
            total += count;
            skipped += skip;
        }

        foreach (Match summary in VitestTests().Matches(text))
        {
            found = vitest = true;
            (int count, int skip) = CountSummary(summary.Groups["body"].Value);
            total += count;
            skipped += skip;
        }

        bool noTestFiles = NoTestFilesFound().IsMatch(text);
        return new TestRunnerTotals(total, skipped, found, jest, vitest, noTestFiles, found && CollectionFailure().IsMatch(text));
    }

    // jest and vitest items: the total is printed by jest ("5 total") or is the sum of the items (vitest). Todo and pending tests are skipped.
    private static (int Count, int Skipped) CountSummary(string body)
    {
        int explicitTotal = -1;
        int sum = 0;
        int skipped = 0;
        foreach (Match item in CountItem().Matches(body))
        {
            if (!int.TryParse(item.Groups["count"].Value, out int count)) continue;
            switch (item.Groups["word"].Value)
            {
                case "total":
                    explicitTotal = count;
                    break;
                case "passed" or "failed":
                    sum += count;
                    break;
                case "skipped" or "todo" or "pending":
                    sum += count;
                    skipped += count;
                    break;
            }
        }

        return (explicitTotal >= 0 ? explicitTotal : sum, skipped);
    }
}
