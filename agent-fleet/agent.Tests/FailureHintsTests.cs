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

    // ---- A fetch with no method is a GET ----------------------------------------------------------------------------

    private const string TestPath = "test\\api.test.ts";

    /// <summary>What node prints when an assertion on a response status fails, with the failing line of the test file in the stack.</summary>
    private static string StatusFailure(int line, string actual = "404", string expected = "204", string file = TestPath) =>
        "✖ delete returns 204 and a second delete returns 404 (26.8071ms)\n" +
        "  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:\n  \n" +
        $"  {actual} !== {expected}\n  \n" +
        $"      at TestContext.<anonymous> ({file}:{line}:12)\n" +
        "      at async Test.run (node:internal/test_runner/test:1313:7)";

    /// <summary>A small test file: the call under test on line 3, the assertion on line 4.</summary>
    private static string TestFile(string call, string assertion = "  assert.equal(res1.status, 204);") =>
        "import assert from \"node:assert/strict\";\n" +
        "test(\"delete returns 204\", async () => {\n" +
        $"  const res1 = await {call};\n" +
        assertion + "\n" +
        "});\n";

    private static string? HintFor(string failure, string? projectFile, string? text)
    {
        string folder = Directory.CreateTempSubdirectory("hints-").FullName;
        try
        {
            if (projectFile is not null && text is not null)
            {
                string path = Path.Combine(folder, projectFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }

            return FailureHints.For(failure, folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_fetch_with_no_method_whose_status_is_404_where_a_2xx_was_expected_is_named_to_the_worker()
    {
        string? hint = HintFor(StatusFailure(4), TestPath, TestFile("fetch(`${url}/api/alerts/${alertId}`)"));

        Assert.NotNull(hint);
        Assert.Contains("res1", hint);
        Assert.Contains("line 3", hint);
        Assert.Contains("no method", hint);
        Assert.Contains("GET", hint);
        Assert.Contains("method: \"DELETE\"", hint);
    }

    [Theory]
    [InlineData("fetch(url, { method: \"DELETE\" })")]
    [InlineData("fetch(url, { headers: {}, method: 'POST', body: \"{}\" })")]
    [InlineData("fetch(url, options)")]
    public void A_fetch_that_sets_a_method_or_passes_its_options_in_a_variable_gets_no_hint(string call)
    {
        Assert.Null(HintFor(StatusFailure(4), TestPath, TestFile(call)));
    }

    [Fact]
    public void A_fetch_call_spread_over_several_lines_is_read_to_its_closing_bracket()
    {
        string withMethod = TestFile("fetch(\n    `${url}/api/alerts/${alertId}`,\n    { method: \"DELETE\" }\n  )");
        string withoutMethod = TestFile("fetch(\n    `${url}/api/alerts/${alertId}`\n  )");

        Assert.Null(HintFor(StatusFailure(7), TestPath, withMethod));
        Assert.NotNull(HintFor(StatusFailure(6), TestPath, withoutMethod));
    }

    [Theory]
    [InlineData("404", "400")]
    [InlineData("500", "204")]
    [InlineData("200", "204")]
    public void Only_a_404_or_405_where_a_2xx_was_expected_gets_the_hint(string actual, string expected)
    {
        Assert.Null(HintFor(StatusFailure(4, actual, expected), TestPath, TestFile("fetch(url)")));
    }

    [Fact]
    public void Without_a_project_folder_or_with_a_file_that_cannot_be_read_there_is_no_hint()
    {
        Assert.Null(FailureHints.For(StatusFailure(4)));
        Assert.Null(HintFor(StatusFailure(4), null, null));
        Assert.Null(HintFor(StatusFailure(4, file: "test\\missing.test.ts"), TestPath, TestFile("fetch(url)")));
    }

    [Theory]
    [InlineData("..\\outside.test.ts")]
    [InlineData("../outside.test.ts")]
    [InlineData("C:\\Windows\\system.test.ts")]
    public void A_file_outside_the_project_folder_is_never_read(string file)
    {
        Assert.Null(HintFor(StatusFailure(4, file: file), TestPath, TestFile("fetch(url)")));
    }

    [Fact]
    public void An_assertion_that_does_not_read_a_response_status_gets_no_hint()
    {
        string text = TestFile("fetch(url)", "  assert.equal(await res1.text(), \"x\");");

        Assert.Null(HintFor(StatusFailure(4), TestPath, text));
    }

    [Fact]
    public void A_variable_that_is_not_a_fetch_result_gets_no_hint()
    {
        string text = "const res1 = await request(url);\nassert.equal(res1.status, 204);\n";

        Assert.Null(HintFor(StatusFailure(2), TestPath, text));
    }
}
