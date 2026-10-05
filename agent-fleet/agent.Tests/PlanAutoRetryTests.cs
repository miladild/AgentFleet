using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class PlanAutoRetryTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private string ProjectDirectory
    {
        get
        {
            string directory = Path.Combine(PlansDirectory, "project");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private static string Fail(string _) => "Exit code: 1\n--- stdout ---\nerror";

    private PlanTools RepairTools(Func<string, string> verify)
    {
        string project = ProjectDirectory;
        return new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(command switch
        {
            "git rev-parse --show-toplevel" => $"Exit code: 0\n--- stdout ---\n{project}",
            "git rev-parse HEAD" => "Exit code: 0\n--- stdout ---\nabc123",
            _ when command.Contains("--untracked-files=all", StringComparison.Ordinal) =>
                "Exit code: 0\n--- stdout ---\n" + string.Join("\n",
                    Directory.GetFiles(project, "*", SearchOption.AllDirectories)
                        .Select(file => "?? " + Path.GetRelativePath(project, file).Replace('\\', '/'))),
            _ when command.StartsWith("git ", StringComparison.Ordinal) => "Exit code: 0\n--- stdout ---\n",
            _ => verify(command)
        }));
    }

    private PlanRunner RepairRunner(FakeStepAgent agent, Func<string, string> verify,
        int roundsPerRung = 1, IPlanNotifier? notifier = null) =>
        new(Store, RepairTools(verify), agent, NullLogger.Instance, roundsPerRung: roundsPerRung,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, notifier: notifier);

    private PlanRecord OneStepPlan(PlanStepInput step, int autoRetries) =>
        Store.Approve(Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null, [step],
            PlanRecoveryScope.WorkerOnly).Id, recoveryScope: PlanRecoveryScope.WorkerOnly, autoRetries: autoRetries)!;

    private static PlanStepInput StepNamed(string title, string file, string? verify = "check") =>
        new(title, "detail", [file], verify, "standard");

    [Fact]
    public async Task A_step_whose_ladder_is_spent_is_retried_by_the_runner_and_finishes_without_anyone_being_told()
    {
        string filePath = Path.Combine(ProjectDirectory, "data.txt");
        File.WriteAllText(filePath, "initial");

        PlanRecord plan = OneStepPlan(StepNamed("fix", "data.txt"), autoRetries: 1);
        int modelCall = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            modelCall++;
            // First two calls fail (first ladder: rung 1, rung 2)
            // Then budget is used, so automatic retry happens
            // Next two calls should be on fresh ladder (rung 1 again, rung 2 again)
            if (modelCall == 3)
            {
                File.WriteAllText(filePath, "fixed");
                return Task.FromResult("Fixed it.");
            }
            return Task.FromResult("Still working...");
        });

        var notifier = new RecordingNotifier();
        await RepairRunner(agent, _ =>
            Path.GetFileName(filePath) == "data.txt" && File.ReadAllText(filePath) == "fixed" ? Pass("check") : Fail("check"),
            roundsPerRung: 1, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);

        // Should have exactly one StepRetried event with FailureClass "AutoRetry"
        PlanRunEvent[] retried = after.Events!.Where(e => e.Kind == RunEventKind.StepRetried && e.FailureClass == "AutoRetry").ToArray();
        Assert.Single(retried);
        Assert.Contains("retry 1 of 1", retried[0].Detail);

        // Should have no StepParked event
        Assert.Empty(after.Events!.Where(e => e.Kind == RunEventKind.StepParked));

        // Should have plan-done notice, not step-parked
        Assert.Empty(notifier.Notices.Where(n => n.Kind == "step-parked"));
        Assert.NotEmpty(notifier.Notices.Where(n => n.Kind == "plan-done"));
    }

    [Fact]
    public async Task A_step_that_never_passes_is_retried_up_to_the_budget_and_then_parked_with_one_notice()
    {
        string filePath = Path.Combine(ProjectDirectory, "data.txt");
        File.WriteAllText(filePath, "initial");

        PlanRecord plan = OneStepPlan(StepNamed("fix", "data.txt"), autoRetries: 2);
        int modelCall = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            modelCall++;
            // Never actually fix the file
            return Task.FromResult("Working on it...");
        });

        var notifier = new RecordingNotifier();
        await RepairRunner(agent, _ => Fail("check"),
            roundsPerRung: 1, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);

        // Should have exactly two StepRetried events with FailureClass "AutoRetry"
        PlanRunEvent[] retried = after.Events!.Where(e => e.Kind == RunEventKind.StepRetried && e.FailureClass == "AutoRetry").ToArray();
        Assert.Equal(2, retried.Length);
        Assert.Contains("retry 1 of 2", retried[0].Detail);
        Assert.Contains("retry 2 of 2", retried[1].Detail);

        // Should have exactly one StepParked event
        PlanRunEvent[] parked = after.Events!.Where(e => e.Kind == RunEventKind.StepParked).ToArray();
        Assert.Single(parked);
        Assert.Equal("check-kept-failing", parked[0].FailureSignature);

        // Should have exactly one step-parked notice
        PlanNotice[] stepParkedNotices = notifier.Notices.Where(n => n.Kind == "step-parked").ToArray();
        Assert.Single(stepParkedNotices);

        // Model was called 6 times: 3 ladders x 2 calls per ladder (rung 1 + rung 2)
        Assert.Equal(6, modelCall);
    }

    [Fact]
    public async Task With_no_automatic_retries_a_spent_ladder_parks_the_step_at_once()
    {
        string filePath = Path.Combine(ProjectDirectory, "data.txt");
        File.WriteAllText(filePath, "initial");

        PlanRecord plan = OneStepPlan(StepNamed("fix", "data.txt"), autoRetries: 0);
        int modelCall = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            modelCall++;
            return Task.FromResult("Trying...");
        });

        var notifier = new RecordingNotifier();
        await RepairRunner(agent, _ => Fail("check"),
            roundsPerRung: 1, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);

        // Should have no StepRetried events
        Assert.Empty(after.Events!.Where(e => e.Kind == RunEventKind.StepRetried && e.FailureClass == "AutoRetry"));

        // Should have one StepParked event
        Assert.Single(after.Events!.Where(e => e.Kind == RunEventKind.StepParked));

        // Should have one step-parked notice
        Assert.Single(notifier.Notices.Where(n => n.Kind == "step-parked"));

        // Model was called 2 times: 1 ladder x 2 calls (rung 1 + rung 2)
        Assert.Equal(2, modelCall);
    }

    [Fact]
    public async Task A_failure_a_second_pass_cannot_mend_is_not_retried_automatically()
    {
        string filePath = Path.Combine(ProjectDirectory, "data.txt");
        File.WriteAllText(filePath, "initial");

        PlanRecord plan = OneStepPlan(StepNamed("fix", "data.txt"), autoRetries: 2);
        var agent = new FakeStepAgent((_, _, _) => Task.FromResult("Can't help."));

        var notifier = new RecordingNotifier();
        await RepairRunner(agent, _ =>
            "Exit code: 127\n--- stdout ---\nbash: node: command not found",
            roundsPerRung: 1, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);

        // Should have no StepRetried events with FailureClass "AutoRetry"
        Assert.Empty(after.Events!.Where(e => e.Kind == RunEventKind.StepRetried && e.FailureClass == "AutoRetry"));

        // Step should be Parked (not retried automatically)
        Assert.NotEqual(PlanStatus.Done, after.Status);
    }

    [Fact]
    public async Task A_retry_by_the_user_gives_the_step_a_fresh_budget()
    {
        string filePath = Path.Combine(ProjectDirectory, "data.txt");
        File.WriteAllText(filePath, "initial");

        // First run with budget 1
        PlanRecord plan = OneStepPlan(StepNamed("fix", "data.txt"), autoRetries: 1);
        int modelCall = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            modelCall++;
            return Task.FromResult("Trying...");
        });

        var notifier = new RecordingNotifier();
        await RepairRunner(agent, _ => Fail("check"),
            roundsPerRung: 1, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);

        // Count auto-retries from first run
        int firstRunRetries = after.Events!.Count(e => e.Kind == RunEventKind.StepRetried && e.FailureClass == "AutoRetry");
        Assert.Equal(1, firstRunRetries);

        // User retries the step
        plan = Store.RetryStep(plan.Id, 1)!;
        Assert.Equal(StepStatus.Pending, plan.Steps[0].Status);

        // Re-approve and run
        plan = Store.Approve(plan.Id, autoRetries: 1)!;
        modelCall = 0;
        var agent2 = new FakeStepAgent((_, _, _) =>
        {
            modelCall++;
            if (modelCall == 3)
            {
                File.WriteAllText(filePath, "fixed");
                return Task.FromResult("Fixed!");
            }
            return Task.FromResult("Still working...");
        });

        await RepairRunner(agent2, _ =>
            Path.GetFileName(filePath) == "data.txt" && File.ReadAllText(filePath) == "fixed" ? Pass("check") : Fail("check"),
            roundsPerRung: 1, notifier: notifier).RunPlanAsync(plan.Id, default);

        after = Store.Get(plan.Id)!;
        // The budget of 1 was used up in the first run. Only because the user's retry gave the step a fresh one does the
        // second run retry again (a second AutoRetry event, "retry 1 of 1" once more) and finish.
        PlanRunEvent[] allRetries = after.Events!.Where(e => e.Kind == RunEventKind.StepRetried && e.FailureClass == "AutoRetry").ToArray();
        Assert.Equal(2, allRetries.Length);
        Assert.All(allRetries, e => Assert.Contains("retry 1 of 1", e.Detail));
        Assert.Equal(PlanStatus.Done, after.Status);
    }

    [Fact]
    public async Task A_step_that_edits_its_source_but_not_the_test_file_it_names_is_not_accepted()
    {
        string srcPath = Path.Combine(ProjectDirectory, "src.cs");
        string testPath = Path.Combine(ProjectDirectory, "test", "a.test.cs");
        Directory.CreateDirectory(Path.Combine(ProjectDirectory, "test"));
        File.WriteAllText(srcPath, "// source");
        File.WriteAllText(testPath, "// test");

        var step = new PlanStepInput("write code", "detail", ["src.cs", "test/a.test.cs"], "check", "standard");
        PlanRecord plan = OneStepPlan(step, autoRetries: 0);
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(srcPath, "// modified source");
            return Task.FromResult("Modified source.");
        }, toolCalls: 1, editToolCalled: true);

        await RepairRunner(agent, _ => Pass("check"), roundsPerRung: 1).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);

        // Should have StepParked event with FailureSignature "no-change" (test file not changed)
        PlanRunEvent? parked = after.Events?.FirstOrDefault(e => e.Kind == RunEventKind.StepParked);
        Assert.NotNull(parked);
        Assert.Equal("no-change", parked.FailureSignature);

        // Check for the validation failure event that mentions test files
        Assert.Contains(after.Events!, e =>
            (e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed) &&
            e.Detail is not null && e.Detail.Contains("names test files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_check_that_ran_no_test_does_not_get_a_step_accepted_and_the_model_is_told_why()
    {
        string srcPath = Path.Combine(ProjectDirectory, "src.js");
        string testPath = Path.Combine(ProjectDirectory, "test", "a.test.js");
        Directory.CreateDirectory(Path.Combine(ProjectDirectory, "test"));
        File.WriteAllText(srcPath, "// source");
        File.WriteAllText(testPath, "// test");

        var step = new PlanStepInput("write code", "detail", ["src.js", "test/a.test.js"], "node --test test/a.test.js", "standard");
        PlanRecord plan = OneStepPlan(step, autoRetries: 0);
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(srcPath, "// source " + round);
            File.WriteAllText(testPath, "console.log('ERROR: one case is wrong');" + round);
            return Task.FromResult("Wrote a script that prints its results.");
        }, toolCalls: 1, editToolCalled: true);

        // What node prints for a test file that registers no test: the file itself is the one test that passed.
        await RepairRunner(agent, _ => "Exit code: 0\n--- stdout ---\nERROR: one case is wrong\n✔ test\\a.test.js (12.0ms)\nℹ tests 1\nℹ pass 1\n", roundsPerRung: 1)
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
        Assert.Equal("no-tests-ran", after.Events!.First(e => e.Kind == RunEventKind.StepParked).FailureSignature);
        Assert.Contains(after.Events!, e =>
            (e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed) && e.Detail is not null && e.Detail.Contains("ran no test", StringComparison.Ordinal));
        Assert.True(agent.Calls.Count > 1, "the model was asked again with the reason");
        Assert.Contains("ran no test", agent.Calls[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_that_changes_the_test_file_it_names_is_accepted()
    {
        string srcPath = Path.Combine(ProjectDirectory, "src.cs");
        string testPath = Path.Combine(ProjectDirectory, "test", "a.test.cs");
        Directory.CreateDirectory(Path.Combine(ProjectDirectory, "test"));
        File.WriteAllText(srcPath, "// source");
        File.WriteAllText(testPath, "// test");

        var step = new PlanStepInput("write code", "detail", ["src.cs", "test/a.test.cs"], "check", "standard");
        PlanRecord plan = OneStepPlan(step, autoRetries: 0);
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(srcPath, "// modified source");
            File.WriteAllText(testPath, "// modified test");
            return Task.FromResult("Modified both.");
        }, toolCalls: 2, editToolCalled: true);

        await RepairRunner(agent, _ => Pass("check"), roundsPerRung: 1).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);
        Assert.Single(agent.Calls);
    }

    [Theory]
    [InlineData("test/app.test.ts", true)]
    [InlineData("tests/x.py", true)]
    [InlineData("src/__tests__/a.ts", true)]
    [InlineData("foo_spec.rb", true)]
    [InlineData("FooTests.cs", true)]
    [InlineData("FooTest.java", true)]
    [InlineData("test_foo.py", true)]
    [InlineData("a/b.spec.js", true)]
    [InlineData("src/Spec/x.cs", true)]
    [InlineData("src/latest.ts", false)]
    [InlineData("src/server/marketHours.ts", false)]
    [InlineData("contest.ts", false)]
    [InlineData("docs/testing-notes.md", false)]
    [InlineData("src/attest/x.ts", false)]
    public void IsTestPath_identifies_test_files_correctly(string path, bool isTest)
    {
        Assert.Equal(isTest, PlanRunner.IsTestPath(path));
    }
}
