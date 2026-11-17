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
    [InlineData("it")]
    [InlineData("describe")]
    public void A_test_that_uses_an_unimported_node_test_name_is_told_to_import_it(string name)
    {
        string output = $"failing tests: ReferenceError: {name} is not defined\n at SuiteContext.<anonymous> (test\\a.test.ts:24:3)\n" +
                        "at Suite.runInAsyncScope (node:async_hooks:227:14)\nat new Suite (node:internal/test_runner/test:1693:28)";

        string? hint = FailureHints.For(output);

        Assert.NotNull(hint);
        Assert.Contains($"uses {name} but does not import it", hint);
        Assert.Contains("node:test", hint);
    }

    [Fact]
    public void An_undefined_name_outside_the_node_test_runner_gets_no_import_hint()
    {
        Assert.Null(FailureHints.For("ReferenceError: it is not defined\n at Object.<anonymous> (app.js:3:1)"));
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
