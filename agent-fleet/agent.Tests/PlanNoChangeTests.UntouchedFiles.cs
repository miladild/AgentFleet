namespace AgentFleet.Tests;

/// <summary>
/// A passing check does not finish a step that lists files and left one of them as it was. The worker gets one more round
/// in the same conversation. If a listed file is still untouched after that round, the step is not done either, unless the
/// round's report_blocker names the file; the step is sent back and climbs the repair ladder like any failed check.
/// </summary>
public sealed partial class PlanNoChangeTests
{
    private const string UntouchedKind = "untouched-files";

    private static PlanStepInput StepListing(string title, params string[] files) => new(title, "detail", files, "check", "standard");

    private int UntouchedRounds(string planId) => Store.Get(planId)?.Events?.Count(runEvent => runEvent.Kind == UntouchedKind) ?? 0;

    [Fact]
    public async Task A_passing_step_that_left_a_listed_file_untouched_gets_one_more_round_in_the_same_conversation()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "old b");
        PlanRecord plan = OneStepPlan(StepListing("edit both", "a.txt", "b.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(ProjectDirectory, round == 1 ? "a.txt" : "b.txt"), $"new {round}");
            return Task.FromResult("Changed one file.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Single(agent.Sessions);
        Assert.Equal(1, UntouchedRounds(plan.Id));
        string extra = agent.Sessions[0].Messages[1].Text;
        Assert.Contains("were not changed: b.txt", extra, StringComparison.Ordinal);
        Assert.Contains("report_blocker", extra, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listed_file_still_untouched_after_the_extra_round_sends_the_step_back_and_names_it()
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

            if (round == 3)
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "new b");
                return Task.FromResult("Changed b.");
            }

            return Task.FromResult("Looked again, nothing else to change.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Single(agent.Sessions);
        Assert.Equal(1, UntouchedRounds(plan.Id));
        Assert.Contains("still unchanged after one more round: b.txt", agent.Sessions[0].Messages[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listed_file_that_stays_untouched_keeps_the_step_from_being_done()
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

            return Task.FromResult("Looked again, nothing else to change.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.NotEqual(StepStatus.Done, after.Steps[0].Status);

        // The extra round is given once per attempt, and each attempt is a new conversation, so one round per attempt is the most.
        int[] extraRoundAttempts = (after.Events ?? []).Where(runEvent => runEvent.Kind == UntouchedKind).Select(runEvent => runEvent.Attempt ?? 0).ToArray();
        Assert.NotEmpty(extraRoundAttempts);
        Assert.Equal(extraRoundAttempts.Length, extraRoundAttempts.Distinct().Count());
        Assert.DoesNotContain(after.Events ?? [], runEvent => runEvent.Kind == RunEventKind.StepDone);
        Assert.Contains(after.Events ?? [], runEvent =>
            runEvent.Detail.Contains("still unchanged after one more round: b.txt", StringComparison.Ordinal));
        Assert.True(agent.Calls.Count > 2, "the step is sent back again rather than accepted");
    }

    [Fact]
    public async Task A_report_blocker_that_names_the_untouched_file_lets_the_step_pass_after_the_extra_round()
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
            }

            return Task.FromResult(round == 1 ? "Changed a." : "b.txt is generated by the build, so it needs no change.");
        })
        {
            CallsOf = _ => round == 2 ? Blocker("b.txt is generated by the build, so it needs no change.") : []
        };

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, UntouchedRounds(plan.Id));
    }

    [Fact]
    public async Task A_report_blocker_that_does_not_name_the_untouched_file_does_not_let_the_step_pass()
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
            }

            return Task.FromResult(round == 1 ? "Changed a." : "Nothing else to do.");
        })
        {
            CallsOf = _ => round == 2 ? Blocker("the check is fine as it is") : []
        };

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.NotEqual(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.True(agent.Calls.Count > 2, "the step is sent back again rather than accepted");
        Assert.Contains("still unchanged after one more round: b.txt", agent.Sessions[0].Messages[2].Text, StringComparison.Ordinal);
    }

    private static IReadOnlyList<AgentToolCall> Blocker(string evidence) =>
        [new AgentToolCall("report_blocker", new Dictionary<string, string?> { ["kind"] = "other", ["evidence"] = evidence })];

    [Fact]
    public async Task A_round_that_changes_every_listed_file_gets_no_extra_round()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "old b");
        PlanRecord plan = OneStepPlan(StepListing("edit both", "a.txt", "b.txt"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "new b");
            return Task.FromResult("Changed both.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UntouchedRounds(plan.Id));
    }

    [Fact]
    public async Task A_listed_file_that_is_never_created_keeps_the_step_from_passing_until_it_exists()
    {
        // The existing rule for files a step was meant to create fails a passing check while such a file is missing,
        // so the step is not finished without it; the file's arrival is what finishes it.
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a and add new.txt", "a.txt", "new.txt"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(ProjectDirectory, round == 1 ? "a.txt" : "new.txt"), $"new {round}");
            return Task.FromResult("Changed a file.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Contains("new.txt", agent.Sessions[0].Messages[1].Text, StringComparison.Ordinal);
        Assert.Equal(0, UntouchedRounds(plan.Id));
    }

    [Fact]
    public async Task A_step_that_lists_no_files_gets_no_untouched_round_when_it_changes_another_file()
    {
        PlanRecord plan = OneStepPlan(StepNoFiles("write notes"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "notes.txt"), "notes");
            return Task.FromResult("Wrote notes.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(0, UntouchedRounds(plan.Id));

        // notes.txt is a file the plan does not name, so the extra round for unnamed files applies instead (it is not a listed file).
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, UnlistedRounds(plan.Id));
    }

    [Fact]
    public async Task A_wildcard_listed_in_a_step_is_not_a_file_to_wait_for()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "old.log"), "log");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt", "*.log"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UntouchedRounds(plan.Id));
    }

    [Fact]
    public async Task The_last_step_of_a_longer_plan_gets_no_extra_round_for_a_listed_file_it_left_alone()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "");
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "old b");
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                StepNamed("setup", "setup.json"),
                StepListing("validate", "a.txt", "b.txt")
            ]).Id)!;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("(1 of 2)"))
            {
                File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "{}");
                return Task.FromResult("Set it up.");
            }

            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            return Task.FromResult("Validated.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(0, UntouchedRounds(plan.Id));
    }

    [Fact]
    public async Task Where_git_cannot_tell_no_extra_round_is_given_for_a_listed_file()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "old b");
        PlanRecord plan = OneStepPlan(StepListing("edit both", "a.txt", "b.txt"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            return Task.FromResult("Changed a.");
        });

        await NoGitRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UntouchedRounds(plan.Id));
    }

    [Fact]
    public async Task A_file_an_earlier_round_of_the_step_recorded_as_changed_is_not_waited_for_after_a_restart()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "old b");
        PlanRecord plan = OneStepPlan(StepListing("edit both", "a.txt", "b.txt"));

        // A round before a restart changed b.txt. The runner's own record of the step starts after it, so only the run log says so.
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.RoundClassified, "standard", detail: "Round classified: earlier round.",
            changedFiles: ["b.txt"]);
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UntouchedRounds(plan.Id));
    }

    [Fact]
    public async Task A_frozen_test_file_the_step_lists_is_not_waited_for()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        File.WriteAllText(Path.Combine(ProjectDirectory, "b.txt"), "accepted b");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt", "b.txt"));
        Store.AddFrozenTests(plan.Id, new PlanFrozenTestSet(1, "check", [new PlanFrozenTestFile("b.txt", "0")]));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, UntouchedRounds(plan.Id));
    }

    // The runner's own check cannot reach a file that never existed (the existing files-to-create rule fails the check first),
    // so the rule's handling of such a file is tested on the helper it uses.
    [Fact]
    public void Listed_paths_that_no_change_touched_and_that_are_not_folders_are_the_untouched_ones()
    {
        string changedFile = Path.Combine(ProjectDirectory, "a.txt");
        string untouchedFile = Path.Combine(ProjectDirectory, "b.txt");
        string missingFile = Path.Combine(ProjectDirectory, "new.txt");
        string folder = Path.Combine(ProjectDirectory, "src");
        string bigFile = Path.Combine(ProjectDirectory, "big.bin");
        File.WriteAllText(changedFile, "a");
        File.WriteAllText(untouchedFile, "b");
        File.WriteAllText(bigFile, "big");
        Directory.CreateDirectory(folder);

        string[] untouched = PlanRunner.UntouchedListed(
            [changedFile, untouchedFile, missingFile, folder, bigFile],
            new HashSet<string>([changedFile]),
            new HashSet<string>([bigFile])).ToArray();

        Assert.Equal([untouchedFile, missingFile], untouched);
    }
}
