namespace AgentFleet.Tests;

// What a check's output says about the code: whether the code loads at all, how a round is scored because of it, and whether a
// syntax error comes from the project's own source file or from the check. The outputs are the real ones in
// PlanCheckOutputFixtures, and synthetic ones for the shapes those do not have.
public sealed class PlanBuildBrokenTests
{
    public static TheoryData<string> OutputsThatDoNotLoadTheCode => new()
    {
        PlanCheckOutputFixtures.Round01,
        PlanCheckOutputFixtures.Round03,
        PlanCheckOutputFixtures.Round06,
        "Exit code: 1\nTransform failed with 1 error:\nsrc/app.ts:3:1: ERROR: Unexpected \"}\"\n",
        "Exit code: 1\nFailed to parse source for import analysis because the content contains invalid syntax.\n",
        "Exit code: 1\nsrc/app.ts:3:1: Unexpected token `}`\n",
        "Exit code: 1\n✖ tests/limits.test.js (12ms)\nℹ tests 1\nℹ fail 1\n",
        "Exit code: 1\n✖ failing tests:\n\ntest at tests/limits.test.ts:1:1\n✖ tests/limits.test.ts (12ms)\nℹ tests 1\nℹ fail 1\n"
    };

    public static TheoryData<string> OutputsThatRunTheTests => new()
    {
        PlanCheckOutputFixtures.Round02,
        PlanCheckOutputFixtures.Round04,
        PlanCheckOutputFixtures.Round05,
        PlanCheckOutputFixtures.Round07,
        "Exit code: 1\n✖ rejects SyntaxError input (2ms)\n✖ parses Transform failed with 1 error: text (1ms)\nℹ tests 2\nℹ fail 2\n",
        "Exit code: 1\n✖ accepts an Unexpected token in a name (3ms)\nℹ tests 1\nℹ fail 1\n",
        "Exit code: 1\n✖ reads a file named limits.test.js (3ms)\nℹ tests 2\nℹ fail 1\nℹ pass 1\n",
        "Exit code: 1\nFailed Limits.rejects_zero [1 ms]\n  Expected: No tests found\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1\n"
    };

    [Theory]
    [MemberData(nameof(OutputsThatDoNotLoadTheCode))]
    public void A_check_that_cannot_load_the_code_says_it_does_not_build(string output) =>
        Assert.True(PlanFailure.BuildBroken(output));

    [Theory]
    [MemberData(nameof(OutputsThatRunTheTests))]
    public void A_check_that_ran_the_tests_says_the_code_builds_even_when_a_name_uses_the_words(string output) =>
        Assert.False(PlanFailure.BuildBroken(output));

    [Fact]
    public void A_parser_message_indented_under_a_test_is_an_assertion_and_not_a_parse_error()
    {
        const string output = "Exit code: 1\n✖ reads the file (4ms)\n  AssertionError: expected the file at src/a.ts:3 to read as text, got Unexpected token\nℹ tests 1\nℹ fail 1\n";

        Assert.False(PlanFailure.BuildBroken(output));
    }

    [Fact]
    public void A_test_file_that_loaded_but_had_no_name_on_a_failing_line_is_not_a_file_that_did_not_load()
    {
        // Two tests ran and one of them failed: the file line is not the only failure, so the file did load.
        const string output = "Exit code: 1\n✖ tests/limits.test.js (12ms)\nℹ tests 2\nℹ fail 1\n";

        Assert.False(PlanFailure.BuildBroken(output));
    }

    [Fact]
    public void A_failing_check_that_prints_a_compiler_error_on_a_failing_line_still_does_not_build()
    {
        const string output = "Exit code: 1\n✖ src/a.ts(3,5): error TS2322: Type 'string' is not assignable to type 'number'.\n";

        Assert.True(PlanFailure.BuildBroken(output));
    }

    [Fact]
    public void A_real_failing_run_signs_its_failing_tests_and_a_file_that_did_not_load_signs_none()
    {
        // The header and the file-level line of a run name no test; the nine failing tests of round 2 are the signature.
        FailureFingerprint ran = PlanFailure.FailureSignature(PlanCheckOutputFixtures.Round02);
        Assert.Equal(9, ran.Size);
        Assert.DoesNotContain("failing tests:", ran.Items);
        Assert.All(ran.Items, item => Assert.DoesNotContain(".test.", item));
        Assert.Contains("shop session shop open", ran.Items);
        Assert.Contains("isShopHoliday", ran.Items);

        FailureFingerprint notLoaded = PlanFailure.FailureSignature(PlanCheckOutputFixtures.Round01);
        Assert.Empty(notLoaded.Items);
        Assert.StartsWith("sha256:", notLoaded.Value);
    }

