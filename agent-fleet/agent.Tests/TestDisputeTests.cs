using System.Text;

namespace AgentFleet.Tests;

// The pure rules of a dispute: when a ruling is due, how a ruler's reply is read, and what a correction may change.
public sealed class TestDisputeTests : PlanTestBase
{
    private const string LimitTestsText =
        "public class LimitTests\n" +
        "{\n" +
        "    [Fact]\n" +
        "    public void Rejects_over_limit() { }\n" +
        "\n" +
        "    [Fact]\n" +
        "    public void Accepts_at_limit() { }\n" +
        "}\n";

    private const string RejectsFailure = "Failed LimitTests.Rejects_over_limit [5 ms]";
    private static readonly TestFileText Frozen = new("tests/LimitTests.cs", LimitTestsText);
    private static readonly TestFileText[] FrozenFiles = [Frozen];

    private static TestRulingDue Due(string? output = RejectsFailure, bool testFirstOn = true, bool ordinary = true, bool hasFrozen = true,
        bool disputed = true, int rulings = 0, int asks = 0, IReadOnlyList<TestFileText>? frozen = null) =>
        TestDispute.Evaluate(testFirstOn, ordinary, hasFrozen, disputed, rulings, asks, output, frozen ?? FrozenFiles);

    [Fact]
    public void A_step_may_ask_the_ruler_at_most_twice_without_a_usable_answer()
    {
        Assert.True(Due(asks: 1).Due);

        TestRulingDue due = Due(asks: 2);

        Assert.False(due.Due);
        Assert.Equal("too-many-asks", due.Reason);
    }

    [Fact]
    public void A_ruling_is_due_when_every_condition_holds()
    {
        TestRulingDue due = Due();

        Assert.True(due.Due);
        Assert.Equal("due", due.Reason);
    }

    [Fact]
    public void The_setting_off_never_rules()
    {
        TestRulingDue due = Due(testFirstOn: false);

        Assert.False(due.Due);
        Assert.Equal("off", due.Reason);
    }

    [Fact]
    public void A_tests_step_is_not_ruled_on()
    {
        TestRulingDue due = Due(ordinary: false);

        Assert.False(due.Due);
        Assert.Equal("not-ordinary", due.Reason);
    }

    [Fact]
    public void A_plan_without_frozen_files_is_not_ruled_on()
    {
        TestRulingDue due = Due(hasFrozen: false, frozen: []);

        Assert.False(due.Due);
        Assert.Equal("no-frozen-files", due.Reason);
    }

    [Fact]
    public void No_dispute_means_no_ruling()
    {
        TestRulingDue due = Due(disputed: false);

        Assert.False(due.Due);
        Assert.Equal("no-dispute", due.Reason);
    }

