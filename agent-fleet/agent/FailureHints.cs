namespace AgentFleet;

/// <summary>
/// Short plain-language hints for failures whose printed text names their own cause, added to the failure a worker is sent back
/// with. Only causes that were measured in real runs belong here, and each hint says what to change, not who is to blame.
/// </summary>
internal static class FailureHints
{
    private const string ReferenceEqualMarker = "not reference-equal";

    /// <summary>
    /// The hint for a check's failure text, or null when no rule applies. node's assert prints "Values have same structure but are not
    /// reference-equal" when a test compares two arrays or objects with strictEqual or equal: the values match, the comparison is wrong.
    /// </summary>
    internal static string? For(string? failure)
    {
        if (string.IsNullOrEmpty(failure) || !failure.Contains(ReferenceEqualMarker, StringComparison.Ordinal))
        {
            return null;
        }

        return "Hint: the failure says the two values have the same structure but are not reference-equal. The test compares an array or " +
               "object with assert.strictEqual or assert.equal, which compares references and can never pass for two separate arrays. " +
               "Change that assertion to assert.deepStrictEqual (or compare the length or the items). The code under test is probably right.";
    }
}
