namespace AgentFleet.Tests;

// The corrected-files reply of a ruler, read when the strict form gives no file: FILE lines and their fenced blocks may have prose
// between them, a FILE line may be wrapped in bold or backticks, and a lone fenced block answers for the one test file under
// dispute. A fence that never closes is not a file. The guard still decides what may be written; these tests cover the reading
// and the runner path that uses it.
public sealed class RulerLenientFilesReadingTests
{
    private const string ClassBlock = "```csharp\npublic class A { }\n```";

    [Fact]
    public void The_strict_form_still_parses_and_a_disputed_path_does_not_override_the_path_it_names()
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles("FILE: tests/A.cs\n```csharp\npublic class A { }\n```", "tests/Other.cs");
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(new[] { "tests/A.cs" }, verdict.Files.Select(file => file.Path).ToArray());
        Assert.Equal("public class A { }\n", verdict.Files[0].Text);
    }

    [Theory]
    [InlineData("Here is the corrected test.\nFILE: tests/A.cs\n```csharp\npublic class A { }\n```", new[] { "tests/A.cs" })]
    [InlineData("FILE: tests/A.cs\n```csharp\npublic class A { }\n```\nThe rest of the file is unchanged.", new[] { "tests/A.cs" })]
    [InlineData("Here is the fix.\n\nFILE: tests/A.cs\n\n```csharp\npublic class A { }\n```\n\nDone.", new[] { "tests/A.cs" })]
    [InlineData("FILE: tests/A.cs\n```csharp\npublic class A { }\n```\nAnd the second file:\nFILE: tests/B.cs\n```\nb\n```\nThat is all.",
        new[] { "tests/A.cs", "tests/B.cs" })]
    public void Prose_before_after_or_between_a_file_line_and_its_fence_is_ignored(string reply, string[] paths)
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles(reply);
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(paths, verdict.Files.Select(file => file.Path).ToArray());
    }

    [Theory]
    [InlineData("**FILE: tests/A.cs**\n" + ClassBlock)]
    [InlineData("`FILE: tests/A.cs`\n" + ClassBlock)]
    [InlineData("- FILE: tests/A.cs\n" + ClassBlock)]
    [InlineData("- **FILE: `tests/A.cs`**\n" + ClassBlock)]
    [InlineData("FILE: `./tests/A.cs`\n" + ClassBlock)]
    [InlineData("FILE: tests\\A.cs\n" + ClassBlock)]
    public void A_file_line_wrapped_in_bold_backticks_or_a_bullet_is_read_as_its_path(string reply)
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles(reply);
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(new[] { "tests/A.cs" }, verdict.Files.Select(file => file.Path).ToArray());
        Assert.Equal("public class A { }\n", verdict.Files[0].Text);
    }

    [Fact]
    public void Prose_between_two_file_pairs_leaves_both_files()
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles("FILE: tests/A.cs\n```\na\n```\nand the second one:\nFILE: tests/B.cs\n```\nb\n```");
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(new[] { "tests/A.cs", "tests/B.cs" }, verdict.Files.Select(file => file.Path).ToArray());
        Assert.Equal("a\n", verdict.Files[0].Text);
        Assert.Equal("b\n", verdict.Files[1].Text);
    }

    [Theory]
    [InlineData("FILE: tests/A.cs\n```\na\n```\nand again\nFILE: tests/A.cs\n```\nb\n```")]
    [InlineData("FILE: `tests/A.cs`\n```\na\n```\nand again\nFILE: ./tests/A.cs\n```\nb\n```")]
    public void The_same_path_named_twice_makes_the_reply_unclear(string reply)
    {
        Assert.Equal(TestRulingKind.NoVerdict, TestRuling.ParseFiles(reply, "tests/A.cs").Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tests/A.cs")]
    public void A_fence_that_never_closes_is_not_a_file(string? onlyPath)
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles("Here is the file.\nFILE: tests/A.cs\n```csharp\npublic class A {", onlyPath);
        Assert.Equal(TestRulingKind.NoVerdict, verdict.Kind);
    }

    [Fact]
    public void A_truncated_second_pair_is_not_used_and_the_closed_first_pair_is()
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles("FILE: tests/A.cs\n```\na\n```\nand\nFILE: tests/B.cs\n```\nb");
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(new[] { "tests/A.cs" }, verdict.Files.Select(file => file.Path).ToArray());
    }

    [Fact]
    public void A_lone_fenced_block_is_the_text_of_the_one_disputed_path()
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles("Here is the corrected test.\n" + ClassBlock, "tests/A.cs");
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(new[] { "tests/A.cs" }, verdict.Files.Select(file => file.Path).ToArray());
        Assert.Equal("public class A { }\n", verdict.Files[0].Text);
    }

    [Fact]
    public void A_lone_fenced_block_after_a_file_line_and_a_sentence_is_the_text_of_the_one_disputed_path()
    {
        TestRulingVerdict verdict = TestRuling.ParseFiles("FILE: tests/A.cs\nHere is the file:\n" + ClassBlock, "tests/A.cs");
        Assert.Equal(TestRulingKind.TestWrong, verdict.Kind);
        Assert.Equal(new[] { "tests/A.cs" }, verdict.Files.Select(file => file.Path).ToArray());
        Assert.Equal("public class A { }\n", verdict.Files[0].Text);
    }

    [Fact]
    public void A_lone_fenced_block_with_no_disputed_path_is_no_verdict()
    {
        Assert.Equal(TestRulingKind.NoVerdict, TestRuling.ParseFiles("Here is the corrected test.\n" + ClassBlock).Kind);
    }

    [Fact]
    public void A_reply_that_says_code_wrong_is_no_file_even_with_a_lone_fenced_block()
    {
        Assert.Equal(TestRulingKind.NoVerdict,
            TestRuling.ParseFiles("VERDICT: CODE-WRONG" + (char)10 + "The code must change; for example:" + (char)10 + ClassBlock, "tests/A.cs").Kind);
    }

    [Fact]
    public void Two_fenced_blocks_with_no_file_line_are_no_verdict_even_with_a_disputed_path()
    {
        Assert.Equal(TestRulingKind.NoVerdict, TestRuling.ParseFiles("```\na\n```\nand\n```\nb\n```", "tests/A.cs").Kind);
    }

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData("Just prose, with no fence or file.", 0, 0)]
    [InlineData("VERDICT: TEST-WRONG\nFILE: tests/A.cs\n```csharp\na\n```", 1, 1)]
    [InlineData("Here.\nFILE: tests/A.cs\n```\na\n```\nand\nFILE: tests/B.cs\n```\nb\n```", 2, 2)]
    [InlineData("- **FILE: tests/A.cs**\nprose\n```csharp\npublic class A {", 1, 1)]
    [InlineData("```\na\n```\nand\n```\nb\n```", 2, 0)]
    public void CountShape_counts_the_fenced_blocks_and_the_FILE_lines_a_reply_held(string? reply, int fences, int fileLines)
    {
        (int Fences, int FileLines) shape = TestRuling.CountShape(reply);
        Assert.Equal(fences, shape.Fences);
        Assert.Equal(fileLines, shape.FileLines);
    }
}

