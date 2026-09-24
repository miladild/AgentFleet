using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class PlanPromptTests : PlanTestBase
{
    private PlanRecord NewApproved(string? verify = "node --test") =>
        Store.Approve(NewPlan(Step("build it", verify: verify)).Id)!;

    [Fact]
    public void A_step_with_a_check_tells_the_model_to_run_that_check_itself_first()
    {
        PlanRecord plan = NewApproved();

        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 1, null);

        Assert.Contains("Run the check yourself", prompt);
    }

    [Fact]
    public void A_step_without_a_check_does_not_ask_for_one()
    {
        PlanRecord plan = NewApproved(verify: null);

        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 1, null);

        Assert.DoesNotContain("Run the check yourself", prompt);
        Assert.Contains("no automatic check", prompt);
    }

    [Fact]
    public void A_retry_says_the_cause_may_lie_in_an_earlier_steps_file()
    {
        PlanRecord plan = NewApproved();

        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 2, "Exit code: 1\nexpected 3 got 2");

        Assert.Contains("expected 3 got 2", prompt);
        Assert.Contains("an earlier step wrote", prompt);
        Assert.DoesNotContain("keeps the process alive", prompt);
    }

    [Theory]
    [InlineData("Error: command exceeded the 120s timeout and was killed.")]
    [InlineData("The attempt ran out of time after 20 minutes.")]
    public void A_retry_after_a_hang_points_at_what_keeps_a_process_alive(string failure)
    {
        PlanRecord plan = NewApproved();

        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 2, failure);

        Assert.Contains("keeps the process alive", prompt);
    }

    [Fact]
    public void The_planner_is_told_a_check_must_exercise_behavior_and_finish_on_its_own()
    {
        string directive = PlanGate.PlanningDirective(null);

        Assert.Contains("exercises the step's", directive);
        Assert.Contains("must finish on its own", directive);
    }

    [Fact]
    public void Prompts_name_the_machines_own_platform_not_a_fixed_one()
    {
        PlanRecord plan = NewApproved();

        string runnerPrompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 1, null);
        string planning = PlanGate.PlanningInstructions(null);

        Assert.Contains(HubPlatform.Name, runnerPrompt);
        Assert.Contains(HubPlatform.Name, planning);
    }

    [Fact]
    public async Task The_shell_tool_runs_a_command_with_a_pipe_on_this_platform()
    {
        var options = HubShellOptions.Load(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        string result = await HubShellTools.RunCommandAsync(
            "echo one && echo two", null, options, NullLogger.Instance, default);

        Assert.Contains("Exit code: 0", result);
        Assert.Contains("one", result);
        Assert.Contains("two", result);
    }
}
