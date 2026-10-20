using System.Security.Cryptography;
using System.Text;

namespace AgentFleet.Tests;

// Runner-level tests of a dispute: the model says a frozen test is wrong, an independent model rules on it, and a correction
// is applied on the record only when the guard accepts it. Setting off, no dispute and an unreachable ruler change nothing.
// Every run has a hard time limit, and every ruler is counted: a loop fails with a timeout or a count, not a hang.
public sealed partial class PlanRunnerTests
{
    private const string LimitTestFile = "tests/LimitTests.cs";
    private const string WrongTest = "public class LimitTests\n{\n    [Fact]\n    public void rejects_zero() { Assert.True(false); }\n}\n";
    private const string CorrectedTest = "public class LimitTests\n{\n    [Fact]\n    public void rejects_zero() { Assert.True(true); } // CORRECTED\n}\n";
    private const string TestWrongRuling = "VERDICT: TEST-WRONG\nFILE: tests/LimitTests.cs\n```csharp\n" + CorrectedTest + "```";
    private const string CodeWrongRuling = "VERDICT: CODE-WRONG\nThe limit must refuse zero before it counts the call.";
    private static readonly TimeSpan DisputeTimeLimit = TimeSpan.FromSeconds(60);

    private static readonly AgentToolCall DisputeCall = new("dispute_test", new Dictionary<string, string?>
    {
        ["tests"] = "rejects_zero",
        ["reason"] = "the test expects zero to be accepted, but the task says zero is refused"
    });

