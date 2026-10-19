namespace AgentFleet.Tests;

public sealed class PlanStepGuidanceTests : PlanTestBase
{
    private const string ExpectedGuidance = "Writing the tests for this step: write at least one test for every rule this step states, and name each test after the rule it checks. Before you stop, check your tests against this list wherever the step's rules make it meaningful: empty input; a single element, also when that single element is the one that must be rejected; the first, the last and the only element being invalid; both sides of every boundary (zero, one, the maximum); input that must not be changed; a result that must be a new object; the error type and message for each kind of invalid input, through every entry point (a function that validates must validate even when nothing would be compared). Assert only what the step states. When a test of yours fails, read the step text again before you change the code: either the code or the test may be wrong, and the step text decides.";

    [Fact]
    public void Enabled_guidance_is_inserted_once_after_files_and_before_verification()
    {
        PlanRecord plan = Store.Approve(NewPlan(new PlanStepInput(
            "write parser tests", "Cover the parser rules.", ["src/parser.ts", "tests/parser.test.ts"], "npm test")).Id)!;

        string prompt = PlanPromptComposition.BuildPrompt(plan, plan.Steps[0], 1, null, stepGuidance: true);
        string repeated = PlanPromptComposition.BuildPrompt(plan, plan.Steps[0], 1, null, stepGuidance: true);
        int files = prompt.IndexOf("Files: src/parser.ts, tests/parser.test.ts", StringComparison.Ordinal);
        int guidance = prompt.IndexOf(ExpectedGuidance, StringComparison.Ordinal);
        int verification = prompt.IndexOf("When you stop, your work is checked automatically", StringComparison.Ordinal);

        Assert.Equal(prompt, repeated);
        Assert.Equal(1, prompt.Split(ExpectedGuidance, StringSplitOptions.None).Length - 1);
        Assert.True(files >= 0 && files < guidance && guidance < verification);
    }

    [Fact]
    public void Disabled_default_is_byte_identical_to_an_explicitly_disabled_prompt()
    {
        PlanRecord plan = Store.Approve(NewPlan(new PlanStepInput(
            "write tests", "Test the step.", ["tests/parser.test.ts"], "npm test")).Id)!;

        string defaultPrompt = PlanPromptComposition.BuildPrompt(plan, plan.Steps[0], 1, null);
        string disabledPrompt = PlanPromptComposition.BuildPrompt(plan, plan.Steps[0], 1, null, stepGuidance: false);

        Assert.Equal(disabledPrompt, defaultPrompt);
        Assert.DoesNotContain(ExpectedGuidance, defaultPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Guidance_uses_file_names_not_test_words_in_step_detail_or_latest_js()
    {
        PlanRecord plan = Store.Approve(NewPlan(new PlanStepInput(
            "update latest", "The tests should be considered during this change.", ["src/latest.js"], "npm test")).Id)!;

        string prompt = PlanPromptComposition.BuildPrompt(plan, plan.Steps[0], 1, null, stepGuidance: true);

        Assert.DoesNotContain(ExpectedGuidance, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/foo.spec.ts")]
    [InlineData("tests/a.py")]
    [InlineData("test/a.py")]
    [InlineData("__tests__/a.js")]
    [InlineData("spec/a.js")]
    public void Guidance_applies_to_supported_test_file_names(string file)
    {
        PlanRecord plan = Store.Approve(NewPlan(new PlanStepInput("test", "detail", [file], "check")).Id)!;

        Assert.Contains(ExpectedGuidance,
            PlanPromptComposition.BuildPrompt(plan, plan.Steps[0], 1, null, stepGuidance: true), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("off")]
    [InlineData("true")]
    [InlineData("other")]
    public void Any_setting_other_than_tests_leaves_guidance_disabled(string? setting) =>
        Assert.False(PlanStepGuidance.IsEnabled(setting));

    [Fact]
    public void Tests_setting_enables_guidance()
    {
        Assert.True(PlanStepGuidance.IsEnabled("tests"));
    }
}
