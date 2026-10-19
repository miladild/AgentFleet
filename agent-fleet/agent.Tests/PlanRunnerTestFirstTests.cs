using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed partial class PlanRunnerTests
{
    private const string TestsFailOutput = "Exit code: 1\n  Failed Limits.rejects_zero [1 ms]\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1";
    private const string TestsPassOutput = "Exit code: 0\nPassed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1";

    private static PlanStepInput TestsStep(string? group = null) => new("Write limits tests", "Reject zero and negative input",
        ["tests/LimitTests.cs"], "dotnet test", "standard", ParallelGroup: group, Kind: "tests");

    private PlanRunner TestFirstRunner(IStepAgent agent, FakeWorkerWorkspaceManager? workspaces, FleetOptions options, FleetHealthMonitor health,
        Func<string> verify, bool enabled = true, string? writer = null, FleetModeService? mode = null) =>
        new(Store, new PlanTools(Store, Valid, (command, directory, token) => command.StartsWith("git ", StringComparison.Ordinal)
                ? FailFirstTests.RunShell(command, directory, token) : Task.FromResult(verify())),
            agent, NullLogger.Instance, fleetOptions: options, healthMonitor: health, workerWorkspaces: workspaces,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, roundsPerRung: 2,
            failFirst: FailFirstMode.Enforce, testFirst: enabled ? TestFirstMode.On : TestFirstMode.Off,
            testWriterNode: () => writer, modeService: mode);

    private sealed class WriterAgent(FakeStepAgent inner) : IStepAgent
    {
        public List<string?> Roles { get; } = [];
        public Task<StepAgentReply> RunStepAsync(string prompt, string tier, CancellationToken token) => inner.RunStepAsync(prompt, tier, token);
        public IStepSession OpenSession() => OpenSession(null);
        public IStepSession OpenSession(string? role) { Roles.Add(role); return inner.OpenSession(); }
    }

    [Fact]
    public async Task Test_first_Failing_tests_are_accepted_with_writer_role_guidance_and_one_decision()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesTestsOnly(project);
            var agent = new WriterAgent(fake);
            await TestFirstRunner(agent, workspaces, setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal([PlanRunnerToolPolicy.TestWriterRole], agent.Roles);
            Assert.Contains(PlanTestsStepPrompt.Addendum, Assert.Single(fake.Calls).Prompt);
            Assert.Equal("accepted", OutcomeOf(Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestFirst)));
            Assert.DoesNotContain(after.Events!, e => e.Kind is RunEventKind.CheckAudit or RunEventKind.CheckHealed or RunEventKind.FailFirst or RunEventKind.StepReview);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.CheckPassed && e.Detail.Contains("failed on the new tests as required"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(TestsPassOutput, "tests-pass", "these tests already pass")]
    [InlineData("Exit code: 1\nNo test is available in project", "no-test-ran", "no test ran: if the tests cannot import")]
    [InlineData("Exit code: 1\nerror CS1002: ; expected", "build-error", "build or syntax error")]
    [InlineData("Exit code: 1\nunknown output", "unreadable", "no test ran: if the tests cannot import")]
    public async Task Test_first_Rejected_checks_get_feedback_and_a_later_round_uses_the_same_rule(string firstOutput, string outcome, string feedback)
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesTestsOnly(project);
            int checks = 0;
            await TestFirstRunner(new WriterAgent(fake), workspaces, setup.Options, setup.Health,
                () => ++checks <= 2 ? firstOutput : TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal([outcome, outcome, "accepted"], after.Events!.Where(e => e.Kind == RunEventKind.TestFirst).Select(OutcomeOf));
            Assert.Contains(feedback, fake.Calls[1].Prompt);
            Assert.Contains(feedback, fake.Calls[2].Prompt);
            Assert.DoesNotContain(after.Events!, e => e.Kind is RunEventKind.CheckAudit or RunEventKind.CheckHealed);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_An_implementation_edit_in_an_earlier_round_cannot_be_hidden_by_a_tests_only_later_round()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            int calls = 0;
            var fake = new FakeStepAgent((_, _, _) =>
            {
                if (++calls == 1) File.WriteAllText(Path.Combine(project, "src", "limit.cs"), "changed implementation\n");
                Directory.CreateDirectory(Path.Combine(project, "tests"));
                File.WriteAllText(Path.Combine(project, "tests", "LimitTests.cs"), "tests round " + calls);
                return Task.FromResult("Done.");
            });
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.NotEqual(StepStatus.Done, after.Steps[0].Status);
            Assert.True(fake.Calls.Count > 1);
            Assert.All(after.Events!.Where(e => e.Kind == RunEventKind.TestFirst), e => Assert.Equal("non-test-changed", OutcomeOf(e)));
            Assert.Contains("src/limit.cs", fake.Calls[1].Prompt);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_An_attempt_that_changes_no_file_is_never_accepted()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            var fake = new FakeStepAgent((_, _, _) => Task.FromResult("Done."));
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.NotEqual(StepStatus.Done, after.Steps[0].Status);
            Assert.NotEmpty(fake.Calls);
            Assert.All(after.Events!.Where(e => e.Kind == RunEventKind.TestFirst), e => Assert.Equal("no-test-changes", OutcomeOf(e)));
            Assert.Contains("no test file was written or changed in this step", fake.Calls[1].Prompt);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_Off_treats_tests_kind_as_an_ordinary_step()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            var fake = WritesTestsOnly(project);
            var agent = new WriterAgent(fake);
            await TestFirstRunner(agent, new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsPassOutput, enabled: false).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal([null], agent.Roles);
            Assert.DoesNotContain(PlanTestsStepPrompt.Addendum, Assert.Single(fake.Calls).Prompt);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.TestFirst);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_first_Writer_route_is_separate_from_the_worker_and_hub_mode_is_respected(bool conservative)
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            FleetNodeConfig worker = new("worker-a", "http://worker.example.test/v1", "m", "worker", "standard",
                Workspace: new FleetWorkerWorkspaceConfig("worker.example.test", "worker", "C:/fleet-test/key", "SHA256:synthetic-key", "/tmp/fleet-test", "linux"));
            FleetOptions options = OptionsWithNodes(new FleetNodeConfig("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true),
                worker, new FleetNodeConfig("writer", "http://writer.example.test/v1", "m", "writer", "standard"),
                new FleetNodeConfig("heavy", "http://heavy.example.test/v1", "m", "heavy", "heavy"));
            var health = new FleetHealthMonitor(options, new HealthyNodeFactory());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["FLEET_CONFIG_PATH"] = Path.Combine(PlansDirectory, "fleet.config.json") }).Build();
            var mode = new FleetModeService(new FleetConfigStore(configuration));
            var fake = WritesTestsOnly(project);
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            await TestFirstRunner(new WriterAgent(fake), workspaces, options, health, () => TestsFailOutput,
                writer: conservative ? null : "writer", mode: mode).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(conservative ? "heavy" : "writer", Assert.Single(fake.Calls).Machine);
            Assert.Equal("worker-a", Assert.Single(workspaces.Sessions).Machine);
            if (conservative) Assert.Contains(after.Events!, e => e.Kind == RunEventKind.AttemptStarted && e.Detail.Contains("using the heavy tier"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_Unknown_initial_change_baseline_is_never_accepted()
    {
        string project = NewFolderProject();
        try
        {
            var setup = WorkerPlan(project, TestsStep());
            var fake = WritesTestsOnly(project);
            await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
                setup.Options, setup.Health, () => TestsFailOutput).RunPlanAsync(setup.Plan.Id, default);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.NotEqual(StepStatus.Done, after.Steps[0].Status);
            Assert.All(after.Events!.Where(e => e.Kind == RunEventKind.TestFirst), e => Assert.Equal("unreadable", OutcomeOf(e)));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Test_first_Interrupted_tests_step_waits_for_review_instead_of_using_ordinary_recovery_acceptance()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        Store.Update(plan.Id, current => current with { Steps = current.Steps.Select(s => s with { Kind = "tests" }).ToArray() });
        FleetOptions options = OptionsWithNodes(new FleetNodeConfig("worker-a", "http://worker.example.test/v1", "m", "worker", "standard"));
        var fake = Agent();
        await TestFirstRunner(new WriterAgent(fake), new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])),
            options, new FleetHealthMonitor(options, new HealthyNodeFactory()), () => TestsPassOutput).RunPlanAsync(plan.Id, default);
        Assert.NotEqual(StepStatus.Done, Store.Get(plan.Id)!.Steps[0].Status);
        Assert.Empty(fake.Calls);
    }
}
