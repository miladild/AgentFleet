using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// Runner-level tests of a ruling on a test the step wrote itself: measured, a worker wrote a wrong test and then changed correct
// code to satisfy it for fifteen rounds. After rounds in a row failing only on such tests, an independent model rules on it.
public sealed partial class PlanRunnerTests
{
    private static PlanStepInput StepWithItsOwnTest() =>
        new("Implement limits", "Reject zero, with a test for it", ["src/limit.cs", LimitTestFile], "dotnet test", "standard");

    private PlanRunner OwnTestRunner(IStepAgent agent, FakeWorkerWorkspaceManager workspaces, FleetOptions options, FleetHealthMonitor health,
        Func<string, string> run, HarnessOptions? harness = null) =>
        new(Store, new PlanTools(Store, Valid, (command, directory, token) => command.StartsWith("git ", StringComparison.Ordinal)
                ? FailFirstTests.RunShell(command, directory, token) : Task.FromResult(run(command))),
            agent, NullLogger.Instance, fleetOptions: options, healthMonitor: health, workerWorkspaces: workspaces,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, roundsPerRung: 2,
            failFirst: FailFirstMode.Off, testFirst: TestFirstMode.Off, harness: harness);

    // The step's model writes its test in the first message and changes the code in every message after it.
    private static FakeStepAgent WritesItsOwnTest(string project, RulingBudget budget, string testFile = WrongTest)
    {
        int calls = 0;
        return new FakeStepAgent((_, _, _) =>
        {
            if (calls++ == 0) WriteProjectFile(project, LimitTestFile, testFile);
            WriteProjectFile(project, "src/limit.cs", $"version {calls}\n");
            return Task.FromResult("The code is right; the provider in my test always returns the same price.");
        })
        {
            Auditor = call => call.Role == PlanRunnerToolPolicy.TestRulingRole
                ? budget.Answer(call)
                : new StepAgentReply("The check is sound.", 0, false)
        };
    }

    [Fact]
    public async Task A_test_the_step_wrote_that_keeps_failing_is_ruled_on_after_three_rounds_and_a_correction_lets_the_step_pass()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(CorrectedTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Equal(1, budget.Calls);
            Assert.Equal(0, budget.Violations);
            PlanRunEvent ruling = Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("TestWrong", ruling.FailureClass);
            Assert.Contains("failed 3 rounds in a row on tests the step wrote", ruling.Detail);
            Assert.Contains("+ " + "    public void rejects_zero() { Assert.True(true); } // CORRECTED", ruling.Detail);
            Assert.Equal(3, after.Events!.TakeWhile(e => e != ruling).Count(e => e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed));
            Assert.Contains(fake.Calls.Skip(3), call => call.Prompt.Contains("ruled that the failing test this step wrote was wrong", StringComparison.Ordinal));
            Assert.Null(after.FrozenTests);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task The_ruler_is_shown_the_steps_test_and_what_its_model_said()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            string? asked = null;
            var budget = new RulingBudget(1, call =>
            {
                asked = call.Message;
                return new StepAgentReply(TestWrongRuling, 0, false);
            });
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.NotNull(asked);
            Assert.Contains("a test file the model wrote in this step", asked);
            Assert.Contains("FILE: tests/LimitTests.cs", asked);
            Assert.Contains("Assert.Equal(1, Limit.Check(0))", asked);
            Assert.Contains("the provider in my test always returns the same price", asked);
            Assert.Contains("FILE: src/limit.cs", asked);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruling_that_the_code_is_wrong_is_fed_back_and_the_test_is_left_as_it_is()
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

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Equal(1, budget.Calls);
            Assert.Equal(0, budget.Violations);
            Assert.Equal("CodeWrong", Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling).FailureClass);
            Assert.Contains(fake.Calls, call => call.Prompt.Contains("found it right, so the code must change: The limit must refuse zero", StringComparison.Ordinal));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_failing_test_the_step_did_not_create_is_never_ruled_on()
    {
        string project = NewGitProject();
        try
        {
            // The test file was in the project before the step began: a failure in it is a regression, the code's to fix.
            WriteProjectFile(project, LimitTestFile, WrongTest);
            FailFirstTests.RunShell("git add -A && git commit -q -m tests", project, default).GetAwaiter().GetResult();
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            int calls = 0;
            var fake = new FakeStepAgent((_, _, _) =>
            {
                WriteProjectFile(project, "src/limit.cs", $"version {++calls}\n");
                return Task.FromResult("Done.");
            })
            {
                Auditor = call => call.Role == PlanRunnerToolPolicy.TestRulingRole ? budget.Answer(call) : new StepAgentReply("The check is sound.", 0, false)
            };
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(0, budget.Calls);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task With_the_arbiter_off_no_ruler_is_asked()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"),
                new HarnessOptions(EnableArbiter: false)).RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(0, budget.Calls);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.TestRuling);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_compiler_error_next_to_the_failing_test_is_the_codes_and_no_ruler_is_asked()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                command => command == "dotnet test" ? TestsFailOutput + "\nsrc/limit.cs(3,1): error CS1002: ; expected" : BuildPassOutput)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.Equal(0, budget.Calls);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData("Failed Limits.rejects_zero [1 ms]", true)]
    [InlineData("Failed Limits.rejects_zero [1 ms]\nFailed Other.something_else [1 ms]", false)]
    [InlineData("", false)]
    public void Only_a_failure_every_item_of_which_names_a_test_in_the_steps_own_files_counts(string output, bool expected)
    {
        Assert.Equal(expected, PlanRunner.OnlyOwnTestsFail("Exit code: 1\n" + output, [new TestFileText(LimitTestFile, WrongTest)]));
        Assert.False(PlanRunner.OnlyOwnTestsFail("Exit code: 1\n" + output, []));
    }
}
