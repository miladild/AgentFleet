namespace AgentFleet.Tests;

// A ruling that the code is wrong is given once per step. Measured: the step was retried by the runner three minutes later, the retry
// forgot the ruling and asked the ruler again, and the second answer was no verdict. The runner's own retry keeps the ruling; a retry
// or approval by the user starts the step fresh.
public sealed partial class PlanRunnerTests
{
    private const string CarriedParagraph = "Close the server in a finally block.";

    private static string CodeWrongDetail() =>
        "The check failed 2 rounds in a row on tests the step wrote (tests/LimitTests.cs). The ruler (worker-b) found the test right: Close the server."
        + PlanRunner.RulingNoteMarker
        + "An independent model read the failing test this step wrote and found it right, so the code must change: " + CarriedParagraph;

    private void RulingEvent(string planId, int stepId, string verdict, string? detail = null) =>
        Store.AddEvent(planId, stepId, 1, RunEventKind.TestRuling, detail: detail ?? (verdict == "CodeWrong" ? CodeWrongDetail() : "ruling"), failureClass: verdict);

    private void RetryEvent(string planId, int stepId, bool automatic) =>
        Store.AddEvent(planId, stepId, null, RunEventKind.StepRetried, detail: "retry", failureClass: automatic ? FleetPlanStore.AutoRetryClass : null);

    private string? CarriedAfter(Action<PlanRecord> seed)
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            seed(setup.Plan);
            var fake = WritesItsOwnTest(project, new RulingBudget(0, _ => new StepAgentReply("", 0, false)));
            PlanRunner runner = OwnTestRunner(new RulingAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, _ => string.Empty);
            return runner.CarriedRulingNote(setup.Plan.Id, 1);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public void A_code_wrong_ruling_is_carried_across_the_runners_own_retry()
    {
        string? note = CarriedAfter(plan => { RulingEvent(plan.Id, 1, "CodeWrong"); RetryEvent(plan.Id, 1, automatic: true); });

        Assert.Equal("An independent model read the failing test this step wrote and found it right, so the code must change: " + CarriedParagraph, note);
    }

    [Fact]
    public void A_code_wrong_ruling_is_carried_when_no_retry_happened_yet()
    {
        Assert.NotNull(CarriedAfter(plan => RulingEvent(plan.Id, 1, "CodeWrong")));
    }

    [Fact]
    public void A_retry_by_the_user_starts_the_step_without_the_ruling()
    {
        Assert.Null(CarriedAfter(plan => { RulingEvent(plan.Id, 1, "CodeWrong"); RetryEvent(plan.Id, 1, automatic: false); }));
    }

    [Fact]
    public void An_automatic_retry_after_a_retry_by_the_user_does_not_bring_back_an_older_ruling()
    {
        Assert.Null(CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong");
            RetryEvent(plan.Id, 1, automatic: false);
            RetryEvent(plan.Id, 1, automatic: true);
        }));
    }

    [Fact]
    public void Approving_the_plan_again_starts_every_step_without_the_ruling()
    {
        Assert.Null(CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong");
            RetryEvent(plan.Id, 1, automatic: true);
            Store.AddEvent(plan.Id, null, null, RunEventKind.RetryApproved, detail: "approved again");
        }));
    }

    [Theory]
    [InlineData("TestWrong")]
    [InlineData("NoVerdict")]
    [InlineData("Refused")]
    [InlineData("Undone")]
    public void Only_a_code_wrong_ruling_is_carried(string verdict)
    {
        Assert.Null(CarriedAfter(plan => { RulingEvent(plan.Id, 1, verdict); RetryEvent(plan.Id, 1, automatic: true); }));
    }

    [Fact]
    public void A_ruling_on_another_step_is_not_carried()
    {
        Assert.Null(CarriedAfter(plan => { RulingEvent(plan.Id, 2, "CodeWrong"); RetryEvent(plan.Id, 1, automatic: true); }));
    }

    [Fact]
    public void The_latest_code_wrong_ruling_is_the_one_carried()
    {
        string? note = CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetail().Replace(CarriedParagraph, "An older paragraph."));
            RulingEvent(plan.Id, 1, "CodeWrong");
        });

        Assert.EndsWith(CarriedParagraph, note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ruling_the_event_log_has_dropped_is_not_carried()
    {
        Assert.Null(CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong");
            for (int index = 0; index < FleetPlanStore.MaxEvents + 10; index++)
            {
                Store.AddEvent(plan.Id, 1, 1, RunEventKind.AttemptEnded, detail: "filler " + index);
            }
        }));
    }

    [Fact]
    public async Task A_step_that_starts_with_a_carried_ruling_is_not_ruled_on_again_and_its_later_rounds_carry_the_ruling()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            RulingEvent(setup.Plan.Id, 1, "CodeWrong");
            RetryEvent(setup.Plan.Id, 1, automatic: true);
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks < 5 ? TestsFailOutput : TestsPassOutput, roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.Equal(0, budget.Calls);
            Assert.True(fake.Calls.Count >= 4, $"only {fake.Calls.Count} rounds ran");
            Assert.All(fake.Calls.Skip(1), call => Assert.Contains(CarriedParagraph, call.Prompt, StringComparison.Ordinal));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_disputed_test_is_not_ruled_on_again_after_the_runners_own_retry_kept_a_code_wrong_ruling()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            RulingEvent(setup.Plan.Id, 2, "CodeWrong");
            RetryEvent(setup.Plan.Id, 2, automatic: true);
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => true, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.Equal(0, budget.Calls);
            Assert.Contains(Store.Get(setup.Plan.Id)!.Events!, e => e.Kind == RunEventKind.TestRuling && e.Detail.Contains("not ruled on: already-ruled"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_code_wrong_ruling_is_logged_with_the_note_the_worker_was_given()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(CodeWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRunEvent ruling = Assert.Single(Store.Get(setup.Plan.Id)!.Events!, e => e.Kind == RunEventKind.TestRuling);
            Assert.Contains(PlanRunner.RulingNoteMarker + "An independent model read the failing test this step wrote and found it right, so the code must change: The limit must refuse zero",
                ruling.Detail, StringComparison.Ordinal);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
