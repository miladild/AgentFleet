using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A step's own check runs the tests of the files it touched, so a step that breaks an older test can pass. When a step's check
/// passes, the plan's whole-project check (the last step's Verify) runs too, before the step's changes are synced back. If it fails,
/// the worker gets one more round in the same conversation, once per attempt. A failure that names a file a later step lists is
/// expected until that step is written, so it gives no round.
/// </summary>
public sealed partial class PlanNoChangeTests
{
    private const string FinalCheckKind = "final-check-round";
    private const string FinalCheckCommand = "final";
    private const string BrokenOldTest = "Exit code: 1\n--- stdout ---\nFAIL test/old.test.ts\nFAILED rejects zero (3 ms)\nTests: 1 failed";

    private int FinalCheckRounds(string planId) => Store.Get(planId)?.Events?.Count(runEvent => runEvent.Kind == FinalCheckKind) ?? 0;

    private static PlanStepInput Listed(string title, string? verify, params string[] files) => new(title, "detail", files, verify, "standard");

    private void SeedFiles(params string[] files)
    {
        foreach (string file in files)
        {
            WriteProjectFile(file, "old");
        }
    }

    // Step 1 edits a.txt; step 2 edits b.txt, which may break the whole project; the last step edits c.txt and b.txt.
    private PlanRecord ThreeStepPlan(string[] lastFiles) =>
        Store.Approve(Store.Create("Three-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                Listed("setup", "check1", "a.txt"),
                Listed("break", "check2", "b.txt"),
                Listed("last", FinalCheckCommand, lastFiles)
            ]).Id)!;

    private FakeStepAgent ThreeStepAgent(string breakText, string repairText, bool lastStepEditsSource = false) => new((prompt, _, _) =>
    {
        if (prompt.Contains("project's final check", StringComparison.Ordinal))
        {
            WriteProjectFile("b.txt", repairText);
        }
        else if (prompt.Contains("This step (1 of 3)", StringComparison.Ordinal))
        {
            WriteProjectFile("a.txt", "new a");
        }
        else if (prompt.Contains("This step (2 of 3)", StringComparison.Ordinal))
        {
            WriteProjectFile("b.txt", breakText);
        }
        else
        {
            WriteProjectFile("c.txt", "new c");
            WriteProjectFile("b.txt", "fixed");
            if (lastStepEditsSource)
            {
                WriteProjectFile("src/limits.ts", "code");
            }
        }

        return Task.FromResult("Done.");
    });

    // The whole-project command fails while b.txt says "broken"; every other command passes.
    private Func<string, string> ThreeStepVerify(List<string> commands, string failure) => command =>
    {
        commands.Add(command);
        string path = Path.Combine(ProjectDirectory, "b.txt");
        if (command == FinalCheckCommand && File.Exists(path) && File.ReadAllText(path).Contains("broken", StringComparison.Ordinal))
        {
            return failure;
        }

        return Pass(command);
    };

    [Fact]
    public async Task A_passing_step_that_breaks_an_old_test_gets_one_more_round_when_the_whole_project_check_fails()
    {
        SeedFiles("a.txt", "b.txt", "c.txt");
        PlanRecord plan = ThreeStepPlan(["c.txt"]);
        var commands = new List<string>();
        FakeStepAgent agent = ThreeStepAgent(breakText: "broken", repairText: "fixed");

        await RepairRunner(agent, ThreeStepVerify(commands, BrokenOldTest)).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(4, agent.Calls.Count);
        Assert.Equal(1, FinalCheckRounds(plan.Id));
        string extra = agent.Sessions[1].Messages[1].Text;
        Assert.Contains("the project's final check (`final`) fails now", extra, StringComparison.Ordinal);
        Assert.Contains("FAIL test/old.test.ts", extra, StringComparison.Ordinal);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == FinalCheckKind && runEvent.StepId == 2 &&
            runEvent.Detail.Contains("`final`", StringComparison.Ordinal) && runEvent.Detail.Contains("rejects zero", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_the_extra_round_does_not_fix_is_still_done_and_is_given_no_second_round()
    {
        SeedFiles("a.txt", "b.txt", "c.txt");
        PlanRecord plan = ThreeStepPlan(["c.txt"]);
        var commands = new List<string>();
        FakeStepAgent agent = ThreeStepAgent(breakText: "broken", repairText: "still broken");

        await RepairRunner(agent, ThreeStepVerify(commands, BrokenOldTest)).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(StepStatus.Done, after.Steps[1].Status);
        Assert.Equal(1, FinalCheckRounds(plan.Id));
        Assert.Equal(2, agent.Sessions[1].Messages.Count);
        Assert.Equal(PlanStatus.Done, after.Status);
    }

    [Fact]
    public async Task The_runners_failing_tests_header_is_not_named_as_a_failing_test()
    {
        SeedFiles("a.txt", "b.txt", "c.txt");
        PlanRecord plan = ThreeStepPlan(["c.txt"]);
        var commands = new List<string>();
        FakeStepAgent agent = ThreeStepAgent(breakText: "broken", repairText: "fixed");
        const string withHeader = "Exit code: 1\n--- stdout ---\n✖ failing tests:\n✖ rejects zero (3 ms)\nFAIL test/old.test.ts\nTests: 1 failed";

        await RepairRunner(agent, ThreeStepVerify(commands, withHeader)).RunPlanAsync(plan.Id, default);

        PlanRunEvent round = Assert.Single(Store.Get(plan.Id)!.Events!, runEvent => runEvent.Kind == FinalCheckKind);
        Assert.Contains("rejects zero", round.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("failing tests", round.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_extra_round_does_not_use_up_a_round_of_the_rung()
    {
        SeedFiles("a.txt", "b.txt", "c.txt");
        PlanRecord plan = ThreeStepPlan(["c.txt"]);
        var commands = new List<string>();
        FakeStepAgent agent = ThreeStepAgent(breakText: "broken", repairText: "fixed");

        // One round per rung: charged, the extra round would end the rung and the repair would continue in a new conversation.
        await RepairRunner(agent, ThreeStepVerify(commands, BrokenOldTest), roundsPerRung: 1).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(1, FinalCheckRounds(plan.Id));
        Assert.Equal(2, agent.Sessions[1].Messages.Count);
        Assert.Equal(1, after.Steps[1].Attempts);
    }

    [Fact]
    public async Task A_passing_whole_project_check_gives_no_round_and_the_last_step_runs_its_check_once()
    {
        SeedFiles("a.txt", "b.txt", "c.txt");
        PlanRecord plan = ThreeStepPlan(["c.txt"]);
        var commands = new List<string>();
        FakeStepAgent agent = ThreeStepAgent(breakText: "fine", repairText: "fine");

        await RepairRunner(agent, ThreeStepVerify(commands, BrokenOldTest)).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Equal(0, FinalCheckRounds(plan.Id));
        // Steps 1 and 2 each run the whole-project command once; the last step runs it only as its own check.
        Assert.Equal(3, commands.Count(command => command == FinalCheckCommand));
    }

    [Fact]
    public async Task A_failure_that_names_a_file_a_later_step_lists_gives_no_round()
    {
        SeedFiles("a.txt", "b.txt", "c.txt");
        PlanRecord plan = ThreeStepPlan(["c.txt", "src/limits.ts"]);
        var commands = new List<string>();
        FakeStepAgent agent = ThreeStepAgent(breakText: "broken", repairText: "fixed", lastStepEditsSource: true);
        const string namesLaterFile = "Exit code: 1\n--- stdout ---\nFAIL src/limits.ts\nFAILED rejects zero";

        await RepairRunner(agent, ThreeStepVerify(commands, namesLaterFile)).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(0, FinalCheckRounds(plan.Id));
        Assert.Single(agent.Sessions[1].Messages);
        Assert.Equal(3, commands.Count(command => command == FinalCheckCommand));
    }

    [Fact]
    public async Task A_one_step_plan_runs_its_check_once_and_no_whole_project_check()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(Listed("edit a", FinalCheckCommand, "a.txt"));
        var commands = new List<string>();
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, command => { commands.Add(command); return Pass(command); }).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(1, commands.Count(command => command == FinalCheckCommand));
        Assert.Equal(0, FinalCheckRounds(plan.Id));
    }

    [Fact]
    public async Task A_last_step_whose_check_is_the_same_as_this_steps_is_not_run_a_second_time()
    {
        SeedFiles("a.txt", "c.txt");
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                Listed("first", FinalCheckCommand, "a.txt"),
                Listed("second", FinalCheckCommand, "c.txt")
            ]).Id)!;
        var commands = new List<string>();
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            WriteProjectFile(prompt.Contains("This step (1 of 2)", StringComparison.Ordinal) ? "a.txt" : "c.txt", "new");
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, command => { commands.Add(command); return Pass(command); }).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        // One run for each step's own check; the whole-project check would repeat the first step's check.
        Assert.Equal(2, commands.Count(command => command == FinalCheckCommand));
    }

    [Fact]
    public async Task A_last_step_without_a_check_means_no_whole_project_command_is_run()
    {
        SeedFiles("a.txt");
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                Listed("first", "check1", "a.txt"),
                Listed("second", null, "c.txt")
            ]).Id)!;
        var commands = new List<string>();
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            WriteProjectFile(prompt.Contains("This step (1 of 2)", StringComparison.Ordinal) ? "a.txt" : "c.txt", "new");
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, command => { commands.Add(command); return Pass(command); }).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(["check1"], commands);
    }

    [Fact]
    public async Task A_last_step_check_the_command_policy_refuses_is_never_run_as_the_whole_project_check()
    {
        SeedFiles("a.txt");
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                Listed("first", "check1", "a.txt"),
                Listed("second", "git status", "c.txt")
            ]).Id)!;
        var commands = new List<string>();
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            WriteProjectFile(prompt.Contains("This step (1 of 2)", StringComparison.Ordinal) ? "a.txt" : "c.txt", "new");
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, command => { commands.Add(command); return Pass(command); }).RunPlanAsync(plan.Id, default);

        Assert.Equal(["check1"], commands.Where(command => command != "git status"));
        Assert.Equal(1, agent.Calls.Count(call => call.Prompt.Contains("This step (1 of 2)", StringComparison.Ordinal)));
        Assert.Equal(0, FinalCheckRounds(plan.Id));
    }

    [Fact]
    public async Task A_tests_step_that_comes_last_is_not_run_as_the_whole_project_check()
    {
        PlanRecord plan = Store.Approve(Store.Create("Test-first plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                Listed("Implement limits", "check1", "src/limits.cs"),
                new PlanStepInput("Write limits tests", "Reject zero", ["tests/LimitTests.cs"], "tests-cmd", "standard", Kind: "tests")
            ]).Id)!;
        var commands = new List<string>();
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            WriteProjectFile(prompt.Contains("This step (1 of 2)", StringComparison.Ordinal) ? "src/limits.cs" : "tests/LimitTests.cs", "content");
            return Task.FromResult("Done.");
        });
        var runner = new PlanRunner(Store, RepairTools(command =>
        {
            commands.Add(command);
            // Tests that fail are what a tests step requires, so the command fails every time it runs.
            return command == "tests-cmd" ? TestsFailing : Pass(command);
        }), new TestWriterAgent(agent), NullLogger.Instance,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, testFirst: TestFirstMode.On);

        await runner.RunPlanAsync(plan.Id, default);

        Assert.Equal(0, FinalCheckRounds(plan.Id));
        Assert.Equal(1, commands.Count(command => command == "tests-cmd"));
    }

    [Fact]
    public async Task The_tests_step_of_test_first_mode_does_not_run_the_whole_project_check()
    {
        PlanRecord plan = Store.Approve(Store.Create("Test-first plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                new PlanStepInput("Write limits tests", "Reject zero", ["tests/LimitTests.cs"], "tests-cmd", "standard", Kind: "tests"),
                Listed("Implement limits", FinalCheckCommand, "src/limits.cs")
            ]).Id)!;
        var commands = new List<string>();
        int testWrites = 0;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("This step (2 of 2)", StringComparison.Ordinal))
            {
                WriteProjectFile("src/limits.cs", "code");
            }
            else
            {
                WriteProjectFile("tests/LimitTests.cs", $"tests {++testWrites}");
            }

            return Task.FromResult("Done.");
        });
        var runner = new PlanRunner(Store, RepairTools(command =>
        {
            commands.Add(command);
            // The first run of the tests passes, which test-first mode refuses (the step is sent back); the second run fails as the
            // step requires; after the code is written the frozen tests pass.
            return command == "tests-cmd" && commands.Count(candidate => candidate == "tests-cmd") == 2 ? TestsFailing : Pass(command);
        }), new TestWriterAgent(agent), NullLogger.Instance,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, testFirst: TestFirstMode.On);

        await runner.RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(1, commands.Count(command => command == FinalCheckCommand));
        Assert.Equal(0, FinalCheckRounds(plan.Id));
    }

    [Fact]
    public async Task A_whole_project_check_that_cannot_run_neither_fails_the_step_nor_gives_a_round()
    {
        SeedFiles("a.txt");
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                Listed("first", "check1", "a.txt"),
                Listed("second", FinalCheckCommand, "c.txt")
            ]).Id)!;
        // The first run of the whole-project command (after the first step) cannot reach the worker; the second step's check is answered.
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            WriteProjectFile(prompt.Contains("This step (1 of 2)", StringComparison.Ordinal) ? "a.txt" : "c.txt", "new");
            return Task.FromResult("Done.");
        });
        Func<string, string> verify = command =>
        {
            if (command == FinalCheckCommand && !File.Exists(Path.Combine(ProjectDirectory, "c.txt")))
            {
                throw new InvalidOperationException("The worker did not answer.");
            }

            return Pass(command);
        };

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(0, FinalCheckRounds(plan.Id));
    }
}
