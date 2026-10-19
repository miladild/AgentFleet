using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// Runner-level tests of frozen test files: the freeze when a tests step is accepted, the restore before a check, the feedback
// that names a restored file, and the rule that the implementation step must pass the frozen tests.
public sealed partial class PlanRunnerTests
{
    private const string BuildPassOutput = "Exit code: 0\nBuild succeeded.";
    private const string BuildFailOutput = "Exit code: 1\nFAILED limits: zero was accepted";

    private static PlanStepInput ImplementationStep(string verify = "dotnet build") =>
        new("Implement limits", "Reject zero", ["src/limit.cs"], verify, "standard");

    private static PlanStepInput OtherStep() => new("Implement the other part", "Keep the other part working", ["src/other.cs"], "dotnet build", "standard");

    private static void WriteProjectFile(string project, string relative, string content)
    {
        string path = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // The writer writes one of the named files per call, in order, and the last one again after that.
    private static FakeStepAgent WritesFilesInTurn(string project, params string[] files)
    {
        int call = 0;
        return new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile(project, files[Math.Min(call++, files.Length - 1)], "content\n");
            return Task.FromResult("Done.");
        });
    }

    private PlanRunner FrozenRunner(IStepAgent agent, FakeWorkerWorkspaceManager workspaces, FleetOptions options, FleetHealthMonitor health,
        Func<string, string> run, bool enabled = true) =>
        new(Store, new PlanTools(Store, Valid, (command, directory, token) => command.StartsWith("git ", StringComparison.Ordinal)
                ? FailFirstTests.RunShell(command, directory, token) : Task.FromResult(run(command))),
            agent, NullLogger.Instance, fleetOptions: options, healthMonitor: health, workerWorkspaces: workspaces,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, roundsPerRung: 2,
            failFirst: FailFirstMode.Off, testFirst: enabled ? TestFirstMode.On : TestFirstMode.Off);

    [Fact]
    public async Task Frozen_implementation_step_is_accepted_when_the_frozen_tests_pass_and_the_tests_are_frozen()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(), ImplementationStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "src/limit.cs");
            int testRuns = 0;
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command => command == "dotnet test" ? (++testRuns == 1 ? TestsFailOutput : TestsPassOutput) : BuildPassOutput)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(2, testRuns);
            PlanFrozenTestSet set = Assert.Single(after.FrozenTests!);
            Assert.Equal(1, set.StepId);
            Assert.Equal("dotnet test", set.Command);
            Assert.Equal("tests/LimitTests.cs", Assert.Single(set.Files).Path);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.FrozenTests && e.Detail.Contains("tests/LimitTests.cs"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(TestsFailOutput, "the fixed tests still fail")]
    [InlineData("Exit code: 0\nNo test is available in project", "did not run")]
    public async Task Frozen_implementation_step_is_refused_when_the_frozen_tests_fail_or_do_not_run(string frozenOutput, string reason)
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(), ImplementationStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "src/limit.cs");
            int testRuns = 0;
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command => command == "dotnet test" ? (++testRuns == 1 ? TestsFailOutput : frozenOutput) : BuildPassOutput)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.NotEqual(StepStatus.Done, after.Steps[1].Status);
            Assert.Contains(fake.Calls, call => call.Prompt.Contains(reason));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Frozen_tests_are_run_only_for_the_implementation_step()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(), ImplementationStep(), OtherStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "src/limit.cs", "src/other.cs");
            int testRuns = 0;
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command => command == "dotnet test" ? (++testRuns == 1 ? TestsFailOutput : TestsPassOutput) : BuildPassOutput)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(2, testRuns);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Frozen_files_are_restored_before_the_check_and_the_next_round_names_them()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(), ImplementationStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var restores = new List<string>();
            workspaces.FrozenRestore = () =>
            {
                restores.Add("restore");
                return restores.Count == 1 ? ["tests/LimitTests.cs"] : [];
            };
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "src/limit.cs", "src/limit.cs");
            int buildRuns = 0;
            int testRuns = 0;
            int restoresBeforeBuild = -1;
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command =>
                {
                    if (command == "dotnet test") return ++testRuns == 1 ? TestsFailOutput : TestsPassOutput;
                    if (++buildRuns == 1)
                    {
                        restoresBeforeBuild = restores.Count;
                        return BuildFailOutput;
                    }

                    return BuildPassOutput;
                }).RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.True(restoresBeforeBuild >= 1, "the frozen files must be restored before the check runs");
            Assert.Contains("These fixed test files were changed and have been put back; change the code, not the tests: tests/LimitTests.cs",
                fake.Calls[2].Prompt);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.FrozenTests && e.Detail.Contains("tests/LimitTests.cs"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task With_the_setting_off_no_frozen_set_is_made_and_no_frozen_test_is_run_or_restored()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(), ImplementationStep());
            Store.AddFrozenTests(setup.Plan.Id, new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile("tests/LimitTests.cs", "00")]));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            int restores = 0;
            workspaces.FrozenRestore = () => { restores++; return []; };
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "src/limit.cs");
            int testRuns = 0;
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command => command == "dotnet test" ? (++testRuns == 1 ? TestsPassOutput : TestsFailOutput) : BuildPassOutput,
                enabled: false).RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(1, testRuns);
            Assert.Equal(0, restores);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.FrozenTests);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    private static PlanStepInput OtherTestsStep(string verify = "dotnet test --other") =>
        new("Write the other tests", "Cover the other part", ["tests/OtherTests.cs"], verify, "standard", Kind: "tests");

    [Fact]
    public async Task Every_frozen_set_must_pass_when_two_tests_steps_name_the_same_implementation_step_and_one_fails()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(), OtherTestsStep(), ImplementationStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "tests/OtherTests.cs", "src/limit.cs");
            int limitRuns = 0;
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command => command switch
                {
                    "dotnet test" => ++limitRuns == 1 ? TestsFailOutput : TestsPassOutput,
                    "dotnet test --other" => TestsFailOutput,
                    _ => BuildPassOutput,
                }).RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.NotEqual(StepStatus.Done, after.Steps[2].Status);
            Assert.Contains(fake.Calls, call => call.Prompt.Contains("the fixed tests still fail. Make `dotnet test --other` pass"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Every_frozen_set_is_run_and_the_implementation_step_is_accepted_when_all_of_them_pass()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(), OtherTestsStep(), ImplementationStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "tests/OtherTests.cs", "src/limit.cs");
            int limitRuns = 0, otherRuns = 0;
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command => command switch
                {
                    "dotnet test" => ++limitRuns == 1 ? TestsFailOutput : TestsPassOutput,
                    "dotnet test --other" => ++otherRuns == 1 ? TestsFailOutput : TestsPassOutput,
                    _ => BuildPassOutput,
                }).RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(2, limitRuns);
            Assert.Equal(2, otherRuns);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Two_frozen_sets_with_the_same_command_run_that_command_once()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(), OtherTestsStep("dotnet test"), ImplementationStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "tests/OtherTests.cs", "src/limit.cs");
            int testRuns = 0;
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command => command == "dotnet test" ? (++testRuns <= 2 ? TestsFailOutput : TestsPassOutput) : BuildPassOutput)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(2, after.FrozenTests!.Count);
            Assert.Equal(3, testRuns);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_step_that_names_only_a_test_file_is_not_held_to_the_frozen_tests_but_a_code_step_after_it_is()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep(),
                new PlanStepInput("Add the test helpers", "Shared helpers for the tests", ["tests/Helpers.cs"], "dotnet build", "standard"),
                new PlanStepInput("Implement limits", "Reject zero", ["src/limits.cs"], "dotnet build", "standard"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesFilesInTurn(project, "tests/LimitTests.cs", "tests/Helpers.cs", "src/limits.cs");
            await FrozenRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                command => command == "dotnet test" ? TestsFailOutput : BuildPassOutput).RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(StepStatus.Done, after.Steps[1].Status);
            Assert.NotEqual(StepStatus.Done, after.Steps[2].Status);
            Assert.Contains(fake.Calls, call => call.Prompt.Contains("the fixed tests still fail"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