    private static string Sha256Upper(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // The worker's own tool calls are not seen by the runner's conversation sessions, so the ruling and the review go to the
    // auditor side of the fake (a session opened with a role), and everything else is an ordinary step conversation.
    private sealed class RulingAgent(FakeStepAgent inner) : IStepAgent
    {
        public Task<StepAgentReply> RunStepAsync(string prompt, string tier, CancellationToken token) => inner.RunStepAsync(prompt, tier, token);
        public IStepSession OpenSession() => inner.OpenSession();
        public IStepSession OpenSession(string? role) =>
            role is PlanRunnerToolPolicy.TestRulingRole or PlanRunnerToolPolicy.StepReviewRole ? inner.OpenSession(role) : inner.OpenSession();
    }

    // How many times the ruler may be asked, and what it answers. A call beyond the expected number is counted as a violation
    // and answered with CODE-WRONG, so a ruling loop ends at once and the test fails on its count.
    private sealed class RulingBudget(int maxCalls, Func<FakeAuditCall, StepAgentReply> answer)
    {
        public int Calls { get; private set; }

        public int Violations { get; private set; }

        public StepAgentReply Answer(FakeAuditCall call)
        {
            Calls++;
            if (Calls <= maxCalls)
            {
                return answer(call);
            }

            Violations++;
            return new StepAgentReply("VERDICT: CODE-WRONG\nThe ruler was asked more often than the test expects.", 0, false);
        }
    }

    // The tests step writes the test file first (testFile). Then the implementer writes the code each message, and the model
    // disputes the test in the messages that disputeAt names (the message index in the implementer's conversation: 0 is the first).
    // Only the first conversation disputes unless disputeFreshConversations is set, so a climb to a fresh conversation is quiet.
    private static FakeStepAgent DisputingImplementer(string project, Func<int, bool> disputeAt, RulingBudget budget, string testFile = WrongTest,
        bool disputeFreshConversations = false)
    {
        int calls = 0;
        return new FakeStepAgent((_, _, _) =>
        {
            if (calls++ == 0) WriteProjectFile(project, LimitTestFile, testFile);
            else WriteProjectFile(project, "src/limit.cs", $"version {calls}\n");
            return Task.FromResult("Done.");
        })
        {
            CallsOf = call => (call.Session == 1 || disputeFreshConversations && call.Session > 1) && disputeAt(call.Message) ? [DisputeCall] : [],
            Auditor = call => call.Role == PlanRunnerToolPolicy.TestRulingRole
                ? budget.Answer(call)
                : new StepAgentReply("The check is sound.", 0, false)
        };
    }

    private static Func<string, string> CheckReadsTheTestFile(string project, string passMarker) => command =>
        command == "dotnet test"
            ? File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")).Contains(passMarker, StringComparison.Ordinal) ? TestsPassOutput : TestsFailOutput
            : BuildPassOutput;

    // The step's worker, and a second worker of the same tier that rules on a dispute (a ruler is never the machine that wrote the code).
    private (PlanRecord Plan, FleetOptions Options, FleetHealthMonitor Health) DisputePlan(string project, params PlanStepInput[] steps)
    {
        var worker = new FleetNodeConfig("worker-a", "http://worker.example.test/v1", "m", "worker", "standard",
            Workspace: new FleetWorkerWorkspaceConfig("worker.example.test", "worker", "C:/fleet-test/worker-key.pem",
                "SHA256:synthetic-host-key", "/tmp/fleet-test/workspaces", "linux"));
        var reviewer = new FleetNodeConfig("worker-b", "http://reviewer.example.test/v1", "m", "worker", "standard");
        FleetOptions options = OptionsWithNodes(worker, reviewer);
        var health = new FleetHealthMonitor(options, new HealthyNodeFactory());
        PlanRecord plan = NewPlan(steps);
        plan = Store.Update(plan.Id, current => current with { WorkingDirectory = project })!;
        for (int id = 1; id <= steps.Length; id++)
        {
            plan = Store.SelectMachine(plan.Id, id, "worker-a", out string? selectionError)!;
            Assert.Null(selectionError);
        }

        plan = Store.Approve(plan.Id, autoRetries: 0)!;
        return (plan, options, health);
    }

    [Fact]
    public async Task A_disputed_test_that_the_ruler_finds_wrong_is_corrected_on_the_record_and_the_step_passes()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(CorrectedTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.TestDisputed && e.Detail.Contains("rejects_zero"));
            PlanRunEvent ruling = Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("TestWrong", ruling.FailureClass);
            string before = Sha256Upper(WrongTest);
            string now = Sha256Upper(CorrectedTest);
            Assert.Contains(before, ruling.Detail);
            Assert.Contains(now, ruling.Detail);
            Assert.Contains("- " + "    public void rejects_zero() { Assert.True(false); }", ruling.Detail);
            Assert.Contains("+ " + "    public void rejects_zero() { Assert.True(true); } // CORRECTED", ruling.Detail);
            Assert.Equal(now, Assert.Single(Assert.Single(after.FrozenTests!).Files).Sha256);
            Assert.Equal(1, budget.Calls);
            Assert.Equal(0, budget.Violations);
            Assert.Contains("corrected after an independent model ruled that the test was wrong", fake.Calls[2].Prompt);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruling_that_the_code_is_wrong_is_fed_back_once_and_a_second_dispute_gets_no_second_ruling()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(CodeWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => true, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Equal(1, budget.Calls);
            Assert.Equal(0, budget.Violations);
            List<PlanRunEvent> rulings = after.Events!.Where(e => e.Kind == RunEventKind.TestRuling).ToList();
            Assert.Equal(["CodeWrong", "NotDue"], rulings.Select(e => e.FailureClass));
            Assert.Contains("An independent model read the disputed test and found it right: The limit must refuse zero before it counts the call.",
                fake.Calls[2].Prompt);
            Assert.Contains(rulings, e => e.Detail.Contains("not ruled on: already-ruled"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_correction_the_guard_refuses_changes_nothing_and_is_logged()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply("VERDICT: TEST-WRONG\nFILE: src/limit.cs\n```\nthe ruler's own code\n```", 0, false));
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.DoesNotContain("the ruler's own code", File.ReadAllText(Path.Combine(project, "src", "limit.cs")));
            Assert.Equal("Refused", Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling).FailureClass);
            Assert.Equal(Sha256Upper(WrongTest), Assert.Single(Assert.Single(after.FrozenTests!).Files).Sha256);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_that_cannot_be_reached_changes_nothing_and_the_step_is_parked_as_a_suspect_test()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => throw new InvalidOperationException("the ruler is down"));
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Equal(Sha256Upper(WrongTest), Assert.Single(Assert.Single(after.FrozenTests!).Files).Sha256);
            Assert.Equal("Unavailable", Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling).FailureClass);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.StepParked && e.FailureSignature == "test-suspect");
            Assert.Equal(1, budget.Calls);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task With_no_dispute_no_ruler_is_asked_and_the_step_is_parked_as_before()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => false, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(0, budget.Calls);
            Assert.DoesNotContain(after.Events!, e => e.Kind is RunEventKind.TestDisputed or RunEventKind.TestRuling);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.StepParked && e.FailureSignature == "check-kept-failing");
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task With_the_setting_off_a_dispute_is_ignored_and_no_ruler_is_asked()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => true, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"),
                enabled: false).RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(0, budget.Calls);
            Assert.DoesNotContain(after.Events!, e => e.Kind is RunEventKind.TestDisputed or RunEventKind.TestRuling);
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_second_dispute_after_a_correction_gets_no_second_ruling_and_the_step_is_not_a_suspect_test()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => true, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "PASSES"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(CorrectedTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Equal(1, budget.Calls);
            Assert.Equal(0, budget.Violations);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.TestRuling && e.Detail.Contains("not ruled on: already-ruled"));
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.StepParked && e.FailureSignature == "check-kept-failing");
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_dispute_that_names_a_failure_outside_the_frozen_tests_is_not_ruled_on()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                command => command == "dotnet test"
                    ? TestsFailOutput + "\nFailed NewTests.brand_new_case [2 ms]"
                    : BuildPassOutput).RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(0, budget.Calls);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.TestRuling && e.Detail.Contains("not-a-frozen-test"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_correction_is_not_undone_by_rolling_back_to_a_checkpoint_taken_before_it()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, message => message == 0, budget);
            // Round 1 fails one test, round 2 fails two (worse than round 1, so the runner would undo it), round 3 passes.
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, command =>
            {
                if (command != "dotnet test") return BuildPassOutput;
                string source = File.ReadAllText(Path.Combine(project, "src", "limit.cs"));
                if (source.Contains("version 4", StringComparison.Ordinal)) return File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")).Contains("CORRECTED", StringComparison.Ordinal) ? TestsPassOutput : TestsFailOutput;
                return source.Contains("version 3", StringComparison.Ordinal) ? TestsFailOutput + "\nFailed Limits.second_case [1 ms]" : TestsFailOutput;
            }).RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(CorrectedTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.RoundRolledBack);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_with_no_clear_verdict_is_asked_at_most_twice_for_the_step()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(2, _ => new StepAgentReply("I am not sure which one is wrong.", 0, false));
            var fake = DisputingImplementer(project, _ => true, budget, disputeFreshConversations: true);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(2, budget.Calls);
            Assert.Equal(0, budget.Violations);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.TestRuling && e.Detail.Contains("not ruled on: too-many-asks"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task Two_refused_corrections_end_the_asks_for_the_step()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(2, _ => new StepAgentReply("VERDICT: TEST-WRONG\nFILE: src/limit.cs\n```\nnot a test\n```", 0, false));
            var fake = DisputingImplementer(project, _ => true, budget, disputeFreshConversations: true);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(2, budget.Calls);
            Assert.Equal(0, budget.Violations);
            Assert.Equal(2, after.Events!.Count(e => e.Kind == RunEventKind.TestRuling && e.FailureClass == "Refused"));
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.TestRuling && e.Detail.Contains("not ruled on: too-many-asks"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task An_unreachable_ruler_does_not_count_toward_the_asks()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(int.MaxValue, _ => throw new InvalidOperationException("the ruler is down"));
            var fake = DisputingImplementer(project, _ => true, budget, disputeFreshConversations: true);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.True(after.Events!.Count(e => e.Kind == RunEventKind.TestRuling && e.FailureClass == "Unavailable") >= 3);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.TestRuling && e.Detail.Contains("too-many-asks"));
            Assert.True(budget.Calls >= 3);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task The_next_round_is_staged_with_the_plan_after_a_correction_so_its_frozen_hash_is_the_corrected_one()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            var staged = workspaces.StagedWith.Where(entry => entry.Step.Id == 2).ToArray();
            Assert.Equal(2, staged.Length);
            Assert.Equal(Sha256Upper(WrongTest), Assert.Single(Assert.Single(staged[0].Plan.FrozenTests!).Files).Sha256);
            string hubHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(project, "tests", "LimitTests.cs"))));
            Assert.Equal(hubHash, Assert.Single(Assert.Single(staged[1].Plan.FrozenTests!).Files).Sha256);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_correction_on_the_last_round_of_a_rung_gives_the_rung_its_full_rounds_again()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, message => message == 1, budget);
            // The check never passes, so the step keeps climbing once a rung has had its rounds.
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "NEVER"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            PlanRunEvent[] rounds = after.Events!.Where(e => e.StepId == 2 && e.Kind == RunEventKind.RoundClassified).Take(4).ToArray();
            Assert.Equal(4, rounds.Length);
            Assert.Equal([1, 1, 1, 1], rounds.Select(e => e.Rung ?? -1));
            Assert.Equal([1, 2, 1, 2], rounds.Select(e => e.Round ?? -1));
            Assert.Equal(1, budget.Calls);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_correction_written_over_a_byte_order_mark_keeps_the_mark()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, message => message == 0, budget, testFile: "\uFEFF" + WrongTest);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            byte[] bytes = File.ReadAllBytes(Path.Combine(project, "tests", "LimitTests.cs"));
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3));
            Assert.Equal(CorrectedTest, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_correction_keeps_the_line_endings_of_the_file_it_replaces(bool crlf)
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            string testFile = crlf ? WrongTest.Replace("\n", "\r\n", StringComparison.Ordinal) : WrongTest;
            var fake = DisputingImplementer(project, message => message == 0, budget, testFile: testFile);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            string expected = crlf ? CorrectedTest.Replace("\n", "\r\n", StringComparison.Ordinal) : CorrectedTest;
            Assert.Equal(expected, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task dispute_test_is_offered_to_an_ordinary_step_while_frozen_tests_exist()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => false, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.NotEmpty(fake.Sessions[1].DisputeOffers);
            Assert.All(fake.Sessions[1].DisputeOffers, offered => Assert.True(offered));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task dispute_test_is_not_offered_to_a_tests_step_even_with_the_setting_on()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => false, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.NotEmpty(fake.Sessions[0].DisputeOffers);
            Assert.All(fake.Sessions[0].DisputeOffers, offered => Assert.False(offered));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task dispute_test_is_not_offered_to_an_ordinary_step_when_no_frozen_tests_exist()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, ImplementationStep("dotnet build"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = new FakeStepAgent((_, _, _) =>
            {
                WriteProjectFile(project, "src/limit.cs", "version 1\n");
                return Task.FromResult("Done.");
            });
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, _ => BuildPassOutput)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.NotEmpty(fake.Sessions[0].DisputeOffers);
            Assert.All(fake.Sessions[0].DisputeOffers, offered => Assert.False(offered));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task dispute_test_is_not_offered_with_the_setting_off_even_when_frozen_tests_exist()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            Store.AddFrozenTests(setup.Plan.Id, new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile(LimitTestFile, Sha256Upper(WrongTest))]));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => false, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, _ => TestsPassOutput, enabled: false)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.NotEmpty(fake.Sessions[1].DisputeOffers);
            Assert.All(fake.Sessions.SelectMany(session => session.DisputeOffers), offered => Assert.False(offered));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_that_found_the_test_right_does_not_park_the_step_as_a_suspect_test()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(CodeWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => true, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(1, budget.Calls);
            Assert.Equal(0, budget.Violations);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.StepParked && e.FailureSignature == "check-kept-failing");
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.StepParked && e.FailureSignature == "test-suspect");
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_frozen_file_changed_while_the_ruler_was_working_is_not_overwritten_by_the_correction()
    {
        string project = NewGitProject();
        const string changedElsewhere = "public class LimitTests\n{\n    [Fact]\n    public void rejects_zero() { Assert.True(false); } // CHANGED ELSEWHERE\n}\n";
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ =>
            {
                File.WriteAllText(Path.Combine(project, "tests", "LimitTests.cs"), changedElsewhere);
                return new StepAgentReply(TestWrongRuling, 0, false);
            });
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(1, budget.Calls);
            // The correction was not written; the step then runs on the hub, which puts the accepted bytes back before the check.
            Assert.DoesNotContain("CORRECTED", File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")), StringComparison.Ordinal);
            Assert.Equal("Refused", Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling).FailureClass);
            Assert.Equal(Sha256Upper(WrongTest), Assert.Single(Assert.Single(after.FrozenTests!).Files).Sha256);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_dispute_on_a_step_that_was_not_offered_it_is_neither_recorded_nor_ruled_on()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, _ => true, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(0, budget.Calls);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.TestDisputed);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.TestRuling);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
