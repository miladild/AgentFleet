using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// A check that passed although it ran no test. Node's test runner counts a file that registers no test() or it() calls as one
/// passing test named after the file, so `node --test test/cron.test.js` exits 0 for a script that only prints its results
/// (measured: a rescue model wrote such a script for a cron parser, it printed "ERROR: ... No match" for the one case that was
/// wrong, the check passed, the plan said done and the hidden tests failed). The dotnet runner passes a project without a test
/// the same way. Where a check's own output shows that every test it ran is a bare file, or that none was available, it proves nothing.
/// </summary>
internal static partial class CheckVacuity
{
    // node:test spec reporter: "✔ test/a.test.js (12.3ms)" at the start of the line, and "ℹ tests 1".
    [GeneratedRegex(@"(?m)^[✔✓]\s+(?<name>\S+?)\s+\([\d.]+m?s\)\s*$")]
    private static partial Regex SpecPass();

    [GeneratedRegex(@"(?m)^ℹ tests (?<count>\d+)\s*$")]
    private static partial Regex SpecTests();

    // node:test TAP reporter: "ok 1 - test/a.test.js" at the start of the line, and "# tests 1".
    [GeneratedRegex(@"(?m)^ok \d+ - (?<name>\S+)\s*$")]
    private static partial Regex TapPass();

    [GeneratedRegex(@"(?m)^# tests (?<count>\d+)\s*$")]
    private static partial Regex TapTests();

    [GeneratedRegex(@"(?i)^[\w./\\:-]+\.(?:[cm]?[jt]sx?)$")]
    private static partial Regex SourceFile();

    // dotnet test: "No test is available in ..." or "Passed!  - Failed:     0, Passed:     0, ..."
    [GeneratedRegex(@"(?i)No test is available in|Passed!\s+-\s+Failed:\s+0,\s+Passed:\s+0,")]
    private static partial Regex DotnetNone();

    /// <summary>True when the output of a passing check shows that it ran no test (see the class summary).</summary>
    public static bool NoTestsRan(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        if (DotnetNone().IsMatch(output))
        {
            return true;
        }

        return OnlyBareFiles(output, SpecPass(), SpecTests()) || OnlyBareFiles(output, TapPass(), TapTests());
    }

    // Every test the runner counted is a file passing under its own name.
    private static bool OnlyBareFiles(string output, Regex pass, Regex total)
    {
        Match counted = total.Match(output);
        if (!counted.Success || !int.TryParse(counted.Groups["count"].Value, out int tests) || tests < 1)
        {
            return false;
        }

        int bare = pass.Matches(output).Count(match => SourceFile().IsMatch(match.Groups["name"].Value));
        return bare >= tests;
    }
}
