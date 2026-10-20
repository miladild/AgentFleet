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
    [InlineData("Rejects_over_limit_twice", "Rejects_over_limit", true)]
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
}
