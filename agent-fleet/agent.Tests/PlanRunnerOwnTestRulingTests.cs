using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// Runner-level tests of a ruling on a test the step wrote itself: measured, a worker wrote a wrong test and then changed correct
// code to satisfy it for fifteen rounds. After rounds in a row failing only on such tests, an independent model rules on it.
public sealed partial class PlanRunnerTests
{
    private static PlanStepInput StepWithItsOwnTest() =>
        new("Implement limits", "Reject zero, with a test for it", ["src/limit.cs", LimitTestFile], "dotnet test", "standard");

    private PlanRunner OwnTestRunner(IStepAgent agent, FakeWorkerWorkspaceManager workspaces, FleetOptions options, FleetHealthMonitor health,
        Func<string, string> run, HarnessOptions? harness = null, int roundsPerRung = 2) =>
        new(Store, new PlanTools(Store, Valid, (command, directory, token) => command.StartsWith("git ", StringComparison.Ordinal)
                ? FailFirstTests.RunShell(command, directory, token) : Task.FromResult(run(command))),
            agent, NullLogger.Instance, fleetOptions: options, healthMonitor: health, workerWorkspaces: workspaces,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, roundsPerRung: roundsPerRung,
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
    public async Task A_test_the_step_wrote_that_keeps_failing_is_ruled_on_after_two_rounds_and_a_correction_lets_the_step_pass()
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
            Assert.Contains("failed 2 rounds in a row on tests the step wrote", ruling.Detail);
            Assert.Contains("+ " + "    public void rejects_zero() { Assert.True(true); } // CORRECTED", ruling.Detail);
            Assert.Equal(2, after.Events!.TakeWhile(e => e != ruling).Count(e => e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed));
            Assert.Contains(fake.Calls.Skip(2), call => call.Prompt.Contains("ruled that the failing test this step wrote was wrong", StringComparison.Ordinal));
            Assert.Contains(fake.Calls.Skip(2), call => call.Prompt.Contains("the step's check passes now", StringComparison.Ordinal));
            Assert.Null(after.FrozenTests);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_correction_that_leaves_the_check_failing_the_same_way_is_undone_before_the_next_round()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(2, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "NEVER"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            PlanRunEvent[] rulings = after.Events!.Where(e => e.Kind == RunEventKind.TestRuling).ToArray();
            Assert.NotEmpty(rulings);
            Assert.All(rulings, ruling => Assert.Equal("Undone", ruling.FailureClass));
            Assert.Contains("with its correction the check still fails", rulings[0].Detail);
            Assert.DoesNotContain(fake.Calls, call => call.Prompt.Contains("ruled that the failing test this step wrote was wrong", StringComparison.Ordinal));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_correction_that_makes_another_test_fail_is_undone()
    {
        // Measured: a correction dropped an import that a passing test still used, and nothing caught it.
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            const string otherFails = "Exit code: 1\n  Failed Limits.reads_the_file [1 ms]\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1";
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, command => command != "dotnet test"
                    ? BuildPassOutput
                    : File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")).Contains("CORRECTED", StringComparison.Ordinal) ? otherFails : TestsFailOutput)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            PlanRunEvent ruling = after.Events!.First(e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("Undone", ruling.FailureClass);
            Assert.Contains("tests that did not fail before (Limits.reads_the_file", ruling.Detail);
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
    public async Task The_rulers_file_tools_read_a_project_relative_path_from_the_project_folder()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            string? resolved = null;
            string? read = null;
            var budget = new RulingBudget(1, _ =>
            {
                // The prompt names files relative to the project; a read in the ruler's session must find them there.
                resolved = HubFileSystemTools.Resolve(LimitTestFile);
                read = HubFileSystemTools.ReadFileAsync(LimitTestFile, null, null, default).GetAwaiter().GetResult();
                return new StepAgentReply(TestWrongRuling, 0, false);
            });
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.Equal(Path.GetFullPath(Path.Combine(project, LimitTestFile)), resolved);
            Assert.Equal(WrongTest, read);
            Assert.NotEqual(Path.GetFullPath(Path.Combine(project, LimitTestFile)), HubFileSystemTools.Resolve(LimitTestFile));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public void A_project_folder_scope_roots_relative_paths_and_leaves_absolute_ones_and_ends_with_its_scope()
    {
        string root = Path.Combine(Path.GetTempPath(), "fleet-root-test");
        string absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "a.txt");
        string before = HubFileSystemTools.Resolve("src/a.ts");

        using (HubProjectFolderScope.Push(root))
        {
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "src/a.ts")), HubFileSystemTools.Resolve("src/a.ts"));
            Assert.Equal(absolute, HubFileSystemTools.Resolve(absolute));
        }

