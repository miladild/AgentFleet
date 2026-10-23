namespace AgentFleet.Tests;

// A repair run replayed over the check outputs of one real run, in its order. Each round the model writes "round N" and the
// approved check prints output N. A round that leaves a test file that does not load must be undone, and a round that loads
// and fails fewer tests must become the best state, so the step never ends on a file that does not load while a loading round
// existed. Each run has a time limit, and the model's calls are counted: a runaway loop fails with a count, not a hang.
public sealed partial class PlanRunnerTests
{
    private static readonly TimeSpan ReplayTimeLimit = TimeSpan.FromSeconds(60);
    private const int ReplayRounds = 7;
    private const int ReplayCallLimit = 30;

    [Fact]
    public async Task A_round_that_leaves_a_file_that_does_not_load_is_undone_and_the_best_loading_round_is_kept()
    {
        string[] outputs =
        [
            PlanCheckOutputFixtures.Round01, PlanCheckOutputFixtures.Round02, PlanCheckOutputFixtures.Round03,
            PlanCheckOutputFixtures.Round04, PlanCheckOutputFixtures.Round05, PlanCheckOutputFixtures.Round06,
            PlanCheckOutputFixtures.Round07
        ];
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        // What the file held when each round started: the state the round began from, after any undo.
        var starts = new List<string>();
        var agent = new FakeStepAgent((_, _, _) =>
        {
            starts.Add(File.Exists(SourceFile) ? File.ReadAllText(SourceFile) : string.Empty);
            int round = starts.Count;
            if (round > ReplayCallLimit) throw new InvalidOperationException("The repair loop did not stop.");
            File.WriteAllText(SourceFile, $"round {Math.Min(round, ReplayRounds)}");
            return Task.FromResult($"Wrote round {round}.");
        });
        Func<string, string> verify = _ => File.Exists(SourceFile) &&
            int.TryParse(File.ReadAllText(SourceFile).Replace("round ", string.Empty), out int written) && written is >= 1 and <= ReplayRounds
                ? outputs[written - 1]
                : Pass("check");

        await RepairRunner(agent, verify, options, health, roundsPerRung: ReplayRounds)
            .RunPlanAsync(plan.Id, default).WaitAsync(ReplayTimeLimit);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.InRange(starts.Count, ReplayRounds, ReplayCallLimit);
        Assert.Equal(new[] { "", "round 1", "round 2", "round 2", "round 4", "round 5", "round 5" }, starts.Take(ReplayRounds));

        // Round 1 (does not load) is the first best state, and round 2 (loads, 9 failing) is not undone because of it. Round 3 (does not
        // load) is undone back to round 2. Round 4 (7 failing) and then the round after it (5 failing) are better, so the best is the
        // 5-failing state; the runner numbers rounds per rung and an improving round does not use one up, so both carry the label 4.
        // Round 6 (does not load) is undone back to that state, and round 7 (5 failing again) ties and is kept.
        PlanRunEvent[] undone = after.Events!.Where(runEvent => runEvent.Kind == RunEventKind.RoundRolledBack).ToArray();
        Assert.Equal(2, undone.Length);
        Assert.Contains("code that does not build (1 error code) where round 2 had 9 failing, so it was undone", undone[0].Detail);
        Assert.Contains("code that does not build (1 error code) where round 4 had 5 failing, so it was undone", undone[1].Detail);

        // The check never passes, so the step is not done; what it ends on must be a file that loads.
        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.DoesNotContain(File.ReadAllText(SourceFile), new[] { "round 1", "round 3", "round 6" });
    }
}
