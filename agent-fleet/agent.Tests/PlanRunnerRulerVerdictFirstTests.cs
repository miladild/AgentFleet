namespace AgentFleet.Tests;

// Runner-level tests of a ruler's verdict-first follow-up. When the first answer has no verdict line, the follow-up asks for the
// verdict alone, with a short cap and a short limit. Only a test-wrong verdict leads to a third ask, for the complete corrected
// files; a code-wrong verdict is fed back as before and gets no third ask.
public sealed partial class PlanRunnerTests
{
    private const string AnalysisOnlyRulerReply = "Let me analyze this carefully. The test expects zero to be refused and the code accepts it.";
    private const string TestWrongVerdictOnlyReply = "VERDICT: TEST-WRONG\nThe test expects zero to be accepted, but the task says zero is refused.";
    private const string CorrectedFilesOnlyReply = "FILE: tests/LimitTests.cs\n```csharp\n" + CorrectedTest + "```";

    private static FakeAuditCall[] RulerAsks(FakeStepAgent fake) =>
        fake.Audits.Where(call => call.Role == PlanRunnerToolPolicy.TestRulingRole).ToArray();

    [Fact]
    public async Task A_ruler_whose_code_wrong_follow_up_asks_for_the_verdict_alone_with_a_400_token_cap_and_no_third_ask()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(2, call => call.Number == 1
                ? new StepAgentReply(AnalysisOnlyRulerReply, 0, false)
                : new StepAgentReply(CodeWrongRuling, 0, false));
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            FakeAuditCall[] asks = RulerAsks(fake);
            Assert.Equal(2, asks.Length);
            Assert.Equal(1500, asks[0].MaxOutputTokens);
            Assert.Equal(400, asks[1].MaxOutputTokens);
            Assert.Contains("no VERDICT line", asks[1].Message, StringComparison.Ordinal);
            Assert.DoesNotContain("COMPLETE corrected", asks[1].Message, StringComparison.Ordinal);
            Assert.Equal(2, budget.Calls);
            Assert.Equal(0, budget.Violations);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Equal("CodeWrong", Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling).FailureClass);
            Assert.Contains(fake.Calls, call => call.Prompt.Contains("found it right: The limit must refuse zero", StringComparison.Ordinal));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_whose_test_wrong_follow_up_asks_for_the_corrected_files_in_a_third_ask_with_a_3000_token_cap_and_writes_them()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(3, call => call.Number switch
            {
                1 => new StepAgentReply(AnalysisOnlyRulerReply, 0, false),
                2 => new StepAgentReply(TestWrongVerdictOnlyReply, 0, false),
                _ => new StepAgentReply(CorrectedFilesOnlyReply, 0, false)
            });
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            FakeAuditCall[] asks = RulerAsks(fake);
            Assert.Equal(3, asks.Length);
            Assert.Equal(1500, asks[0].MaxOutputTokens);
            Assert.Equal(400, asks[1].MaxOutputTokens);
            Assert.Equal(3000, asks[2].MaxOutputTokens);
            Assert.Contains("COMPLETE corrected file", asks[2].Message, StringComparison.Ordinal);
            Assert.Contains("FILE: <project-relative path>", asks[2].Message, StringComparison.Ordinal);
            Assert.Equal(3, budget.Calls);
            Assert.Equal(0, budget.Violations);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(CorrectedTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            PlanRunEvent ruling = Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("TestWrong", ruling.FailureClass);
            Assert.Contains("+ " + "    public void rejects_zero() { Assert.True(true); } // CORRECTED", ruling.Detail, StringComparison.Ordinal);
            PlanRunEvent timing = after.Events!.Where(e => e.Kind == PlanRunner.RulerTimingEvent).Last();
            Assert.Contains("asked for the corrected file(s), it answered after", timing.Detail, StringComparison.Ordinal);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_ruler_that_says_test_wrong_but_sends_no_corrected_file_is_no_verdict_and_nothing_changes(bool thirdAskFails)
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(3, call =>
            {
                if (call.Number == 1) return new StepAgentReply(AnalysisOnlyRulerReply, 0, false);
                if (call.Number == 2) return new StepAgentReply(TestWrongVerdictOnlyReply, 0, false);
                if (thirdAskFails) throw new InvalidOperationException("the ruler is down");
                return new StepAgentReply("I cannot write the corrected file right now.", 0, false);
            });
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.Equal(3, RulerAsks(fake).Length);
            Assert.Equal(0, budget.Violations);
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            PlanRunEvent ruling = Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("NoVerdict", ruling.FailureClass);
            Assert.Contains("The verdict was TEST-WRONG, but no corrected file arrived", ruling.Detail, StringComparison.Ordinal);
            Assert.Contains("Nothing changes", ruling.Detail, StringComparison.Ordinal);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_whose_first_answer_has_a_verdict_gets_no_follow_up_and_no_third_ask()
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

            FakeAuditCall[] asks = RulerAsks(fake);
            Assert.Single(asks);
            Assert.Equal(1500, asks[0].MaxOutputTokens);
            Assert.Equal(1, budget.Calls);
            Assert.Equal(0, budget.Violations);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(CorrectedTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Equal("TestWrong", Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling).FailureClass);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}

