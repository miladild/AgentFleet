using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// When a step names files to change and a round passes but changed nothing, the pass is not accepted
/// and the repair ladder gives the step more rounds. The rule has exemptions: steps that name no files,
/// the last step of a longer plan, and steps that had an earlier round with an edit tool call.
/// </summary>
public sealed class PlanNoChangeTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private string ProjectDirectory
    {
        get
        {
            string directory = Path.Combine(PlansDirectory, "project");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private PlanTools RepairTools(Func<string, string> verify)
    {
        string project = ProjectDirectory;
        return new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(command switch
        {
            "git rev-parse --show-toplevel" => $"Exit code: 0\n--- stdout ---\n{project}",
            "git rev-parse HEAD" => "Exit code: 0\n--- stdout ---\nabc123",
            _ when command.Contains("--untracked-files=all", StringComparison.Ordinal) =>
                "Exit code: 0\n--- stdout ---\n" + string.Join("\n",
                    Directory.GetFiles(project, "*", SearchOption.AllDirectories)
                        .Select(file => "?? " + Path.GetRelativePath(project, file).Replace('\\', '/'))),
            _ when command.StartsWith("git ", StringComparison.Ordinal) => "Exit code: 0\n--- stdout ---\n",
            _ => verify(command)
        }));
    }

    private PlanRunner RepairRunner(FakeStepAgent agent, Func<string, string> verify,
        int roundsPerRung = RepairLadder.DefaultRoundsPerRung) =>
        new(Store, RepairTools(verify), agent, NullLogger.Instance, roundsPerRung: roundsPerRung,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask);

    // A project git cannot read: every git command fails, so the runner cannot tell which files a round changed.
    private PlanRunner NoGitRunner(FakeStepAgent agent, Func<string, string> verify) =>
        new(Store,
            new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(
                command.StartsWith("git ", StringComparison.Ordinal) ? "Exit code: 128\n--- stdout ---\n" : verify(command))),
            agent, NullLogger.Instance, transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask);

    private PlanRecord OneStepPlan(params PlanStepInput[] steps) =>
        Store.Approve(Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null, steps).Id)!;

    private static PlanStepInput StepNamed(string title, string file, string? verify = "check") =>
        new(title, "detail", [file], verify, "standard");

    private static PlanStepInput StepNoFiles(string title, string? verify = "check") =>
        new(title, "detail", [], verify, "standard");

    [Fact]
    public async Task A_step_that_names_files_but_changes_nothing_is_not_accepted_on_a_passing_check()
    {
        // Pre-create the file so git can initially see it
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.json"), "{}");

        PlanRecord plan = OneStepPlan(StepNamed("write config", "a.json"));
        var agent = new FakeStepAgent((_, _, _) => Task.FromResult("Looked at it."), toolCalls: 0, editToolCalled: false);

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);

        // The step is parked because it named files but changed none
        PlanRunEvent? parked = after.Events?.FirstOrDefault(e => e.Kind == RunEventKind.StepParked);
        Assert.NotNull(parked);
        Assert.Equal("no-change", parked.FailureSignature);

        // The agent was called more than once because the ladder gave it more rounds
        Assert.True(agent.Calls.Count > 1, "Agent should have been called multiple times by the repair ladder");

        // The model is told why the passing check was not accepted (a one-step plan's step is also its final validation).
        Assert.Contains(after.Events!, e => e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed &&
            e.Detail is not null && e.Detail.Contains("Not accepted:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Where_git_cannot_tell_a_round_that_ran_tools_is_given_the_benefit_of_the_doubt()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.cs"), "// existing");
        PlanRecord plan = OneStepPlan(StepNamed("edit through a shell", "a.cs"));
        // Tools ran, no file tool was used (the model may have edited through a shell command), and git cannot say what changed.
        var agent = new FakeStepAgent((_, _, _) => Task.FromResult("Done."), toolCalls: 3, editToolCalled: false);

        await NoGitRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task Where_git_cannot_tell_a_round_with_no_tool_call_at_all_is_still_not_accepted()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.cs"), "// existing");
        PlanRecord plan = OneStepPlan(StepNamed("do nothing", "a.cs"));
        var agent = new FakeStepAgent((_, _, _) => Task.FromResult(string.Empty), toolCalls: 0, editToolCalled: false);

        await NoGitRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
        Assert.Equal("no-change", after.Events!.First(e => e.Kind == RunEventKind.StepParked).FailureSignature);
    }

    [Fact]
    public async Task A_step_that_names_files_and_edits_one_passes_immediately()
    {
        PlanRecord plan = OneStepPlan(StepNamed("create file", "new-file.cs"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "new-file.cs"), "// new file");
            return Task.FromResult("Created it.");
        }, toolCalls: 1, editToolCalled: true);

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);
        Assert.Single(agent.Calls);
    }

    [Fact]
    public async Task A_step_that_names_no_files_passes_even_if_nothing_changed()
    {
        PlanRecord plan = OneStepPlan(StepNoFiles("validate", "check-syntax"));
        var agent = new FakeStepAgent((_, _, _) => Task.FromResult("All looks good."), toolCalls: 0, editToolCalled: false);

        await RepairRunner(agent, _ => Pass("check-syntax")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);
    }

    [Fact]
    public async Task The_last_step_of_a_longer_plan_is_exempt_from_the_no_change_rule()
    {
        // Pre-create files for both steps
        File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "");
        File.WriteAllText(Path.Combine(ProjectDirectory, "report.txt"), "");

        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                StepNamed("setup", "setup.json"),
                StepNamed("validate", "report.txt")
            ]).Id)!;

        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            // First step makes a change
            if (prompt.Contains("(1 of 2)"))
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "{}");
                return Task.FromResult("Set it up.");
            }
            // Second step (the last) changes nothing, but last step is exempt
            return Task.FromResult("Validated everything.");
        }, toolCalls: 1, editToolCalled: false)
        {
            // First step makes an edit, second step doesn't
            ToolsOf = call =>
            {
                if (call.Text.Contains("(1 of 2)"))
                    return (1, true);  // First step: made an edit
                return (0, false);     // Later steps: no edit
            }
        };

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal([StepStatus.Done, StepStatus.Done], after.Steps.Select(s => s.Status).ToArray());
    }

    [Fact]
    public async Task A_first_step_that_changes_nothing_parks_and_later_steps_never_run()
    {
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                StepNamed("write-x", "x.txt"),
                StepNamed("write-y", "y.txt")
            ]).Id)!;

        var agent = new FakeStepAgent((prompt, _, _) => Task.FromResult("Done."), toolCalls: 0, editToolCalled: false);

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
        Assert.Equal(StepStatus.Pending, after.Steps[1].Status);

        // Step 2 was never called (the prompt contains "(2 of 2)")
        Assert.DoesNotContain(agent.Calls, call => call.Prompt.Contains("(2 of 2)"));
    }

    [Fact]
    public async Task An_earlier_round_with_an_edit_exempts_the_step_from_the_rule()
    {
        PlanRecord plan = OneStepPlan(StepNamed("implement-feature", "feature.cs"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            if (round == 1)
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "feature.cs"), "// feature v1");
                return Task.FromResult("Added the feature.");
            }
            // Round 2: no edit, but previous round had one
            return Task.FromResult("Refined it.");
        })
        {
            // The first message edits; the repair message of round 2 reports no tool use at all, so this round passes only
            // because an earlier round of the step changed the file.
            ToolsOf = call => call.Text.Contains("Round 2") ? (0, false) : (1, true)
        };

        // Check fails first time, passes second time
        int checks = 0;
        Func<string, string> verify = command =>
        {
            if (command == "check")
            {
                checks++;
                return checks == 1 ? "Exit code: 1\n--- stdout ---\nFAILED test" : Pass("check");
            }
            return Pass(command);
        };

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        // Because round 1 had an edit, round 2's no-change is acceptable
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);
    }
}
