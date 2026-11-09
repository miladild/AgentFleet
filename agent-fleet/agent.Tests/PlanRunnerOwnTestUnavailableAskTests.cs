namespace AgentFleet.Tests;

// A ruler asked about a test the step wrote that never answers (its ask ends unavailable after the time limit, or the call fails) counts
// toward the ask limit, as a ruler that gives no usable verdict does. A ruler skipped because its machine was not ready never asks, so
// it does not count, and a ruler that is ready again is asked.
public sealed partial class PlanRunnerTests
{
    private static PlanRunEvent OwnTestAskEvent(string detail) =>
        new(DateTimeOffset.UtcNow, 1, 1, PlanRunner.RulerTimingEvent, "standard", "worker-b", detail, "worker-a", null);

    private static PlanRunEvent OwnTestRulingEvent(string verdict, string detail) =>
        new(DateTimeOffset.UtcNow, 1, 1, RunEventKind.TestRuling, "standard", "worker-b", detail, "worker-a", null, verdict);

    [Fact]
    public void OwnTest_only_asks_that_got_no_reply_and_rulings_without_a_usable_answer_count_toward_the_limit()
    {
        PlanRunEvent[] events =
        [
            OwnTestAskEvent("The ruler (worker-b) gave no reply after 8 min (time limit reached)."),
            OwnTestRulingEvent("Unavailable", "The ruler (worker-b) did not answer within 8 min; nothing changes."),
            OwnTestAskEvent("The ruler (worker-b) answered after 2 min with 40 characters."),
            OwnTestRulingEvent("NoVerdict", "The ruler (worker-b) gave no TEST-WRONG or CODE-WRONG verdict: analysis only. Nothing changes."),
            OwnTestRulingEvent("Refused", "The ruler (worker-b) asked for a correction that is refused, so nothing changed."),
            OwnTestRulingEvent("Unavailable", "No independent model is available to rule on the step's own failing test (the other machines are not ready); nothing changes."),
        ];

        // The ask with no reply, the ruling with no verdict and the refused correction count. The answered ask, the ask-ended ruling
        // (counted through its ask) and the skipped ruler do not add to the count.
        Assert.Equal(3, PlanRunner.UnresolvedAsks(events));
    }

    [Fact]
    public void OwnTest_a_ruler_skipped_because_its_machine_was_not_ready_never_counts()
    {
        PlanRunEvent skipped = OwnTestRulingEvent("Unavailable",
            "No independent model is available to rule on the step's own failing test (the other machines are not ready); nothing changes.");

        Assert.Equal(0, PlanRunner.UnresolvedAsks([skipped, skipped, skipped]));
    }

    [Fact]
    public async Task OwnTest_ruler_that_never_answers_is_asked_twice_and_no_third_time()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(2, _ => throw new InvalidOperationException("the ruler is down"));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            Func<string, string> run = command =>
            {
                if (command != "dotnet test") return BuildPassOutput;
                checks++;
                return TestsFailOutput;
            };

            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, run, roundsPerRung: 10)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            // The third ask would come at the sixth check, so the run must reach it.
            Assert.True(checks >= 6, $"only {checks} checks ran; the third ask would have come at the sixth");
            Assert.Equal(2, budget.Calls);
            Assert.Equal(0, budget.Violations);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            PlanRunEvent[] rulings = after.Events!.Where(e => e.Kind == RunEventKind.TestRuling).ToArray();
            Assert.Equal(2, rulings.Length);
            Assert.All(rulings, ruling =>
            {
                Assert.Equal("Unavailable", ruling.FailureClass);
                Assert.Contains("could not be reached", ruling.Detail, StringComparison.Ordinal);
            });
            Assert.Equal(2, after.Events!.Count(e => e.Kind == PlanRunner.RulerTimingEvent && e.Detail.Contains("gave no reply after", StringComparison.Ordinal)));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task OwnTest_ruler_skipped_because_its_machine_was_not_ready_is_not_counted_and_is_asked_once_it_is_ready()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            setup.Health.MarkUnavailable("worker-b", "unreachable");
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            string Run(string command)
            {
                if (command != "dotnet test") return BuildPassOutput;
                // Two rulings are skipped (after rounds two and four); the ruler is ready again before the third one.
                if (++checks == 5) setup.Health.MarkAnswered("worker-b");
                return File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")).Contains("CORRECTED", StringComparison.Ordinal)
                    ? TestsPassOutput
                    : TestsFailOutput;
            }

            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, Run, roundsPerRung: 10)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            PlanRunEvent[] rulings = after.Events!.Where(e => e.Kind == RunEventKind.TestRuling).ToArray();
            Assert.Equal(2, rulings.Count(e => e.FailureClass == "Unavailable" && e.Detail.Contains("No independent model is available", StringComparison.Ordinal)));
            Assert.Single(rulings, e => e.FailureClass == "TestWrong");
            Assert.Equal(1, budget.Calls);
            Assert.Equal(0, budget.Violations);
            Assert.Equal(PlanStatus.Done, after.Status);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
