using Xunit;

namespace AgentFleet.Tests;

public sealed class FailureHintsTests
{
    [Fact]
    public void A_reference_equality_failure_gets_the_deep_equal_hint()
    {
        const string output = "AssertionError [ERR_ASSERTION]: Values have same structure but are not reference-equal:\n\n[]\n";

        string? hint = FailureHints.For(output);

        Assert.NotNull(hint);
        Assert.Contains("deepStrictEqual", hint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AssertionError: Expected values to be strictly equal: 0 !== 1")]
    public void Other_failures_get_no_hint(string? output)
    {
        Assert.Null(FailureHints.For(output));
    }
}
