namespace AgentFleet.Tests;

public sealed class PlaceholderTestsDetectorTests
{
    private static string Widgets(int count) => string.Concat(Enumerable.Range(1, count).Select(index =>
        $"test(\"widget case {index}\", () => {{\n  assert.equal(widget({index}), {index});\n}});\n"));

    [Fact]
    public void Eight_placeholder_tests_are_flagged_with_their_line_numbers()
    {
        const string text = """
            test("GET /api/widget returns JSON with widget and count", () => {
              assert.ok(false); // Failing as expected since endpoint doesn't exist
            });
            test("widget case 2", () => {
              assert.ok(false); // Failing as expected
            });
            test("widget case 3", () => {
              assert.ok(false); // Failing as expected
            });
            test("widget case 4", () => {
              assert.ok(false); // Failing as expected
            });
            test("widget case 5", () => {
              assert.ok(false); // Failing as expected
            });
            test("widget case 6", () => {
              assert.ok(false); // Failing as expected
            });
            test("widget case 7", () => {
              assert.ok(false); // Failing as expected
            });
            test("widget case 8", () => {
              assert.ok(false); // Failing as expected
            });

            """;

        IReadOnlyList<string> found = PlaceholderTests.Find("tests/widget.test.js", text);

        Assert.Equal(8, found.Count);
        Assert.Equal("tests/widget.test.js line 2: assert.ok(false); // Failing as expected since endpoint doesn't exist", found[0]);
        Assert.Equal("tests/widget.test.js line 23: assert.ok(false); // Failing as expected", found[7]);
    }

    [Fact]
    public void A_single_placeholder_among_twelve_real_tests_is_not_a_placeholder_file()
    {
        string text = Widgets(12).Replace("  assert.equal(widget(1), 1);\n", "  if (false) {\n    assert.ok(false);\n  }\n  assert.equal(widget(1), 1);\n");

        Assert.Empty(PlaceholderTests.Find("tests/widget.test.js", text));
    }

    [Theory]
    [InlineData("assert.ok(falsePositives.length === 0);")]
    [InlineData("assert(falseColour);")]
    [InlineData("assert.equal(true, falsey);")]
    [InlineData("Assert.True(falseAlarms == 0);")]
    [InlineData("expect(true).toBe(falsy);")]
    public void A_statement_that_only_starts_like_a_placeholder_is_not_one(string statement)
    {
        string text = "test(\"checks a count\", () => {\n  " + statement + "\n});\n";

        Assert.Empty(PlaceholderTests.Find("tests/widget.test.js", text));
    }

    [Fact]
    public void Assert_fail_inside_a_try_block_is_not_a_placeholder()
    {
        const string text = """
            test("widget rejects a bad input", () => {
              try {
                widget(-1);
                assert.fail("the call must throw");
              } catch (error) {
                assert.match(error.message, /bad input/);
              }
            });

            """;

        Assert.Empty(PlaceholderTests.Find("tests/widget.test.js", text));
    }

    [Fact]
    public void Python_placeholders_are_a_placeholder_file_at_half_of_its_tests()
    {
        const string text = """
            def test_a(): assert False
            def test_b(): assert False
            def test_c(): assert add(1, 2) == 3
            def test_d(): assert add(2, 2) == 4

            """;

        IReadOnlyList<string> found = PlaceholderTests.Find("tests/test_add.py", text);

        Assert.Equal(["tests/test_add.py line 1: def test_a(): assert False", "tests/test_add.py line 2: def test_b(): assert False"], found);
    }

    [Fact]
    public void Python_one_placeholder_of_four_tests_is_not_a_placeholder_file()
    {
        const string text = """
            def test_a(): assert False
            def test_b(): assert add(1, 2) == 3
            def test_c(): assert add(2, 2) == 4
            def test_d(): assert add(3, 3) == 6

            """;

        Assert.Empty(PlaceholderTests.Find("tests/test_add.py", text));
    }

    [Fact]
    public void A_dotnet_fact_that_asserts_true_is_false_is_flagged()
    {
        const string text = """
            using Xunit;
            public class LimitTests
            {
                [Fact]
                public void Rejects_zero()
                {
                    Assert.True(false);
                }
            }

            """;

        IReadOnlyList<string> found = PlaceholderTests.Find("tests/LimitTests.cs", text);

        Assert.Equal(["tests/LimitTests.cs line 7: Assert.True(false);"], found);
    }

    [Theory]
    [InlineData("assert.ok(false);")]
    [InlineData("assert(false);")]
    [InlineData("assert.equal(true, false);")]
    [InlineData("assert.strictEqual(true, false);")]
    [InlineData("expect(true).toBe(false);")]
    [InlineData("expect(false).toBe(true);")]
    [InlineData("throw new Error(\"not implemented yet\");")]
    [InlineData("throw new Error('Not Implemented');")]
    [InlineData("throw new Error(`TODO write this`);")]
    [InlineData("self.assertTrue(False)")]
    [InlineData("self.assertFalse(True)")]
    [InlineData("assert False")]
    [InlineData("Assert.True(false);")]
    [InlineData("Assert.False(true);")]
    public void Each_placeholder_statement_form_is_recognised(string statement)
    {
        // The wrapper only supplies the one test that the placeholder is counted against.
        string text = "test(\"widget case\", () => {\n  " + statement + "\n});\n";

        Assert.Single(PlaceholderTests.Find("tests/widget.test.js", text));
    }

    [Theory]
    [InlineData("assert.fail(\"unreachable\");")]
    [InlineData("Assert.Fail(\"unreachable\");")]
    [InlineData("self.fail(\"unreachable\")")]
    [InlineData("pytest.fail(\"unreachable\")")]
    public void Explicit_failure_helpers_are_not_placeholders(string statement)
    {
        string text = "test(\"widget case\", () => {\n  " + statement + "\n});\n";

        Assert.Empty(PlaceholderTests.Find("tests/widget.test.js", text));
    }

    [Fact]
    public void Placeholder_statements_in_comment_lines_are_ignored()
    {
        const string text = """
            test("widget case 1", () => { assert.equal(widget(1), 1); });
            // assert.ok(false);
            test("widget case 2", () => { assert.equal(widget(2), 2); });
            # assert False
            test("widget case 3", () => { assert.equal(widget(3), 3); });
            // assert.ok(false);

            """;

        Assert.Empty(PlaceholderTests.Find("tests/widget.test.js", text));
    }

    [Fact]
    public void A_file_with_no_tests_is_never_a_placeholder_file()
    {
        Assert.Empty(PlaceholderTests.Find("tests/helpers.js", "export const value = 1;\n"));
        Assert.Empty(PlaceholderTests.Find("tests/helpers.js", "assert.ok(false);\n"));
    }

    [Fact]
    public void A_long_placeholder_line_is_cut_at_120_characters()
    {
        string statement = "assert.ok(false, \"" + new string('a', 200) + "\");";
        string text = "test(\"long\", () => {\n  " + statement + "\n});\n";

        IReadOnlyList<string> found = PlaceholderTests.Find("tests/long.test.js", text);

        Assert.Equal("tests/long.test.js line 2: " + statement[..120], Assert.Single(found));
    }
}
