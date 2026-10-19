using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

internal enum TestFirstMode { Off, On }

internal sealed record TestFirstDecision(string Outcome, int Executed, int Failing, string Feedback)
{
    public bool Accepted => Outcome == "accepted";
}

internal static partial class TestFirst
{
    internal static TestFirstMode ParseMode(string? value, ILogger? logger)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase)) return TestFirstMode.Off;
        if (string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase)) return TestFirstMode.On;
        logger?.LogWarning("Unknown FLEET_PLAN_TEST_FIRST value '{Value}'. Accepted values are: off, on. Ignoring it and using off.", value);
        return TestFirstMode.Off;
    }

    [GeneratedRegex(@"(?im)\bTotal:\s*(?<count>\d+)|^[#ℹ]\s*tests\s+(?<count>\d+)\s*$")]
    private static partial Regex TestTotal();

    [GeneratedRegex(@"(?im)^Exit code:\s*(?<code>-?\d+)\s*$")]
    private static partial Regex ExitCode();

    // Anchored to the start of a line (or the TAP comment, error or yaml field that starts one), so an assertion message that
    // merely quotes the phrase is still read as a test that ran.
    [GeneratedRegex(@"(?im)^\s*[#ℹ]?\s*(?:\S*Error\b[^\r\n]*?:\s*|error:\s*['""]?)?(?:ModuleNotFoundError|ImportError|ERR_MODULE_NOT_FOUND|Cannot find module|No tests? (?:found|ran|collected|is available))")]
    private static partial Regex NoExecution();

    [GeneratedRegex(@"(?i)(?:^|[/\\])\S+\.(?:[cm]?[jt]sx?|py|cs)(?::\d+)?$")]
    private static partial Regex BareTestFile();

    [GeneratedRegex(@"(?m)^\s*not ok \d+(?:\s*-\s*|\s+)(?<name>.+?)\s*$")]
    private static partial Regex TapFailures();

    internal static TestFirstDecision Evaluate(string? output, IReadOnlyList<string>? changedFiles)
    {
        string text = output ?? string.Empty;
        // Several test projects print one summary each: the tests they ran add up, so one project with no tests cannot hide another.
        int total = TestTotal().Matches(text).Sum(match => int.TryParse(match.Groups["count"].Value, out int count) ? count : 0);
        int skipped = Regex.Matches(text, @"(?im)\bSkipped:\s*(?<count>\d+)|^[#ℹ]\s*skipped\s+(?<count>\d+)\s*$")
            .Sum(match => int.TryParse(match.Groups["count"].Value, out int count) ? count : 0);
        int executed = Math.Max(0, total - skipped);
        // A node file-level line ("not ok 1 - tests/a.test.js") names the file, not a test, so it never becomes a failing test name.
        string failures = TapFailures().Replace(text, match => BareTestFile().IsMatch(match.Groups["name"].Value) ? match.Value : "FAILED " + match.Groups["name"].Value);
        FailureFingerprint signature = PlanFailure.FailureSignature(failures);
        int failing = signature.Items.Count(item => !PlanFailure.IsBuildErrorCode(item) && !BareTestFile().IsMatch(item));
        TestFirstDecision Decision(string outcome, string feedback) => new(outcome, executed, failing, feedback);

        if (changedFiles is null) return Decision("unreadable", "the changed files could not be checked; report_blocker before continuing");
        if (changedFiles.Count == 0) return Decision("no-test-changes", "no test file was written or changed in this step: write the tests this step names");
        if (changedFiles.FirstOrDefault(path => !PlanRunner.IsTestPath(path)) is { } implementation)
            return Decision("non-test-changed", $"a non-test file changed: {implementation}; restore it and write only tests");
        if (PlanFailure.BuildBroken(text) || signature.Items.Any(PlanFailure.IsBuildErrorCode))
            return Decision("build-error", "the check has a build or syntax error, not a failing behaviour test; fix the test setup or use report_blocker");
        if (CheckVacuity.NoTestsRan(text) || NoExecution().IsMatch(text) || TestTotal().IsMatch(text) && executed == 0)
            return Decision("no-test-ran", "no test ran: if the tests cannot import the code under test, say so with report_blocker");
        Match exit = ExitCode().Match(text);
        if (executed < 1 || !exit.Success || !int.TryParse(exit.Groups["code"].Value, out int code))
            return Decision("unreadable", "no test ran: if the tests cannot import the code under test, say so with report_blocker");
        if (code == 0)
            return Decision("tests-pass", "these tests already pass, so they do not describe new behaviour; test what this step must make true");
        if (failing < 1)
            return Decision("unreadable", "no test ran: if the tests cannot import the code under test, say so with report_blocker");
        return Decision("accepted", "The new tests ran and failed on the missing behaviour as required.");
    }

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".go", ".rs", ".java", ".kt", ".rb", ".php", ".c", ".cpp", ".h"
    };

    internal static IEnumerable<string> PlanProblems(IReadOnlyList<PlanStepInput> steps)
    {
        bool earlierTests = false;
        for (int index = 0; index < steps.Count; index++)
        {
            PlanStepInput step = steps[index];
            string label = $"Step {index + 1} ({step.Title})";
            if (!string.IsNullOrWhiteSpace(step.Kind) && step.Kind != "tests")
                yield return $"{label} has unknown kind '{step.Kind}': use tests or leave it empty.";
            if (step.Kind == "tests")
            {
                if (step.Files is not { Length: > 0 }) yield return $"{label} is a tests step with no files.";
                foreach (string path in step.Files ?? [])
                    if (!PlanRunner.IsTestPath(path)) yield return $"{label}: {path} is not a test path.";
                if (string.IsNullOrWhiteSpace(step.Verify)) yield return $"{label} is a tests step with no verify command.";
                if (index == steps.Count - 1) yield return $"{label}: the last step must run the whole project's checks and cannot be a tests step.";
                earlierTests = true;
            }
            else if (!earlierTests && (step.Files ?? []).Any(path => !PlanRunner.IsTestPath(path) && CodeExtensions.Contains(Path.GetExtension(path))) && !IsSkeleton(step))
                yield return $"{label} changes behaviour and needs an earlier tests step.";
        }
    }

    // A skeleton has no behaviour to test yet; its signatures must build before the tests can import them.
    private static bool IsSkeleton(PlanStepInput step) =>
        step.Detail.Contains("skeleton", StringComparison.OrdinalIgnoreCase) &&
        step.Detail.Contains("not implemented", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(step.Verify ?? string.Empty, @"^(?:dotnet build|(?:npx )?tsc --noEmit|(?:python|python3|py -3) -m py_compile\s+\S+|node --check\s+\S+)$", RegexOptions.IgnoreCase);
}
