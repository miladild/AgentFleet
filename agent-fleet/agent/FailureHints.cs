using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// Short plain-language hints for failures whose printed text names their own cause, added to the failure a worker is sent back
/// with. Only causes that were measured in real runs belong here, and each hint says what to change, not who is to blame.
/// </summary>
internal static partial class FailureHints
{
    private const string ReferenceEqualMarker = "not reference-equal";

    // A name that node's test module exports, used in a test file that did not import it: "ReferenceError: it is not defined".
    [GeneratedRegex(@"ReferenceError(?: \[Error\])?: (?<name>it|describe|test|before|after|beforeEach|afterEach|mock) is not defined")]
    private static partial Regex TestApiNotImported();

    /// <summary>
    /// The hints for a check's failure text, or null when no rule applies. node's assert prints "Values have same structure but are
    /// not reference-equal" when a test compares two arrays or objects with strictEqual or equal: the values match, the comparison is
    /// wrong. A test that uses it, describe or another export of node:test without importing it fails with "X is not defined" and
    /// runs no test at all.
    /// </summary>
    internal static string? For(string? failure)
    {
        if (string.IsNullOrEmpty(failure))
        {
            return null;
        }

        var hints = new List<string>();
        if (failure.Contains(ReferenceEqualMarker, StringComparison.Ordinal))
        {
            hints.Add("Hint: the failure says the two values have the same structure but are not reference-equal. The test compares an array or " +
                      "object with assert.strictEqual or assert.equal, which compares references and can never pass for two separate arrays. " +
                      "Change that assertion to assert.deepStrictEqual (or compare the length or the items). The code under test is probably right.");
        }

        if (failure.Contains("node:internal/test_runner", StringComparison.Ordinal) && TestApiNotImported().Match(failure) is { Success: true } missing)
        {
            string name = missing.Groups["name"].Value;
            hints.Add($"Hint: the test file uses {name} but does not import it, so no test runs. Add {name} to the import from \"node:test\" " +
                      $"at the top of the test file (for example: import {{ describe, it }} from \"node:test\"). Change only that import line.");
        }

        return hints.Count == 0 ? null : string.Join("\n\n", hints);
    }
}
