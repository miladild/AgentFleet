namespace AgentFleet.Tests;

// A ruling that the code is wrong describes one failure. Measured: the note went on being given to the worker for 13 rounds
// after the check had changed from a failed comparison to a TypeError at an earlier line of the test, and sent the worker on
// editing the code. The note is kept while the check fails the way it did when the ruling was made, and dropped when it does not.
public sealed partial class PlanRunnerTests
{
    private const string ComparisonFailure =
        "Exit code: 1\n  Failed Limits.rejects_zero [1 ms]\nAssertionError [ERR_ASSERTION]: Expected values to be strictly equal:\n\n0 !== 1\n\n    at TestContext.<anonymous> (test/limit.test.ts:48:10)";

    private const string ComparisonFailureOtherNumbers =
        "Exit code: 1\n  Failed Limits.rejects_zero [3 ms]\nAssertionError [ERR_ASSERTION]: Expected values to be strictly equal:\n\n2 !== 1\n\n    at TestContext.<anonymous> (test/limit.test.ts:52:10)";

    private const string TypeErrorFailure =
        "Exit code: 1\n  Failed Limits.rejects_zero [1 ms]\nTypeError: Cannot read properties of undefined (reading 'price')\n    at TestContext.<anonymous> (test/limit.test.ts:43:32)";

    private const string ComparisonLine = "AssertionError [ERR_ASSERTION]: Expected values to be strictly equal: # !== #";

    // A code-wrong ruling as the runner writes it now: the failure line it was made for sits in front of the note.
    private static string CodeWrongDetailFor(string failureLine) =>
        "The check failed 2 rounds in a row on tests the step wrote (tests/LimitTests.cs). The ruler (worker-b) found the test right: Close the server."
        + PlanRunner.RulingFailureMarker + failureLine
        + PlanRunner.RulingNoteMarker
        + "An independent model read the failing test this step wrote and found it right, so the code must change: " + CarriedParagraph;

    private void CheckFailedEvent(string planId, int stepId, string output) =>
        Store.AddEvent(planId, stepId, 1, RunEventKind.CheckFailed, detail: output);

    private void RulingDroppedEvent(string planId, int stepId) =>
        Store.AddEvent(planId, stepId, 1, RunEventKind.TestRuling, detail: "dropped", failureClass: "Dropped");

    [Fact]
    public void A_code_wrong_ruling_is_dropped_once_the_check_fails_differently()
    {
        string? note = CarriedAfter(plan =>
        {
            CheckFailedEvent(plan.Id, 1, ComparisonFailure);
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
            CheckFailedEvent(plan.Id, 1, TypeErrorFailure);
            RetryEvent(plan.Id, 1, automatic: true);
        });

        Assert.Null(note);
    }

    [Fact]
    public void A_code_wrong_ruling_is_kept_while_the_check_fails_the_same_way_with_other_numbers()
    {
        string? note = CarriedAfter(plan =>
        {
            CheckFailedEvent(plan.Id, 1, ComparisonFailure);
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
            CheckFailedEvent(plan.Id, 1, ComparisonFailureOtherNumbers);
            RetryEvent(plan.Id, 1, automatic: true);
        });

        Assert.Equal("An independent model read the failing test this step wrote and found it right, so the code must change: " + CarriedParagraph, note);
    }

