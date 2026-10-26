namespace AgentFleet.Tests;

// The fail-first acceptance refuses a tests step whose test file is a placeholder, and keeps accepting a file of real tests.
public sealed partial class PlanRunnerTests
{
    private const string PlaceholderLimitTests = "using Xunit;\npublic class LimitTests\n{\n    [Fact]\n    public void Rejects_zero()\n    {\n        Assert.True(false);\n    }\n}\n";
    private const string RealFailingLimitTests = "using Xunit;\npublic class LimitTests\n{\n    [Fact]\n    public void Rejects_zero()\n    {\n        Assert.Equal(1, Limit.Check(0));\n    }\n}\n";

    [Fact]
    public async Task Test_first_A_placeholder_tests_step_is_refused_and_the_feedback_says_why()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            var fake = WritesTestRounds(project, "tests/LimitTests.cs", PlaceholderLimitTests, RealFailingLimitTests);
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.True(fake.Calls.Count > 1);
            Assert.Equal(["placeholder", "accepted"], AcceptedOutcomes(after));
            Assert.Contains("These tests fail by construction", fake.Calls[1].Prompt);
            Assert.Contains("tests/LimitTests.cs line 7: Assert.True(false);", fake.Calls[1].Prompt);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_A_placeholder_refusal_is_not_marked_as_covering_a_requirement()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, LimitTestsStep());
            Store.Update(setup.Plan.Id, current => current with { Requirements = PlanRequirementsOf("Rejects zero", "Rejects negatives") });
            // The placeholder names the first requirement, and the second requirement has no test at all. The refusal must come
            // first, so the outcome is the placeholder one and not the uncovered one.
            var fake = WritesTestRounds(project, "tests/LimitTests.cs", PlaceholderLimitTests.Replace("Rejects_zero", "R1_rejects_zero"));
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.NotEqual(StepStatus.Done, after.Steps[0].Status);
            Assert.NotEmpty(AcceptedOutcomes(after));
            Assert.All(AcceptedOutcomes(after), outcome => Assert.Equal("placeholder", outcome));
            Assert.All(after.Requirements!, requirement => Assert.Empty(requirement.CoveredBy));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_A_tests_step_of_real_failing_tests_is_still_accepted()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            var fake = WritesTestRounds(project, "tests/LimitTests.cs", RealFailingLimitTests);
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(["accepted"], AcceptedOutcomes(after));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