// Runner-level tests of the lenient reading of a ruler's corrected files. The ruler's first answer is a test-wrong verdict with no file,
// its second answer is prose with one fenced block, and the single disputed test file takes that block. The guard still applies.
public sealed partial class PlanRunnerTests
{
    [Fact]
    public async Task A_ruler_corrected_files_reply_of_prose_and_one_fence_is_written_to_the_one_test_file_under_dispute_and_the_step_passes()
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
                _ => new StepAgentReply("Here is the complete corrected test file.\n```csharp\n" + CorrectedTest + "```\nThat is the whole file.", 0, false)
            });
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.Equal(3, RulerAsks(fake).Length);
            Assert.Equal(3, budget.Calls);
            Assert.Equal(0, budget.Violations);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(CorrectedTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            PlanRunEvent ruling = Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("TestWrong", ruling.FailureClass);
            Assert.Contains("+ " + "    public void rejects_zero() { Assert.True(true); } // CORRECTED", ruling.Detail, StringComparison.Ordinal);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_lone_fenced_block_that_changes_nothing_is_refused_by_the_guard_and_nothing_is_written()
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
                _ => new StepAgentReply("Here is the file.\n```csharp\n" + WrongTest + "```", 0, false)
            });
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.Equal(3, budget.Calls);
            Assert.Equal(0, budget.Violations);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(WrongTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            PlanRunEvent ruling = Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling);
            Assert.Equal("Refused", ruling.FailureClass);
            Assert.Contains("the correction changes nothing", ruling.Detail, StringComparison.Ordinal);
            Assert.Equal(Sha256Upper(WrongTest), Assert.Single(Assert.Single(after.FrozenTests!).Files).Sha256);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_reply_with_two_fences_and_no_file_line_is_no_verdict_and_the_log_says_how_many_fences_it_held()
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
                _ => new StepAgentReply("Here is the first part.\n```csharp\nx\n```\nAnd the second part.\n```csharp\ny\n```", 0, false)
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
            Assert.Contains("the reply held 2 fenced block(s) and 0 FILE line(s)", ruling.Detail, StringComparison.Ordinal);
            Assert.Contains("Nothing changes", ruling.Detail, StringComparison.Ordinal);
            PlanRunEvent timing = after.Events!.Where(e => e.Kind == PlanRunner.RulerTimingEvent).Last();
            Assert.Contains("and it held no file: the reply held 2 fenced block(s) and 0 FILE line(s).", timing.Detail, StringComparison.Ordinal);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_ruler_lone_fenced_correction_of_the_test_the_step_wrote_itself_is_written_and_the_step_passes()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(3, call => call.Number switch
            {
                1 => new StepAgentReply(AnalysisOnlyRulerReply, 0, false),
                2 => new StepAgentReply(TestWrongVerdictOnlyReply, 0, false),
                _ => new StepAgentReply("Here is the corrected test file.\n```csharp\n" + CorrectedTest + "```", 0, false)
            });
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.Equal(3, budget.Calls);
            Assert.Equal(0, budget.Violations);
            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(CorrectedTest, File.ReadAllText(Path.Combine(project, "tests", "LimitTests.cs")));
            Assert.Equal("TestWrong", Assert.Single(after.Events!, e => e.Kind == RunEventKind.TestRuling).FailureClass);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
