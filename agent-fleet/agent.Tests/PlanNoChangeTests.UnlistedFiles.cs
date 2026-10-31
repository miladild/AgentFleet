using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A passing check does not accept a step that created files the plan does not name, such as scratch scripts left in the project
/// folder. The worker gets one more round in the same conversation to delete them, once per attempt. The step is accepted after
/// that round even if files remain; the step-done note still names them.
/// </summary>
public sealed partial class PlanNoChangeTests
{
    private const string UnlistedKind = "unlisted-files";
    private const string TestsFailing = "Exit code: 1\n  Failed Limits.rejects_zero [1 ms]\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1";

    private int UnlistedRounds(string planId) => Store.Get(planId)?.Events?.Count(runEvent => runEvent.Kind == UnlistedKind) ?? 0;

    private void WriteProjectFile(string relative, string text = "scratch")
    {
        string path = Path.Combine(ProjectDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task A_passing_step_that_created_a_file_the_plan_does_not_name_gets_one_more_round_in_the_same_conversation()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), $"new {round}");
            if (round == 1)
            {
                WriteProjectFile("scratch.js", "try it");
            }
            else
            {
                File.Delete(Path.Combine(ProjectDirectory, "scratch.js"));
            }

            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Single(agent.Sessions);
        Assert.Equal(1, UnlistedRounds(plan.Id));
        string extra = agent.Sessions[0].Messages[1].Text;
        Assert.Contains("were created and are not part of this step: scratch.js", extra, StringComparison.Ordinal);
        Assert.Contains("delete_file", extra, StringComparison.Ordinal);
        Assert.Contains(after.Events!, e => e.Kind == UnlistedKind && e.Detail.Contains("scratch.js", StringComparison.Ordinal));
        Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.StepDone && e.Detail.Contains("does not name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_still_there_after_the_extra_round_does_not_send_the_step_back_and_the_step_done_note_names_it()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), $"new {round}");
            if (round == 1)
            {
                WriteProjectFile("scratch.js");
            }

            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, UnlistedRounds(plan.Id));
        Assert.True(File.Exists(Path.Combine(ProjectDirectory, "scratch.js")), "the fleet reports leftovers, it never deletes them");
        Assert.Contains(after.Events!, e => e.Kind == RunEventKind.StepDone && e.Detail.Contains("scratch.js", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_that_created_only_listed_files_gets_no_extra_round()
    {
        PlanRecord plan = OneStepPlan(StepListing("write new", "new.txt"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("new.txt", "made");
            return Task.FromResult("Wrote new.txt.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UnlistedRounds(plan.Id));
    }

    [Fact]
    public async Task Incidental_files_a_step_leaves_behind_do_not_get_an_extra_round()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            WriteProjectFile("build.log", "build output");
            WriteProjectFile("tsconfig.tsbuildinfo", "{}");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UnlistedRounds(plan.Id));
    }

    [Fact]
    public async Task The_final_check_of_a_longer_plan_gets_no_extra_round_for_a_file_it_did_not_name()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "");
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                StepNamed("setup", "setup.json"),
                StepListing("validate", "a.txt")
            ]).Id)!;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("(1 of 2)"))
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "{}");
                return Task.FromResult("Set it up.");
            }

            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            WriteProjectFile("scratch.js");
            return Task.FromResult("Validated.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(0, UnlistedRounds(plan.Id));
    }

    [Fact]
    public async Task The_final_check_of_a_longer_plan_still_gets_a_round_for_a_file_a_shell_redirect_made()
    {
        const string artifact = "$null";
        File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "");
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                StepNamed("setup", "setup.json"),
                StepListing("validate", "a.txt")
            ]).Id)!;
        int validations = 0;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("(1 of 2)"))
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "{}");
                return Task.FromResult("Set it up.");
            }

            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), $"new {++validations}");
            if (validations == 1)
            {
                WriteProjectFile(artifact);
            }
            else
            {
                File.Delete(Path.Combine(ProjectDirectory, artifact));
            }

            return Task.FromResult("Validated.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Equal(1, UnlistedRounds(plan.Id));
    }

    [Fact]
    public async Task The_tests_step_of_test_first_mode_gets_no_extra_round_for_a_file_it_did_not_name()
    {
        PlanRecord plan = OneStepPlan(new PlanStepInput("Write limits tests", "Reject zero", ["tests/LimitTests.cs"], "dotnet test", "standard",
            Kind: "tests"));
        // A stray file in the tests step must be a test file: test-first mode refuses any other file the step changes first.
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("tests/LimitTests.cs", "tests");
            WriteProjectFile("tests/ScratchTests.cs", "scratch");
            return Task.FromResult("Wrote the tests.");
        });
        var runner = new PlanRunner(Store, RepairTools(_ => TestsFailing), new TestWriterAgent(agent), NullLogger.Instance,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, testFirst: TestFirstMode.On);

        await runner.RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UnlistedRounds(plan.Id));
    }

    // The tests step of test-first mode opens its conversation with the test-writer role; the fake answers that role with an audit
    // reply, so this wrapper sends every conversation to the same fake.
    private sealed class TestWriterAgent(FakeStepAgent inner) : IStepAgent
    {
        public Task<StepAgentReply> RunStepAsync(string prompt, string tier, CancellationToken cancellationToken) =>
            inner.RunStepAsync(prompt, tier, cancellationToken);

        public IStepSession OpenSession() => inner.OpenSession();

        public IStepSession OpenSession(string? role) => inner.OpenSession();
    }

    [Fact]
    public async Task A_file_an_earlier_failing_round_created_is_asked_about_once_the_check_passes()
    {
        // The failing check's note named scratch.js, and the passing check still asks about it: a note is not a deletion.
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), $"new {round}");
            if (round == 1)
            {
                WriteProjectFile("scratch.js");
            }

            return Task.FromResult("Changed a.");
        });
        int checks = 0;
        Func<string, string> verify = command =>
            command == "check" && ++checks == 1 ? "Exit code: 1\n--- stdout ---\nFAILED test" : Pass(command);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Contains("scratch.js", agent.Calls[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(1, UnlistedRounds(plan.Id));
        Assert.Contains("were created and are not part of this step: scratch.js", agent.Sessions[0].Messages[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_one_extra_round_is_given_per_attempt_so_a_file_created_after_it_is_accepted()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), $"new {round}");
            WriteProjectFile(round == 1 ? "first.js" : "second.js");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, UnlistedRounds(plan.Id));
        Assert.Contains("first.js", agent.Sessions[0].Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_untouched_rule_goes_first_and_the_next_passing_check_asks_about_unnamed_files()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "old b");
        PlanRecord plan = OneStepPlan(StepListing("edit both", "a.txt", "b.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            if (round == 1)
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
                return Task.FromResult("Changed a.");
            }

            File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "new b");
            if (round == 2)
            {
                WriteProjectFile("later.js");
            }

            return Task.FromResult("Changed b.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Equal(1, UntouchedRounds(plan.Id));
        Assert.Equal(1, UnlistedRounds(plan.Id));
        string untouched = agent.Sessions[0].Messages[1].Text;
        Assert.Contains("were not changed: b.txt", untouched, StringComparison.Ordinal);
        Assert.DoesNotContain("were created and are not part of this step", untouched, StringComparison.Ordinal);
        Assert.Contains("were created and are not part of this step: later.js", agent.Sessions[0].Messages[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_named_by_the_note_of_an_untouched_round_is_asked_about_once_the_check_passes()
    {
        // The untouched round's failing note names scratch.js too, and the next passing check still asks about it.
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "old b");
        PlanRecord plan = OneStepPlan(StepListing("edit both", "a.txt", "b.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            if (round == 1)
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
                WriteProjectFile("scratch.js");
                return Task.FromResult("Changed a.");
            }

            File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "new b");
            return Task.FromResult("Changed b.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Equal(1, UntouchedRounds(plan.Id));
        Assert.Equal(1, UnlistedRounds(plan.Id));
        Assert.DoesNotContain("were created and are not part of this step", agent.Sessions[0].Messages[1].Text, StringComparison.Ordinal);
        Assert.Contains("were created and are not part of this step: scratch.js", agent.Sessions[0].Messages[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_ten_unnamed_files_list_ten_and_count_the_rest()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), $"new {round}");
            if (round == 1)
            {
                for (int number = 1; number <= 12; number++)
                {
                    WriteProjectFile($"f{number:00}.txt");
                }
            }

            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        string extra = agent.Sessions[0].Messages[1].Text;
        Assert.Contains("f01.txt, f02.txt, f03.txt, f04.txt, f05.txt, f06.txt, f07.txt, f08.txt, f09.txt, f10.txt, and 2 more.", extra, StringComparison.Ordinal);
        Assert.DoesNotContain("f11.txt", extra, StringComparison.Ordinal);
        Assert.DoesNotContain("f12.txt", extra, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_in_the_fleet_folder_do_not_get_an_extra_round()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            WriteProjectFile(Path.Combine(".agent-fleet", "plans", "notes.md"));
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UnlistedRounds(plan.Id));
    }
}
