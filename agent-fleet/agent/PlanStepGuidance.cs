using System.Text.RegularExpressions;

namespace AgentFleet;

internal static class PlanStepGuidance
{
    private static readonly Regex TestFilePattern = new(
        @"(^|/)(tests?|__tests__|spec)/|\.(test|spec)\.[A-Za-z0-9]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether the tests-only prompt guidance is enabled by its configuration value.</summary>
    public static bool IsEnabled(string? value) => string.Equals(value, "tests", StringComparison.OrdinalIgnoreCase);

    public static bool AppliesTo(IEnumerable<string> files) =>
        files.Any(file => TestFilePattern.IsMatch(file.Replace('\\', '/')));

    /// <summary>Written after the semver benchmark exposed the gap; judge it on held-out tasks.</summary>
    public const string Text = "Writing the tests for this step: write at least one test for every rule this step states, and name each test after the rule it checks. Before you stop, check your tests against this list wherever the step's rules make it meaningful: empty input; a single element, also when that single element is the one that must be rejected; the first, the last and the only element being invalid; both sides of every boundary (zero, one, the maximum); input that must not be changed; a result that must be a new object; the error type and message for each kind of invalid input, through every entry point (a function that validates must validate even when nothing would be compared). Assert only what the step states. When a test of yours fails, read the step text again before you change the code: either the code or the test may be wrong, and the step text decides.";
}