    [Fact]
    public void A_second_ruling_for_the_same_step_is_not_due()
    {
        TestRulingDue due = Due(rulings: 1);

        Assert.False(due.Due);
        Assert.Equal("already-ruled", due.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Without_failing_items_there_is_nothing_to_rule_on(string? output)
    {
        TestRulingDue due = Due(output: output);

        Assert.False(due.Due);
        Assert.Equal("no-failing-items", due.Reason);
    }

    [Fact]
    public void A_build_error_is_the_code_fault_and_not_ruled_on()
    {
        TestRulingDue due = Due(output: "src/limit.cs(3,5): error CS1002: ; expected\n" + RejectsFailure);

        Assert.False(due.Due);
        Assert.Equal("build-error", due.Reason);
    }

    [Fact]
    public void One_failure_outside_the_frozen_tests_makes_the_ruling_not_due()
    {
        TestRulingDue due = Due(output: RejectsFailure + "\nFailed NewTests.Brand_new_case [3 ms]");

        Assert.False(due.Due);
        Assert.Equal("not-a-frozen-test", due.Reason);
    }

    [Fact]
    public void Unreadable_output_that_names_no_frozen_test_is_not_ruled_on()
    {
        TestRulingDue due = Due(output: "something went wrong somewhere");

        Assert.False(due.Due);
        Assert.Equal("no-failing-items", due.Reason);
    }

    [Fact]
    public void Names_are_matched_without_regard_to_case_punctuation_or_spaces()
    {
        TestRulingDue due = Due(output: "FAILED limittests.rejects over limit");

        Assert.True(due.Due);
    }

    [Theory]
    [InlineData("LimitTests.Rejects_over_limit", "Rejects_over_limit", true)]
    [InlineData("rejects over limit", "Rejects_over_limit", true)]
    [InlineData("Accepts_at_limit", "Rejects_over_limit", false)]
    [InlineData("Rejects_over_limit_twice", "Rejects_over_limit", false)]
    [InlineData("Anything", "--", false)]
    [InlineData("", "Rejects_over_limit", false)]
    public void An_item_matches_a_test_name_when_it_equals_or_contains_it(string item, string name, bool expected)
    {
        Assert.Equal(expected, TestDispute.Matches(item, name));
    }

    [Fact]
    public void A_dispute_is_read_from_the_reply_calls()
    {
        var reply = new StepAgentReply("", 1, false, [new AgentToolCall("dispute_test", new Dictionary<string, string?>
        {
            ["tests"] = "Rejects_over_limit",
            ["reason"] = "expects 10 but the task says 11"
        })]);

        TestDisputeCall call = TestDispute.DisputeFrom(reply)!;

        Assert.Equal("Rejects_over_limit", call.Tests);
        Assert.Equal("expects 10 but the task says 11", call.Reason);
        Assert.Null(TestDispute.DisputeFrom(new StepAgentReply("", 0, false)));
    }

    [Fact]
    public void A_test_wrong_verdict_with_one_file_is_read_with_its_complete_content()
    {
        string reply = "VERDICT: TEST-WRONG\nFILE: tests/LimitTests.cs\n```csharp\npublic class LimitTests { }\n```\n";

        TestRulingVerdict verdict = TestRuling.Parse(reply);

        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        TestFileText file = Assert.Single(verdict.Files);
        Assert.Equal("tests/LimitTests.cs", file.Path);
        Assert.Equal("public class LimitTests { }\n", file.Text);
    }

    [Fact]
    public void A_test_wrong_verdict_reads_every_file_block()
    {
        string reply = "VERDICT: TEST-WRONG\nFILE: tests/A.cs\n```\na\n```\nFILE: tests/B.cs\n```\nb\n```";

        TestRulingVerdict verdict = TestRuling.Parse(reply);

        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(["tests/A.cs", "tests/B.cs"], verdict.Files.Select(file => file.Path));
    }

    [Fact]
    public void A_code_wrong_verdict_gives_the_paragraph()
    {
        TestRulingVerdict verdict = TestRuling.Parse("VERDICT: CODE-WRONG\n\nThe limit must reject the eleventh call.\n");

        Assert.Equal(TestRulingKind.CodeWrong, verdict.Kind);
        Assert.Equal("The limit must reject the eleventh call.", verdict.Paragraph);
    }

    [Theory]
    [InlineData("verdict: test-wrong\nFILE: tests/A.cs\n```\na\n```")]
    [InlineData("Verdict: Code-Wrong\nThe code must reject it.")]
    public void The_verdict_is_read_whatever_its_case(string reply)
    {
        Assert.NotEqual(TestRulingKind.NoVerdict, TestRuling.Parse(reply).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I think the test is probably wrong.")]
    [InlineData("FILE: tests/A.cs\n```\na\n```")]
    [InlineData("The verdict: TEST-WRONG\nFILE: tests/A.cs\n```\na\n```")]
    [InlineData("VERDICT: TEST-WRONG\nFILE: tests/A.cs\n```\na\n")]
    [InlineData("VERDICT: TEST-WRONG\nFILE: tests/A.cs\nno fence here")]
    [InlineData("VERDICT: TEST-WRONG\nNo file is named here.")]
    [InlineData("VERDICT: CODE-WRONG\n")]
    [InlineData("VERDICT: TEST-WRONG\nFILE: tests/A.cs\n```\na\n```\nVERDICT: CODE-WRONG\nThe code is wrong.")]
    [InlineData("VERDICT: CODE-WRONG\nThe limit must refuse zero.\nVERDICT: TEST-WRONG\nFILE: tests/A.cs\n```\na\n```")]
    [InlineData("VERDICT: TEST-WRONG\nFILE: tests/A.cs\n```\na\n```\nVERDICT: TEST-WRONG\nFILE: tests/B.cs\n```\nb\n```")]
    [InlineData("VERDICT: CODE-WRONG\nThe limit must refuse zero.\nVERDICT: CODE-WRONG\nAgain.")]
    public void Anything_that_cannot_be_read_is_no_verdict(string? reply)
    {
        Assert.Equal(TestRulingKind.NoVerdict, TestRuling.Parse(reply).Kind);
    }

    private static TestFileText Limit(string text) => new("tests/LimitTests.cs", text);

    [Fact]
    public void A_correction_that_keeps_every_name_and_fixes_the_value_is_accepted()
    {
        string corrected = LimitTestsText.Replace("{ }\n\n", "{ Assert.True(true); }\n\n", StringComparison.Ordinal);

        Assert.Null(TestRuling.Guard(FrozenFiles, [Limit(corrected)], [RejectsFailure]));
    }

    [Fact]
    public void A_correction_to_a_file_that_is_not_frozen_is_refused()
    {
        string? reason = TestRuling.Guard(FrozenFiles, [new TestFileText("tests/Other.cs", LimitTestsText)], [RejectsFailure]);

        Assert.NotNull(reason);
        Assert.Contains("frozen", reason);
    }

    [Fact]
    public void A_correction_to_a_frozen_file_that_holds_no_failing_test_is_refused()
    {
        TestFileText other = new("tests/OtherTests.cs", "public class OtherTests\n{\n    [Fact]\n    public void Holds_steady() { }\n}\n");

        string? reason = TestRuling.Guard([Frozen, other], [new TestFileText("tests/OtherTests.cs", "public class OtherTests { }\n")], [RejectsFailure]);

        Assert.NotNull(reason);
        Assert.Contains("failing", reason);
    }

    [Fact]
    public void A_correction_path_must_match_the_frozen_path_exactly()
    {
        string? reason = TestRuling.Guard(FrozenFiles, [new TestFileText("tests/limittests.cs", LimitTestsText)], [RejectsFailure]);

        Assert.NotNull(reason);
    }

    [Fact]
    public void An_empty_correction_is_refused()
    {
        Assert.NotNull(TestRuling.Guard(FrozenFiles, [Limit("   \n")], [RejectsFailure]));
        Assert.NotNull(TestRuling.Guard(FrozenFiles, [], [RejectsFailure]));
    }

    [Fact]
    public void A_correction_may_grow_to_twice_the_old_size_plus_4000_characters_and_no_more()
    {
        int limit = 2 * LimitTestsText.Length + 4000;
        string atLimit = LimitTestsText + new string('x', limit - LimitTestsText.Length);
        string overLimit = atLimit + "x";

        Assert.Null(TestRuling.Guard(FrozenFiles, [Limit(atLimit)], [RejectsFailure]));
        Assert.NotNull(TestRuling.Guard(FrozenFiles, [Limit(overLimit)], [RejectsFailure]));
    }

    [Fact]
    public void A_correction_that_drops_a_test_not_under_dispute_is_refused()
    {
        string corrected = LimitTestsText.Replace("    [Fact]\n    public void Accepts_at_limit() { }\n", string.Empty, StringComparison.Ordinal);

        string? reason = TestRuling.Guard(FrozenFiles, [Limit(corrected)], [RejectsFailure]);

        Assert.NotNull(reason);
        Assert.Contains("Accepts_at_limit", reason);
    }

    private static string Numbered(int count) => string.Concat(Enumerable.Range(1, count).Select(number => "// line " + number + "\n"));

    // A long test file: the test under dispute, then comment lines that a correction may or may not remove.
    private static string LongTest(int comments) =>
        "public class LimitTests\n{\n    [Fact]\n    public void Rejects_over_limit() { }\n" + Numbered(comments) + "}\n";

    [Fact]
    public void A_correction_that_removes_at_most_the_line_limit_is_accepted_and_one_more_is_refused()
    {
        // 45 lines old: the limit is the larger of 10 and 30 percent of 45 (13.5 lines).
        string old = LongTest(40);
        string thirteen = LongTest(27);
        string fourteen = LongTest(26);

        Assert.Null(TestRuling.Guard([new TestFileText("tests/LimitTests.cs", old)], [new TestFileText("tests/LimitTests.cs", thirteen)], [RejectsFailure]));
        string? reason = TestRuling.Guard([new TestFileText("tests/LimitTests.cs", old)], [new TestFileText("tests/LimitTests.cs", fourteen)], [RejectsFailure]);

        Assert.NotNull(reason);
        Assert.Contains("the correction would change too much of the old file to be a correction", reason);
    }

    [Fact]
    public void A_file_over_the_comparison_limit_is_refused_as_too_large_to_compare()
    {
        string old = LongTest(4001);

        string? reason = TestRuling.Guard([new TestFileText("tests/LimitTests.cs", old)], [new TestFileText("tests/LimitTests.cs", old + "// one more\n")], [RejectsFailure]);

        Assert.NotNull(reason);
        Assert.Contains("too large to compare", reason);
    }

    [Fact]
    public void A_correction_may_drop_the_test_under_dispute()
    {
        string corrected = LimitTestsText.Replace("    [Fact]\n    public void Rejects_over_limit() { }\n\n", string.Empty, StringComparison.Ordinal);

        Assert.Null(TestRuling.Guard(FrozenFiles, [Limit(corrected)], [RejectsFailure]));
    }

    [Fact]
    public void The_prompt_says_what_the_ruler_may_do_and_how_to_answer()
    {
        PlanRecord plan = NewPlan(Step("limit"));
        plan = plan with { Goal = "Limit calls to ten a minute", Requirements = [new PlanRequirement("R1", "The eleventh call in a minute is refused", [])] };
        string output = "FAILED LimitTests.Rejects_over_limit\n" + plan.WorkingDirectory + "\\tests\\LimitTests.cs(4,10): error";

        string prompt = TestRuling.BuildPrompt(plan, plan.Steps[0], FrozenFiles, output, "expects 10 but the task says 11", []);

        Assert.Contains("did not write", prompt);
        Assert.Contains("VERDICT: TEST-WRONG", prompt);
        Assert.Contains("VERDICT: CODE-WRONG", prompt);
        Assert.Contains("FILE: <project-relative path>", prompt);
        Assert.Contains("Limit calls to ten a minute", prompt);
        Assert.Contains("The eleventh call in a minute is refused", prompt);
        Assert.Contains("expects 10 but the task says 11", prompt);
        Assert.Contains("tests\\LimitTests.cs(4,10)", prompt);
        Assert.DoesNotContain("C:\\work\\tests", prompt);
    }

    [Fact]
    public void The_changed_files_in_the_prompt_stay_within_their_cap()
    {
        PlanRecord plan = NewPlan(Step("limit"));
        // A marker character that no other part of the prompt uses, so the count is the changed files' text alone.
        TestFileText[] changed = [new("src/a.cs", new string('~', 15000)), new("src/b.cs", new string('~', 15000)), new("src/c.cs", new string('~', 15000))];

        string prompt = TestRuling.BuildPrompt(plan, plan.Steps[0], FrozenFiles, RejectsFailure, "reason", changed);

        int shown = prompt.Count(character => character == '~');
        Assert.True(shown <= 20000, $"the changed files take {shown} characters");
        Assert.True(shown >= 19000, $"the changed files take only {shown} characters, so the cap was not used");
        Assert.Contains("[the rest is cut]", prompt);
    }

    [Fact]
    public void The_prompt_cuts_long_test_files_and_output()
    {
        PlanRecord plan = NewPlan(Step("limit"));
        string longTest = "// " + new string('t', 20000);
        string longOutput = new string('o', 9000);

        string prompt = TestRuling.BuildPrompt(plan, plan.Steps[0], [new TestFileText("tests/LimitTests.cs", longTest)], longOutput,
            "reason", []);

        Assert.DoesNotContain(new string('t', 12001), prompt);
        Assert.Contains(new string('t', 11000), prompt);
        Assert.DoesNotContain(new string('o', 4001), prompt);
    }

    [Fact]
    public void A_failing_test_the_step_added_is_not_a_frozen_test_when_its_name_only_contains_one()
    {
        TestFileText frozen = new("tests/LimitTests.cs", "public class LimitTests\n{\n    [Fact]\n    public void Rejects_zero() { }\n}\n");

        TestRulingDue due = Due(output: "Failed LimitTests.Rejects_zero_negative [5 ms]", frozen: [frozen]);

        Assert.False(due.Due);
        Assert.Equal("not-a-frozen-test", due.Reason);
    }

    [Theory]
    [InlineData("LimitTests.Rejects_zero_negative", "Rejects_zero", false)]
    [InlineData("Limits.rejects_zero(value: 0)", "rejects_zero", true)]
    [InlineData("Limits.rejects_zero [1 ms]", "rejects_zero", true)]
    [InlineData("suite > rejects zero", "rejects zero", true)]
    [InlineData("tests/test_limit.py::test_rejects_zero - AssertionError: 1 != 0", "test_rejects_zero", true)]
    [InlineData("Tests.Größe_prüfen [2 ms]", "Größe_prüfen", true)]
    [InlineData("Tests.Größe_prüfen_extra", "Größe_prüfen", false)]
    public void A_name_matches_only_as_the_last_part_of_an_item(string item, string name, bool expected)
    {
        Assert.Equal(expected, TestDispute.Matches(item, name));
    }

    [Fact]
    public void A_correction_may_not_drop_a_passing_frozen_test_only_because_a_longer_failing_name_contains_it()
    {
        TestFileText frozen = new("tests/LimitTests.cs",
            "public class LimitTests\n{\n    [Fact]\n    public void Accepts_at_limit() { Assert.True(false); }\n\n" +
            "    [Fact]\n    public void Rejects_over_limit() { }\n}\n");
        string corrected = "public class LimitTests\n{\n    [Fact]\n    public void Accepts_at_limit() { Assert.True(true); }\n}\n";

        string? reason = TestRuling.Guard([frozen], [new TestFileText("tests/LimitTests.cs", corrected)],
            ["Failed LimitTests.Accepts_at_limit [1 ms]", "Failed LimitTests.Rejects_over_limit_twice [1 ms]"]);

        Assert.NotNull(reason);
        Assert.Contains("Rejects_over_limit", reason);
    }

    [Fact]
    public void Two_file_blocks_for_the_same_path_are_no_verdict()
    {
        string reply = "VERDICT: TEST-WRONG\nFILE: tests/A.cs\n```\na\n```\nFILE: tests/A.cs\n```\nb\n```";

        Assert.Equal(TestRulingKind.NoVerdict, TestRuling.Parse(reply).Kind);
    }

    [Theory]
    [InlineData("`tests/LimitTests.cs`")]
    [InlineData("\"tests/LimitTests.cs\"")]
    [InlineData("'tests/LimitTests.cs'")]
    public void A_file_path_in_backticks_or_quotes_is_read_as_the_path(string pathText)
    {
        string reply = "VERDICT: TEST-WRONG\nFILE: " + pathText + "\n```csharp\npublic class LimitTests { }\n```";

        TestRulingVerdict verdict = TestRuling.Parse(reply);

        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal("tests/LimitTests.cs", Assert.Single(verdict.Files).Path);
    }

    [Fact]
    public void A_correction_identical_to_the_old_file_is_refused_as_changing_nothing()
    {
        string? reason = TestRuling.Guard(FrozenFiles, [Limit(LimitTestsText)], [RejectsFailure]);

        Assert.NotNull(reason);
        Assert.Contains("nothing", reason);
    }

    [Fact]
    public void A_test_file_with_a_line_of_three_backticks_keeps_its_fence_in_the_prompt()
    {
        PlanRecord plan = NewPlan(Step("limit"));
        const string content = "public class LimitTests\n{\n```\n    [Fact]\n    public void Rejects_over_limit() { }\n}\n";

        string prompt = TestRuling.BuildPrompt(plan, plan.Steps[0], [new TestFileText("tests/LimitTests.cs", content)], RejectsFailure, "reason", []);

        Assert.Contains("FILE: tests/LimitTests.cs" + Environment.NewLine + "````" + Environment.NewLine + content.TrimEnd() + Environment.NewLine + "````", prompt);
    }

    private static TestFileText BigFrozenFile()
    {
        var text = new StringBuilder("public class BigTests\n{\n");
        for (int index = 0; index < 40; index++)
        {
            text.Append($"    [Fact]\n    public void Frozen_case_{index:00}() {{ }}\n\n");
        }

        text.Append("}\n");
        return new TestFileText("tests/BigTests.cs", text.ToString());
    }

    // The shared signature keeps 40 names in sorted order, so the five names of the step's own work sort after the frozen ones
    // and would be cut off there; the dispute must still see them.
    [Fact]
    public void Forty_failing_frozen_tests_and_five_of_the_steps_own_are_not_ruled_on()
    {
        string output = string.Concat(Enumerable.Range(0, 40).Select(index => $"Failed BigTests.Frozen_case_{index:00} [1 ms]\n")) +
            string.Concat(Enumerable.Range(1, 5).Select(index => $"Failed OwnTests.Own_failure_{index} [1 ms]\n"));

        TestRulingDue due = Due(output: output, frozen: [BigFrozenFile()]);

        Assert.False(due.Due);
        Assert.Equal("not-a-frozen-test", due.Reason);
    }

    [Fact]
    public void Forty_failing_frozen_tests_alone_are_ruled_on()
    {
        string output = string.Concat(Enumerable.Range(0, 40).Select(index => $"Failed BigTests.Frozen_case_{index:00} [1 ms]\n"));

        Assert.True(Due(output: output, frozen: [BigFrozenFile()]).Due);
    }

    // The summary lines of the real pytest run, with the message after " - " that a name-at-the-end rule would miss.
    private static readonly TestFileText PytestFrozen = new("tests/test_fail.py",
        "def test_adds():\n    assert add(1, 2) == 3\n\ndef test_subtracts():\n    assert add(1, 2) == -1\n\n" +
        "@pytest.mark.parametrize(\"a,b,r\", [(1,1,2),(2,2,5)])\ndef test_table(a, b, r):\n    assert add(a, b) == r\n");

    [Fact]
    public void A_pytest_summary_line_with_its_message_still_names_the_frozen_test()
    {
        const string pytestOutput =
            "FAILED test_fail.py::test_subtracts - assert 3 == -1\n" +
            "FAILED test_fail.py::test_table[2-2-5] - assert 4 == 5\n" +
            "=================== 2 failed, 2 passed, 1 skipped in 0.08s ===================\n";

        Assert.Equal(2, TestDispute.FailingItems(pytestOutput).Count);
        Assert.True(Due(output: pytestOutput, frozen: [PytestFrozen]).Due);
    }

    // A jest failure is headed "● suite › test" in the default reporter, which the shared reading does not read.
    private static readonly TestFileText JestFrozen = new("tests/missing.test.js", "test('mul', () => { expect(multiply(2, 3)).toBe(6); });\n");

    [Fact]
    public void A_jest_failure_heading_names_the_frozen_test()
    {
        const string jestOutput =
            "FAIL ./missing.test.js\n" +
            "  ● mul\n\n" +
            "    TypeError: multiply is not a function\n\n" +
            "Test Suites: 1 failed, 1 total\n" +
            "Tests:       1 failed, 1 total\n";

        Assert.Equal(["mul"], TestDispute.FailingItems(jestOutput));
        Assert.True(Due(output: jestOutput, frozen: [JestFrozen]).Due);
    }

    // The real jest run also prints a test.each row as "table 2 2", while the frozen name is the template "table %i %i".
    // That row is not matched to its template, so no ruling is due: refusing is the safe side.
    [Fact]
    public void A_jest_test_each_row_is_not_matched_to_its_template_so_no_ruling_is_due()
    {
        const string jestOutput =
            "FAIL ./fail.test.js\n" +
            "  ● math › subtracts\n\n" +
            "  ● math › table 2 2\n\n" +
            "Tests:       2 failed, 1 skipped, 2 passed, 5 total\n";
        var frozen = new TestFileText("tests/fail.test.js",
            "describe('math', () => {\n  test('subtracts', () => { expect(add(1, 2)).toBe(-1); });\n" +
            "  test.each([[1,1,2],[2,2,5]])('table %i %i', (a, b, r) => { expect(add(a, b)).toBe(r); });\n});\n");

        Assert.Contains("math › subtracts", TestDispute.FailingItems(jestOutput));
        TestRulingDue due = Due(output: jestOutput, frozen: [frozen]);

        Assert.False(due.Due);
        Assert.Equal("not-a-frozen-test", due.Reason);
    }

    // The real vitest run: the FAIL line names the test, and the tree line "× subtracts 6ms" reads as "subtracts <time>".
    private static readonly TestFileText VitestFrozen = new("tests/vfail.test.mjs",
        "describe('math', () => {\n  test('adds', () => { expect(add(1, 2)).toBe(3); });\n" +
        "  test('subtracts', () => { expect(add(1, 2)).toBe(-1); });\n  test.skip('later', () => { expect(1).toBe(2); });\n});\n");

    [Fact]
    public void A_vitest_failure_names_the_frozen_test_on_its_fail_line_and_its_tree_line()
    {
        const string vitestOutput =
            " ❯ vfail.test.mjs (3 tests | 1 failed | 1 skipped) 9ms\n" +
            "   ❯ math (3)\n" +
            "     × subtracts 6ms\n\n" +
            " FAIL  vfail.test.mjs > math > subtracts\n" +
            "AssertionError: expected 3 to be -1 // Object.is equality\n\n" +
            " Test Files  1 failed (1)\n" +
            "      Tests  1 failed | 1 passed | 1 skipped (3)\n";

        Assert.Contains("subtracts <time>", TestDispute.FailingItems(vitestOutput));
        Assert.True(Due(output: vitestOutput, frozen: [VitestFrozen]).Due);
    }

    [Fact]
    public void A_vitest_file_that_fails_to_load_names_no_frozen_test()
    {
        const string vitestOutput =
            " FAIL  vmissing.test.mjs [ vmissing.test.mjs ]\n" +
            "Error: Cannot find module './nothing.js' imported from /work/project/vmissing.test.mjs\n";

        TestRulingDue due = Due(output: vitestOutput, frozen: [VitestFrozen]);

        Assert.False(due.Due);
        Assert.Equal("not-a-frozen-test", due.Reason);
    }

    [Theory]
    [InlineData("test_fail.py::test_subtracts - assert 3 == -1", "test_subtracts", true)]
    [InlineData("test_fail.py::test_table[2-2-5] - assert 4 == 5", "test_table", true)]
    [InlineData("subtracts <time>", "subtracts", true)]
    [InlineData("vfail.test.mjs > math > subtracts", "subtracts", true)]
    [InlineData("math › subtracts", "subtracts", true)]
    [InlineData("test_fail.py::test_subtracts - assert 3 == -1", "test_adds", false)]
    public void The_failing_line_shapes_of_each_runner_match_their_test_name(string item, string name, bool expected)
    {
        Assert.Equal(expected, TestDispute.Matches(item, name));
    }

    // The spec reporter's output for a suite with a passing, a failing and a skipped test, copied from a real run of node's test
    // runner. The failing-tests header and the suite's own failure line name no test, so only the failing test is an item.
    private const string NodeSpecOutput =
            "▶ math\n" +
            "  ✔ adds (0.7338ms)\n" +
            "  ✖ subtracts (0.843ms)\n" +
            "  ﹣ later (0.1145ms) # SKIP\n" +
            "✖ math (3.854ms)\n" +
            "ℹ tests 3\n" +
            "ℹ suites 1\n" +
            "ℹ pass 1\n" +
            "ℹ fail 1\n" +
            "ℹ cancelled 0\n" +
            "ℹ skipped 1\n" +
            "ℹ todo 0\n" +
            "ℹ duration_ms 121.3053\n" +
            "\n" +
            "✖ failing tests:\n" +
            "\n" +
            "test at fail.test.mjs:6:3\n" +
            "✖ subtracts (0.843ms)\n" +
            "  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:\n" +
            "  \n" +
            "  3 !== -1\n" +
            "  \n" +
            "      at TestContext.<anonymous> (file:///work/project/fail.test.mjs:6:36)\n" +
            "      at Test.runInAsyncScope (node:async_hooks:227:14)\n" +
            "      at Test.run (node:internal/test_runner/test:1382:25)\n" +
            "      at Suite.processPendingSubtests (node:internal/test_runner/test:960:18)\n" +
            "      at Test.postRun (node:internal/test_runner/test:1522:19)\n" +
            "      at Test.run (node:internal/test_runner/test:1447:12)\n" +
            "      at async Promise.all (index 0)\n" +
            "      at async Suite.run (node:internal/test_runner/test:1869:7)\n" +
            "      at async startSubtestAfterBootstrap (node:internal/test_runner/harness:387:3) {\n" +
            "    generatedMessage: true,\n" +
            "    code: 'ERR_ASSERTION',\n" +
            "    actual: 3,\n" +
            "    expected: -1,\n" +
            "    operator: 'strictEqual',\n" +
            "    diff: 'simple'\n" +
            "  }\n" +
            "";

    // The same run with the TAP reporter: the failing test is an indented "not ok" line, and the suite's own "not ok" line
    // (type suite in its diagnostic block) names no test.
    private const string NodeTapOutput =
            "TAP version 13\n" +
            "# Subtest: math\n" +
            "    # Subtest: adds\n" +
            "    ok 1 - adds\n" +
            "      ---\n" +
            "      duration_ms: 0.6586\n" +
            "      type: 'test'\n" +
            "      ...\n" +
            "    # Subtest: subtracts\n" +
            "    not ok 2 - subtracts\n" +
            "      ---\n" +
            "      duration_ms: 0.8398\n" +
            "      type: 'test'\n" +
            "      location: '/work/project/fail.test.mjs:6:3'\n" +
            "      failureType: 'testCodeFailure'\n" +
            "      error: |-\n" +
            "        Expected values to be strictly equal:\n" +
            "        \n" +
            "        3 !== -1\n" +
            "        \n" +
            "      code: 'ERR_ASSERTION'\n" +
            "      name: 'AssertionError'\n" +
            "      expected: -1\n" +
            "      actual: 3\n" +
            "      operator: 'strictEqual'\n" +
            "      stack: |-\n" +
            "        TestContext.<anonymous> (file:///work/project/fail.test.mjs:6:36)\n" +
            "        Test.runInAsyncScope (node:async_hooks:227:14)\n" +
            "        Test.run (node:internal/test_runner/test:1382:25)\n" +
            "        Suite.processPendingSubtests (node:internal/test_runner/test:960:18)\n" +
            "        Test.postRun (node:internal/test_runner/test:1522:19)\n" +
            "        Test.run (node:internal/test_runner/test:1447:12)\n" +
            "        async Promise.all (index 0)\n" +
            "        async Suite.run (node:internal/test_runner/test:1869:7)\n" +
            "        async startSubtestAfterBootstrap (node:internal/test_runner/harness:387:3)\n" +
            "      ...\n" +
            "    # Subtest: later\n" +
            "    ok 3 - later # SKIP\n" +
            "      ---\n" +
            "      duration_ms: 0.1121\n" +
            "      type: 'test'\n" +
            "      ...\n" +
            "    1..3\n" +
            "not ok 1 - math\n" +
            "  ---\n" +
            "  duration_ms: 3.323\n" +
            "  type: 'suite'\n" +
            "  location: '/work/project/fail.test.mjs:4:1'\n" +
            "  failureType: 'subtestsFailed'\n" +
            "  error: '1 subtest failed'\n" +
            "  code: 'ERR_TEST_FAILURE'\n" +
            "  ...\n" +
            "1..1\n" +
            "# tests 3\n" +
            "# suites 1\n" +
            "# pass 1\n" +
            "# fail 1\n" +
            "# cancelled 0\n" +
            "# skipped 1\n" +
            "# todo 0\n" +
            "# duration_ms 234.8584\n" +
            "";

    private static readonly TestFileText NodeFrozen = new("tests/fail.test.mjs",
        "describe('math', () => {\n  test('adds', () => { expect(add(1, 2)).toBe(3); });\n" +
        "  test('subtracts', () => { expect(add(1, 2)).toBe(-1); });\n  test.skip('later', () => { expect(1).toBe(2); });\n});\n");

    [Fact]
    public void The_spec_reporter_failing_test_is_the_only_item_and_its_summary_lines_do_not_block_a_ruling()
    {
        Assert.Equal(["subtracts"], TestDispute.FailingItems(NodeSpecOutput));
        // The shared reading still takes the header as an item; the dispute path leaves it out.
        Assert.Contains("failing tests:", PlanFailure.FailingItems(NodeSpecOutput));
        Assert.True(Due(output: NodeSpecOutput, frozen: [NodeFrozen]).Due);
    }

    [Fact]
    public void The_tap_reporter_nested_failing_test_is_matched_and_its_suite_line_is_not_an_item()
    {
        Assert.Equal(["subtracts"], TestDispute.FailingItems(NodeTapOutput));
        Assert.True(Due(output: NodeTapOutput, frozen: [NodeFrozen]).Due);
    }

    [Fact]
    public void A_tap_line_that_names_a_test_file_is_not_an_item()
    {
        const string tapOutput =
            "TAP version 13\n" +
            "not ok 1 - tests/a.test.mjs\n" +
            "  ---\n" +
            "  type: 'test'\n" +
            "  ...\n" +
            "1..1\n";

        Assert.Empty(TestDispute.FailingItems(tapOutput));
        Assert.Equal("no-failing-items", Due(output: tapOutput, frozen: [NodeFrozen]).Reason);
    }

    // The jest heading of a suite that fails to run is taken from the real jest sample. The "● Console" heading is not from a
    // real run here: it is pinned as the reading the code applies to that heading. Either heading names no frozen test, so
    // it blocks a ruling, which is the safe side.
    [Fact]
    public void Jest_headings_of_a_suite_that_fails_to_run_or_a_console_block_block_a_ruling()
    {
        const string jestOutput =
            "FAIL ./syntax.test.js\n" +
            "  ● Test suite failed to run\n\n" +
            "    Jest encountered an unexpected token\n\n" +
            "  ● Console\n\n" +
            "    console.log\n" +
            "      hello\n\n" +
            "Test Suites: 1 failed, 1 total\n";

        Assert.Equal(["Console", "Test suite failed to run"], TestDispute.FailingItems(jestOutput));
        TestRulingDue due = Due(output: jestOutput, frozen: [NodeFrozen]);

        Assert.False(due.Due);
        Assert.Equal("not-a-frozen-test", due.Reason);
    }
}