    public static TheoryData<string, int> RunsThatFailSomeTests => new()
    {
        { PlanCheckOutputFixtures.Round02, 9 },
        { PlanCheckOutputFixtures.Round04, 7 },
        { PlanCheckOutputFixtures.Round05, 5 },
        { PlanCheckOutputFixtures.Round07, 5 }
    };

    [Theory]
    [MemberData(nameof(RunsThatFailSomeTests))]
    public void The_signature_of_a_real_failing_run_is_its_failing_names_only(string output, int names)
    {
        FailureFingerprint signature = PlanFailure.FailureSignature(output);

        Assert.Equal(names, signature.Size);
        Assert.Equal(names, signature.Items.Count);
        Assert.DoesNotContain("failing tests:", signature.Items);
        Assert.All(signature.Items, item => Assert.False(TestDispute.BareTestFile().IsMatch(item)));
    }

    [Theory]
    [MemberData(nameof(OutputsThatDoNotLoadTheCode))]
    public void A_round_whose_code_does_not_load_scores_as_one_defect_even_when_it_names_nothing(string output)
    {
        FailureFingerprint signature = PlanFailure.FailureSignature(output);

        Assert.Equal((RoundScore?)new RoundScore(false, 1), RoundScore.Of(signature, output));
    }

    [Fact]
    public void A_round_that_loads_and_fails_the_tests_scores_as_its_failing_names()
    {
        FailureFingerprint signature = PlanFailure.FailureSignature(PlanCheckOutputFixtures.Round02);

        Assert.Equal(new RoundScore(true, 9), RoundScore.Of(signature, PlanCheckOutputFixtures.Round02));
    }

    [Fact]
    public void A_round_with_no_failing_names_and_no_build_error_has_no_score()
    {
        const string unnamed = "Exit code: 1\nsomething went wrong in a way no name captures";
        Assert.Null(RoundScore.Of(PlanFailure.FailureSignature(unnamed), unnamed));
    }

    [Fact]
    public void A_round_that_does_not_load_is_worse_than_a_round_that_loads_and_fails_ten_tests()
    {
        var loads = new RoundScore(true, 10);
        var notLoaded = new RoundScore(false, 1);

        Assert.True(notLoaded.IsWorseThan(loads));
        Assert.False(loads.IsWorseThan(notLoaded));
    }

    [Theory]
    [InlineData("Exit code: 1\nsrc/app.ts:3:1: SyntaxError: Unexpected token '}'\n    at Module.compile (node:internal/modules/cjs/loader)\n")]
    [InlineData("Exit code: 1\nfile:///work/project/src/app.js:12\n    }\n    ^\n\nSyntaxError: Unexpected token '}'\n")]
    [InlineData("Exit code: 1\n  File \"/work/project/app.py\", line 3\n    def f(:\n          ^\nSyntaxError: invalid syntax\n")]
    public void A_syntax_error_in_a_project_source_file_is_the_code_and_not_a_defect_of_the_check(string output)
    {
        Assert.NotEqual(FailureClass.CheckDefect, PlanFailure.Classify(null, output, []));
        Assert.Equal(FailureClass.CodeProgress, PlanFailure.Classify(null, output, [], filesChanged: 1, editToolCalled: true));
    }

    [Theory]
    [InlineData("At verify.ps1:3 char:5\n+ npm run build && npm test\n    + CategoryInfo : ParserError: (:) [], ParentContainsErrorRecordException")]
    [InlineData("bash: -c: line 1: syntax error near unexpected token `&&'")]
    [InlineData("./verify.sh: line 3: syntax error near unexpected token `('")]
    [InlineData("ParserError: Unexpected token '&&' in expression or statement.")]
    public void A_syntax_error_of_the_verify_command_itself_is_a_check_defect(string output) =>
        Assert.Equal(FailureClass.CheckDefect, PlanFailure.Classify(null, output, []));
}