    [Fact]
    public void The_failure_line_is_not_part_of_the_note_the_worker_is_given()
    {
        string? note = CarriedAfter(plan => RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine)));

        Assert.NotNull(note);
        Assert.DoesNotContain("Failure when ruled", note, StringComparison.Ordinal);
        Assert.DoesNotContain(ComparisonLine, note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ruling_written_before_failure_lines_were_stored_is_kept_whatever_the_check_prints()
    {
        string? note = CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong");
            CheckFailedEvent(plan.Id, 1, TypeErrorFailure);
        });

        Assert.NotNull(note);
    }

    [Fact]
    public void A_check_that_prints_no_error_line_does_not_drop_the_ruling()
    {
        string? note = CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
            CheckFailedEvent(plan.Id, 1, "Exit code: 1\nThe check timed out after 120 seconds.");
        });

        Assert.NotNull(note);
    }

    [Fact]
    public void A_failed_check_that_came_before_the_ruling_does_not_drop_it()
    {
        string? note = CarriedAfter(plan =>
        {
            CheckFailedEvent(plan.Id, 1, TypeErrorFailure);
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
        });

        Assert.NotNull(note);
    }

    [Fact]
    public void A_failed_check_of_another_step_does_not_drop_the_ruling()
    {
        string? note = CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
            CheckFailedEvent(plan.Id, 2, TypeErrorFailure);
        });

        Assert.NotNull(note);
    }

    [Fact]
    public void A_dropped_ruling_stays_dropped_when_the_first_failure_comes_back()
    {
        string? note = CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
            CheckFailedEvent(plan.Id, 1, TypeErrorFailure);
            RulingDroppedEvent(plan.Id, 1);
            CheckFailedEvent(plan.Id, 1, ComparisonFailure);
        });

        Assert.Null(note);
    }

    [Fact]
    public void A_ruling_made_after_a_drop_is_carried_again()
    {
        string? note = CarriedAfter(plan =>
        {
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
            RulingDroppedEvent(plan.Id, 1);
            RulingEvent(plan.Id, 1, "CodeWrong", CodeWrongDetailFor("TypeError: Cannot read properties of undefined (reading 'price')"));
            CheckFailedEvent(plan.Id, 1, TypeErrorFailure);
        });

        Assert.NotNull(note);
    }

    [Theory]
    [InlineData(ComparisonFailure, ComparisonLine)]
    [InlineData(ComparisonFailureOtherNumbers, ComparisonLine)]
    [InlineData(TypeErrorFailure, "TypeError: Cannot read properties of undefined (reading 'price')")]
    [InlineData("Exit code: 1\n\u001b[31m✖ rejects zero (3ms)\u001b[39m\n\u001b[31mRangeError: Invalid count value: -1\u001b[39m", "RangeError: Invalid count value: -#")]
    [InlineData("Exit code: 1\n✖ rejects TypeError: bad input (3ms)\nTests: 1 failed", null)]
    [InlineData("Exit code: 1\nThe check timed out after 120 seconds.", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_failure_line_names_the_first_error_and_blanks_its_numbers(string? output, string? expected)
    {
        Assert.Equal(expected, RulingFailure.Line(output));
    }

    [Fact]
    public void The_failure_line_is_one_line_and_short()
    {
        string output = "Exit code: 1\nError: " + new string('x', 400) + "\n    at a.js:1:1";

        string? line = RulingFailure.Line(output);

        Assert.NotNull(line);
        Assert.DoesNotContain('\n', line);
        Assert.True(line.Length <= RulingFailure.MaxCharacters, $"{line.Length} characters");
    }

    [Theory]
    [InlineData(null, "anything", true)]
    [InlineData(ComparisonLine, null, true)]
    [InlineData(ComparisonLine, "no error line here", true)]
    [InlineData(ComparisonLine, ComparisonFailureOtherNumbers, true)]
    [InlineData(ComparisonLine, TypeErrorFailure, false)]
    public void A_ruling_still_applies_when_it_has_no_line_or_the_check_has_none_or_they_are_equal(string? stored, string? output, bool applies)
    {
        Assert.Equal(applies, RulingFailure.StillApplies(stored, output));
    }

    // Measured: the ruling came in round 2; the code was changed in round 5 and the check then failed differently, in every
    // round after it, with the note still in front of each message.
    [Fact]
    public async Task A_carried_ruling_is_not_given_to_the_worker_after_the_check_fails_differently()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            Store.AddEvent(setup.Plan.Id, 1, 1, RunEventKind.CheckFailed, detail: ComparisonFailure);
            RulingEvent(setup.Plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
            RetryEvent(setup.Plan.Id, 1, automatic: true);
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply("", 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks switch { 1 => ComparisonFailure, < 5 => TypeErrorFailure, _ => TestsPassOutput },
                    harness: new HarnessOptions(EnableArbiter: false), roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.True(fake.Calls.Count >= 4, $"only {fake.Calls.Count} rounds ran");
            // The first check failed as it did when the ruler ruled, so the second message still carries the note.
            Assert.Contains(CarriedParagraph, fake.Calls[1].Prompt, StringComparison.Ordinal);
            // The second check failed with a TypeError: no message after it carries the note.
            Assert.All(fake.Calls.Skip(2), call => Assert.DoesNotContain(CarriedParagraph, call.Prompt, StringComparison.Ordinal));
            PlanRunEvent dropped = Assert.Single(Store.Get(setup.Plan.Id)!.Events!, e => e.Kind == RunEventKind.TestRuling && e.FailureClass == "Dropped");
            Assert.Contains("TypeError: Cannot read properties of undefined (reading 'price')", dropped.Detail, StringComparison.Ordinal);
            Assert.Contains(ComparisonLine, dropped.Detail, StringComparison.Ordinal);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_carried_ruling_is_given_in_every_round_while_the_check_keeps_failing_the_same_way()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            Store.AddEvent(setup.Plan.Id, 1, 1, RunEventKind.CheckFailed, detail: ComparisonFailure);
            RulingEvent(setup.Plan.Id, 1, "CodeWrong", CodeWrongDetailFor(ComparisonLine));
            RetryEvent(setup.Plan.Id, 1, automatic: true);
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(0, _ => new StepAgentReply("", 0, false));
            var fake = WritesItsOwnTest(project, budget);
            int checks = 0;
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ++checks < 5 ? ComparisonFailureOtherNumbers : TestsPassOutput,
                    harness: new HarnessOptions(EnableArbiter: false), roundsPerRung: 3)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            Assert.True(fake.Calls.Count >= 4, $"only {fake.Calls.Count} rounds ran");
            Assert.All(fake.Calls.Skip(1), call => Assert.Contains(CarriedParagraph, call.Prompt, StringComparison.Ordinal));
            Assert.DoesNotContain(Store.Get(setup.Plan.Id)!.Events!, e => e.Kind == RunEventKind.TestRuling && e.FailureClass == "Dropped");
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_new_code_wrong_ruling_is_logged_with_the_failure_line_it_was_made_for()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, StepWithItsOwnTest());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(CodeWrongRuling, 0, false));
            var fake = WritesItsOwnTest(project, budget);
            await OwnTestRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health,
                    command => command != "dotnet test" ? BuildPassOutput : ComparisonFailure)
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRunEvent ruling = Assert.Single(Store.Get(setup.Plan.Id)!.Events!, e => e.Kind == RunEventKind.TestRuling && e.FailureClass == "CodeWrong");
            Assert.Contains(PlanRunner.RulingFailureMarker + ComparisonLine + PlanRunner.RulingNoteMarker, ruling.Detail, StringComparison.Ordinal);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