// Reading the two replies that come after the first answer: the verdict alone, and the complete corrected files.
public sealed class RulerVerdictReadingTests
{
    [Theory]
    [InlineData("VERDICT: TEST-WRONG\nThe test expects zero to be accepted, but the task says zero is refused.")]
    [InlineData("**VERDICT: TEST-WRONG**")]
    // A reply that began a file and was cut off by its cap is still a verdict, with no files; the files are asked for again.
    [InlineData("VERDICT: TEST-WRONG\nFILE: tests/A.cs\n```\na\n")]
    public void A_test_wrong_verdict_alone_is_read_with_no_files(string reply)
    {
        TestRulingVerdict verdict = TestRuling.ParseVerdictOnly(reply);
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Empty(verdict.Files);
    }

    [Fact]
    public void A_code_wrong_verdict_alone_is_read_with_its_paragraph()
    {
        TestRulingVerdict verdict = TestRuling.ParseVerdictOnly("VERDICT: CODE-WRONG\nThe limit must refuse zero before it counts the call.");
        Assert.Equal(TestRulingKind.CodeWrong, verdict.Kind);
        Assert.Equal("The limit must refuse zero before it counts the call.", verdict.Paragraph);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I think the test is probably wrong.")]
    [InlineData("VERDICT: TEST-WRONG\nVERDICT: CODE-WRONG\nThe code is wrong.")]
    [InlineData("VERDICT: CODE-WRONG\n")]
    public void A_verdict_alone_reply_with_no_clear_verdict_is_no_verdict(string? reply)
    {
        Assert.Equal(TestRulingKind.NoVerdict, TestRuling.ParseVerdictOnly(reply).Kind);
    }

    [Fact]
    public void The_corrected_files_reply_is_read_as_the_files_of_a_test_wrong_verdict()
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles("FILE: tests/A.cs\n```csharp\npublic class A { }\n```\nFILE: tests/B.cs\n```\nb\n```");
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(new[] { "tests/A.cs", "tests/B.cs" }, verdict.Files.Select(file => file.Path).ToArray());
        Assert.Equal("public class A { }\n", verdict.Files[0].Text);
    }

    [Fact]
    public void A_corrected_files_reply_may_repeat_the_test_wrong_line_before_its_files()
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles("VERDICT: TEST-WRONG\nFILE: tests/A.cs\n```\na\n```");
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Single(verdict.Files);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("I cannot write the corrected file right now.")]
    [InlineData("VERDICT: CODE-WRONG\nThe limit must refuse zero.")]
    [InlineData("FILE: tests/A.cs\n```\na\n")]
    public void A_corrected_files_reply_with_no_complete_file_is_no_verdict(string? reply)
    {
        Assert.Equal(TestRulingKind.NoVerdict, TestRuling.ParseFiles(reply).Kind);
    }
}
