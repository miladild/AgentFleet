using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A step whose check keeps hitting the time limit (or crashing at exit) round after round is told so in the next feedback, and
/// after the third in a row its conversation ends and the next round starts fresh with the diagnosis. Rounds like these are not
/// charged to the repair ladder for a few rounds per attempt, and the step is not parked by any of it. The project folder is
/// real and git is faked, as in the other repair tests.
/// </summary>
public sealed class PlanRunnerStuckGuardTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private const string StuckKind = "stuck-guard";

    private string ProjectDirectory
    {
        get
        {
            string directory = Path.Combine(PlansDirectory, "project");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private string SourceFile => Path.Combine(ProjectDirectory, "a.cs");

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    // The runner's time limit; the printed part differs from run to run, as a real run's does.
    private static string KilledAfter(string printed) =>
        $"Error: command exceeded the 120s timeout and was killed.\n--- partial stdout ---\nok {printed}\n--- partial stderr ---\n";

    private static string CrashedAfter(string printed) =>
        $"Exit code: -1073740791\n--- stdout ---\nok {printed}\n--- stderr ---\nAssertion failed: !(handle->flags & UV_HANDLE_CLOSING)";

    private static string FailWith(params string[] failing) =>
        "Exit code: 1\n--- stdout ---\n" + string.Join("\n", failing.Select(name => "FAILED " + name));

    private static string ReadSource(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

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

    private PlanRunner RepairRunner(FakeStepAgent agent, Func<string, string> verify, HarnessOptions? harness = null) =>
        new(Store, RepairTools(verify), agent, NullLogger.Instance, roundsPerRung: RepairLadder.DefaultRoundsPerRung,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, harness: harness);

    private PlanRecord OneStepPlan() =>
        Store.Approve(Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("edit a", "detail", ["a.cs"], "check1", "standard")]).Id)!;

    // A model that writes a different a.cs each round, so no round changes nothing.
    private static FakeStepAgent Writing(string source)
    {
        int rounds = 0;
        return new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(source, "round " + ++rounds);
            return Task.FromResult("Done.");
        });
    }

    // The results of the approved check, in order, then a pass.
    private static Func<string, string> Results(params string[] results)
    {
        int index = 0;
        return command => command == "check1" && index < results.Length ? results[index++] : Pass(command);
    }

    [Fact]
    public async Task Two_time_limits_in_a_row_put_a_note_in_the_next_feedback_and_the_third_ends_the_conversation_without_parking_the_step()
    {
        PlanRecord plan = OneStepPlan();
        FakeStepAgent agent = Writing(SourceFile);

        await RepairRunner(agent, Results(KilledAfter("a"), KilledAfter("b"), KilledAfter("c"))).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepParked);

        // Round 2's feedback carries no note (one time limit is not a pattern); round 3's does (the second in a row).
        List<FakeStepCall> first = agent.Sessions[0].Messages;
        Assert.DoesNotContain("times in a row", first[1].Text, StringComparison.Ordinal);
        Assert.Contains("hit the time limit 2 times in a row", first[2].Text, StringComparison.Ordinal);
        Assert.Contains("--- partial stdout ---", first[2].Text, StringComparison.Ordinal);

        // The third ends the conversation: the next round is a fresh conversation that starts with the diagnosis.
        Assert.Equal(2, agent.Sessions.Count);
        Assert.Equal(3, first.Count);
        string fresh = agent.Sessions[1].Messages[0].Text;
        Assert.Contains("hit the time limit 3 times in a row", fresh, StringComparison.Ordinal);
        Assert.Contains("next round starts fresh", fresh, StringComparison.Ordinal);
        Assert.True(fresh.IndexOf("3 times in a row", StringComparison.Ordinal) < fresh.IndexOf("--- partial stdout ---", StringComparison.Ordinal),
            "The diagnosis comes before the check's own output.");
    }

    [Fact]
    public async Task Each_note_and_stop_is_logged_with_its_diagnosis_the_repeats_and_the_outcome_class()
    {
        PlanRecord plan = OneStepPlan();

        await RepairRunner(Writing(SourceFile), Results(KilledAfter("a"), KilledAfter("b"), KilledAfter("c"))).RunPlanAsync(plan.Id, default);

        PlanRunEvent[] logged = Store.Get(plan.Id)!.Events!.Where(runEvent => runEvent.Kind == StuckKind).ToArray();
        Assert.Equal(2, logged.Length);
        Assert.All(logged, runEvent => Assert.Equal("time-limit", runEvent.OutcomeClass));
        Assert.StartsWith("note", logged[0].Detail, StringComparison.Ordinal);
        Assert.Contains("2 times in a row", logged[0].Detail, StringComparison.Ordinal);
        Assert.StartsWith("stop", logged[1].Detail, StringComparison.Ordinal);
        Assert.Contains("3 times in a row", logged[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_guard_switched_off_the_runner_behaves_as_before()
    {
        PlanRecord plan = OneStepPlan();
        FakeStepAgent agent = Writing(SourceFile);

        await RepairRunner(agent, Results(KilledAfter("a"), KilledAfter("b"), KilledAfter("c")), new HarnessOptions(StuckGuardEnabled: false))
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == StuckKind);
        Assert.DoesNotContain(agent.Calls, call => call.Prompt.Contains("times in a row", StringComparison.Ordinal));
        // Three failed rounds use up the rung, as always, and the ladder moves on.
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
    }

    [Fact]
    public async Task Ordinary_failures_between_time_limits_end_the_streak_and_get_no_note()
    {
        PlanRecord plan = OneStepPlan();
        FakeStepAgent agent = Writing(SourceFile);

        await RepairRunner(agent, Results(KilledAfter("a"), FailWith("t1", "t2"), KilledAfter("b"), FailWith("t1"))).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == StuckKind);
        Assert.DoesNotContain(agent.Calls, call => call.Prompt.Contains("times in a row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_first_rounds_of_each_attempt_that_hit_an_infrastructure_outcome_are_not_charged_to_the_rung()
    {
        // Time limits and crashes alternate, so no class repeats and the guard stays quiet; the exemption alone is under test. With
        // three rounds a rung and three exempt rounds per attempt, the rung is used up only by the fourth to sixth failing round.
        PlanRecord plan = OneStepPlan();
        FakeStepAgent agent = Writing(SourceFile);
        string[] results = [KilledAfter("a"), CrashedAfter("b"), KilledAfter("c"), CrashedAfter("d"), KilledAfter("e"), CrashedAfter("f")];

        await RepairRunner(agent, Results(results)).RunPlanAsync(plan.Id, default);

        PlanRunEvent[] events = Store.Get(plan.Id)!.Events!.ToArray();
        int failedBeforeClimb = events.TakeWhile(runEvent => runEvent.Kind != RunEventKind.RungChanged)
            .Count(runEvent => runEvent.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed);
        Assert.Equal(6, failedBeforeClimb);
        Assert.DoesNotContain(events, runEvent => runEvent.Kind == StuckKind);
    }

    [Fact]
    public async Task Without_the_guard_the_same_rounds_use_up_the_rung_after_three()
    {
        PlanRecord plan = OneStepPlan();
        FakeStepAgent agent = Writing(SourceFile);
        string[] results = [KilledAfter("a"), CrashedAfter("b"), KilledAfter("c"), CrashedAfter("d"), KilledAfter("e"), CrashedAfter("f")];

        await RepairRunner(agent, Results(results), new HarnessOptions(StuckGuardEnabled: false)).RunPlanAsync(plan.Id, default);

        PlanRunEvent[] events = Store.Get(plan.Id)!.Events!.ToArray();
        int failedBeforeClimb = events.TakeWhile(runEvent => runEvent.Kind != RunEventKind.RungChanged)
            .Count(runEvent => runEvent.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed);
        Assert.Equal(3, failedBeforeClimb);
    }

    [Fact]
    public async Task The_exemption_stops_at_the_ceiling_set_in_the_options()
    {
        PlanRecord plan = OneStepPlan();
        FakeStepAgent agent = Writing(SourceFile);
        string[] results = [KilledAfter("a"), CrashedAfter("b"), KilledAfter("c"), CrashedAfter("d"), KilledAfter("e"), CrashedAfter("f")];

        await RepairRunner(agent, Results(results), new HarnessOptions(StuckUnchargedRoundsPerAttempt: 1)).RunPlanAsync(plan.Id, default);

        PlanRunEvent[] events = Store.Get(plan.Id)!.Events!.ToArray();
        int failedBeforeClimb = events.TakeWhile(runEvent => runEvent.Kind != RunEventKind.RungChanged)
            .Count(runEvent => runEvent.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed);
        Assert.Equal(4, failedBeforeClimb);
    }
}