        Assert.Equal(before, HubFileSystemTools.Resolve("src/a.ts"));
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
    public async Task A_ruling_that_the_code_is_wrong_stays_in_every_later_round_of_the_step()
    {
        // Measured: the ruler named the exact fix, the next round's answer ended without a tool call, and the seven rounds after it
        // never saw the ruling again.
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(CodeWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks < 6 ? TestsFailOutput : TestsPassOutput, roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.True(fake.Calls.Count >= 6, $"only {fake.Calls.Count} rounds ran");
            Assert.All(fake.Calls.Skip(3), call =>
                Assert.Contains("found it right, so the code must change: The limit must refuse zero", call.Prompt, StringComparison.Ordinal));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_reply_with_no_verdict_is_kept_in_the_log_well_past_the_first_few_hundred_characters()
    {
        // Measured: the log kept the first 300 characters of such a reply, so nobody could tell whether a verdict was in it.
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            string reply = "Let me analyze this carefully. " + new string('a', 2000) + " TAIL-MARKER " + new string('b', 6000);
            var budget = new RulingBudget(4, _ => new StepAgentReply(reply, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks < 4 ? TestsFailOutput : TestsPassOutput, roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRunEvent ruling = Store.Get(setup.Plan.Id)!.Events!.First(e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("NoVerdict", ruling.FailureClass);
            Assert.Contains("TAIL-MARKER", ruling.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('b', 6000), ruling.Detail, StringComparison.Ordinal);
            Assert.True(ruling.Detail.Length <= FleetPlanStore.MaxRulingDetailCharacters + 3);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_reply_with_no_verdict_is_asked_once_more_for_the_verdict_alone_in_the_same_conversation()
    {
        // Measured: a ruler that writes its analysis first ignored "verdict first" in three of three rulings, and each took minutes.
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(2, call => call.Number == 1
                ? new StepAgentReply("Let me analyze this carefully. The test is right and the code refuses nothing.", 0, false)
                : new StepAgentReply(CodeWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks < 4 ? TestsFailOutput : TestsPassOutput, roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRunEvent ruling = Store.Get(setup.Plan.Id)!.Events!.First(e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("CodeWrong", ruling.FailureClass);
            Assert.Equal(2, budget.Calls);
            Assert.Equal(0, budget.Violations);
            Assert.Equal(2, fake.Audits.Count);
            Assert.Equal(fake.Audits[0].Role, fake.Audits[1].Role);
            Assert.Contains("no VERDICT line", fake.Audits[1].Message, StringComparison.Ordinal);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_that_gives_no_verdict_twice_is_logged_with_both_replies_and_asked_no_third_time()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(4, call => new StepAgentReply(call.Number == 1 ? "FIRST-REPLY analysis only." : "SECOND-REPLY analysis only.", 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks < 4 ? TestsFailOutput : TestsPassOutput, roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRunEvent ruling = Store.Get(setup.Plan.Id)!.Events!.First(e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("NoVerdict", ruling.FailureClass);
            Assert.Contains("FIRST-REPLY", ruling.Detail, StringComparison.Ordinal);
            Assert.Contains("SECOND-REPLY", ruling.Detail, StringComparison.Ordinal);
            Assert.Equal(0, budget.Violations);
            Assert.All(fake.Audits.GroupBy(call => call.Role), group => Assert.True(group.Count() <= 4));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_ruler_ask_writes_how_long_it_took_and_how_long_each_reply_was(bool giveVerdict)
    {
        // Measured: rulings took 49 of 131 minutes in a live run, three of seven returned nothing after eight minutes, and the log
        // held neither a duration nor a reply size, so the cause could not be read.
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(4, call => giveVerdict && call.Number == 1
                ? new StepAgentReply(CodeWrongRuling, 0, false)
                : new StepAgentReply("Analysis only, " + new string('x', 400), 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks < 4 ? TestsFailOutput : TestsPassOutput, roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRunEvent timing = Store.Get(setup.Plan.Id)!.Events!.First(e => e.Kind == PlanRunner.RulerTimingEvent);
            Assert.Contains("answered after", timing.Detail, StringComparison.Ordinal);
            Assert.Contains(giveVerdict ? $"{CodeWrongRuling.Length} characters" : "415 characters", timing.Detail, StringComparison.Ordinal);
            if (!giveVerdict)
            {
                Assert.Contains("asked again for the verdict alone", timing.Detail, StringComparison.Ordinal);
            }
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_long_ruler_reply_with_no_verdict_keeps_its_end_in_the_log_as_well_as_its_start()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            string reply = "START-MARKER " + new string('a', 9000) + " END-MARKER";
            var budget = new RulingBudget(4, _ => new StepAgentReply(reply, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks < 4 ? TestsFailOutput : TestsPassOutput, roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRunEvent ruling = Store.Get(setup.Plan.Id)!.Events!.First(e => e.Kind == RunEventKind.TestRuling);
            Assert.Contains("START-MARKER", ruling.Detail, StringComparison.Ordinal);
            Assert.Contains("END-MARKER", ruling.Detail, StringComparison.Ordinal);
            Assert.True(ruling.Detail.Length <= FleetPlanStore.MaxRulingDetailCharacters + 3);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_step_whose_check_failed_twice_is_offered_web_lookup_from_its_next_round(bool enabled)
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesItsOwnTest(project, new RulingBudget(0, _ => new StepAgentReply("", 0, false)));
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : TestsFailOutput,
                    harness: new HarnessOptions(EnableArbiter: false, EnableWebLookup: enabled), roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.True(fake.Calls.Count >= 3, $"only {fake.Calls.Count} rounds ran");
            Assert.All(fake.Calls.Take(2), call => Assert.DoesNotContain(PlanRunner.WebLookupNote, call.Prompt));
            if (enabled)
                Assert.All(fake.Calls.Skip(2), call => Assert.Contains(PlanRunner.WebLookupNote, call.Prompt));
            else
                Assert.All(fake.Calls, call => Assert.DoesNotContain(PlanRunner.WebLookupNote, call.Prompt));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_first_offer_of_web_lookup_in_an_attempt_is_written_to_the_run_log_once(bool enabled)
    {
        // Measured: a stuck step's model never used the web tools, and the log could not say whether they were ever offered.
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var fake = WritesItsOwnTest(project, new RulingBudget(0, _ => new StepAgentReply("", 0, false)));
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : TestsFailOutput,
                    harness: new HarnessOptions(EnableArbiter: false, EnableWebLookup: enabled), roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            IReadOnlyList<PlanRunEvent> events = Store.Get(setup.Plan.Id)!.Events!;
            PlanRunEvent[] offered = events.Where(e => e.Kind == PlanRunner.WebLookupOfferedEvent).ToArray();
            if (!enabled)
            {
                Assert.Empty(offered);
                return;
            }

            Assert.NotEmpty(offered);
            Assert.Equal(offered.Length, offered.Select(e => e.Attempt).Distinct().Count());
            Assert.True(events.TakeWhile(e => e != offered[0]).Count(e => e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed) >= 2);
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
