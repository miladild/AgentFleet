namespace AgentFleet.Tests;

/// <summary>
/// A check whose command is killed at the timeout keeps what it printed first, so the retry can see the failing assertion.
/// </summary>
public sealed partial class PlanRunnerTests
{
    private const string KilledCheckMessage = "Error: command exceeded the 120s timeout and was killed.";

    private const string InfiniteLoopSentence = "A program or test that never ends is usually an infinite loop " +
        "(for example while (true) without a limit) or a wait that never completes. Make it finish.";

    private const string TestRunHint = "The test run did not end. A test that starts a server, a timer or a connection must close it in a finally block " +
        "(or an after hook): when an assertion fails before the close, the open handle keeps the process alive and hides the failure. " +
        "Close everything in finally, then fix the failing assertion shown above.";

    // Runs a plan whose first check is killed at the timeout with the given output, then passes; returns the retry's prompts and the final plan.
    private async Task<(string Retry, PlanRecord After)> RunWithKilledCheck(string? partialOutput)
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = true };
        var agent = Agent("I will make the check finish.");
        Exception timeout = new WorkerCommandTimeoutException("Operation has timed out.",
            new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out."), partialOutput);
        int checks = 0;

        string Verify(string command)
        {
            if (command == "dotnet build" && checks++ == 0) throw timeout;
            return Pass(command);
        }

        await Runner(agent, Verify, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        string retry = string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));
        return (retry, Store.Get(plan.Id)!);
    }

    [Fact]
    public async Task A_killed_check_shows_the_output_it_printed_and_the_test_run_hint()
    {
        const string printed = "✖ create a new alert\n  AssertionError [ERR_ASSERTION]: 404 !== 201\n";

        (string retry, PlanRecord after) = await RunWithKilledCheck(printed);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Contains(KilledCheckMessage, retry, StringComparison.Ordinal);
        Assert.Contains("--- output before it was killed ---\n✖ create a new alert", retry, StringComparison.Ordinal);
        Assert.Contains("AssertionError [ERR_ASSERTION]: 404 !== 201", retry, StringComparison.Ordinal);
        Assert.Contains(TestRunHint, retry, StringComparison.Ordinal);
        Assert.Contains("The check never finished.", retry, StringComparison.Ordinal);
        Assert.DoesNotContain(InfiniteLoopSentence, retry, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("node --import tsx --test test/a.test.ts", "node --import tsx --test --test-force-exit --test-timeout=20000 test/a.test.ts")]
    [InlineData("node --test", "node --test --test-force-exit --test-timeout=20000")]
    [InlineData("node --test --test-name-pattern=x test/a.test.js", "node --test --test-force-exit --test-timeout=20000 --test-name-pattern=x test/a.test.js")]
    [InlineData("node --test --test-force-exit test/a.test.js", null)]
    [InlineData("node --test-name-pattern=x app.js", null)]
    [InlineData("npm test", null)]
    [InlineData("npm run typecheck && node --test test/a.test.js", null)]
    [InlineData("node app.js | node --test", null)]
    [InlineData("dotnet test", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_node_test_command_gets_the_force_exit_flags_and_anything_else_is_left_alone(string? command, string? expected) =>
        Assert.Equal(expected, PlanRunner.ForcedExitCommand(command));

    [Fact]
    public void A_killed_test_run_shows_the_errors_the_forced_exit_rerun_printed()
    {
        string failure = PlanRunner.TimedOutCheckFailure(TimeSpan.FromSeconds(120), "✖ create an alert (4 ms)\n",
            null, "✖ create an alert (4 ms)\n  TypeError: fetch failed\n");

        Assert.Contains("--- the same command run again with the test runner forced to exit, so the errors print ---\n", failure, StringComparison.Ordinal);
        Assert.Contains("TypeError: fetch failed", failure, StringComparison.Ordinal);
        Assert.EndsWith(TestRunHint, failure, StringComparison.Ordinal);
    }

    private const string NodeTestCheck = "node --test test/a.test.js";
    // The first run of a step's own check exits when the tests are done and adds no per-test limit (a slow test must not fail it).
    private const string NodeTestCheckForced = "node --test --test-force-exit test/a.test.js";
    // The run after a timeout also stops a test that never ends.
    private const string NodeTestCheckRerun = "node --test --test-force-exit --test-timeout=20000 test/a.test.js";

    [Fact]
    public async Task A_failing_node_test_check_reports_its_forced_exit_run_and_the_original_command_is_not_run_while_it_fails()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _, verify: NodeTestCheck);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = true };
        var agent = Agent("I will fix the test.");
        var commands = new List<string>();
        int forcedRuns = 0;

        string Verify(string command)
        {
            if (!command.StartsWith("node ", StringComparison.Ordinal)) return Pass(command);
            commands.Add(command);
            if (!command.Contains("--test-force-exit", StringComparison.Ordinal)) return Pass(command);
            return ++forcedRuns == 1
                ? "Exit code: 1\n--- stdout ---\n✖ create an alert (4 ms)\n  AssertionError [ERR_ASSERTION]: 404 !== 201\n"
                : Pass(command);
        }

        await Runner(agent, Verify, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        string retry = string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));
        Assert.Equal(new[] { NodeTestCheckForced, NodeTestCheckForced, NodeTestCheck }, commands);
        Assert.Contains("AssertionError [ERR_ASSERTION]: 404 !== 201", retry, StringComparison.Ordinal);
        Assert.DoesNotContain(KilledCheckMessage, retry, StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_hub_node_test_check_that_fails_is_reported_from_its_forced_exit_run_alone()
    {
        PlanRecord plan = ApprovedPlan(new PlanStepInput("fix the test", "detail", [], NodeTestCheck, null));
        var agent = Agent("I will fix the test.");
        var commands = new List<string>();
        int forcedRuns = 0;

        string Verify(string command)
        {
            if (!command.StartsWith("node ", StringComparison.Ordinal)) return Pass(command);
            commands.Add(command);
            if (!command.Contains("--test-force-exit", StringComparison.Ordinal)) return Pass(command);
            return ++forcedRuns == 1
                ? "Exit code: 1\n--- stdout ---\n✖ create an alert (4 ms)\n  AssertionError [ERR_ASSERTION]: 404 !== 201\n"
                : Pass(command);
        }

        await Runner(agent, Verify).RunPlanAsync(plan.Id, default);

        string retry = string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));
        Assert.Equal(new[] { NodeTestCheckForced, NodeTestCheckForced, NodeTestCheck }, commands);
        Assert.Contains("AssertionError [ERR_ASSERTION]: 404 !== 201", retry, StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_node_test_check_that_passes_when_forced_still_runs_the_original_and_its_timeout_fails_the_round()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _, verify: NodeTestCheck);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = true };
        var agent = Agent("I will fix the test.");
        var commands = new List<string>();
        Exception timeout = new WorkerCommandTimeoutException("Operation has timed out.",
            new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out."), "✖ create an alert (4 ms)\n");
        bool originalHung = false;
        bool rerunPrinted = false;

        string Verify(string command)
        {
            if (!command.StartsWith("node ", StringComparison.Ordinal)) return Pass(command);
            commands.Add(command);
            if (!command.Contains("--test-force-exit", StringComparison.Ordinal))
            {
                if (originalHung) return Pass(command);
                originalHung = true;
                throw timeout;
            }

            if (!originalHung || rerunPrinted) return Pass(command);
            rerunPrinted = true;
            return "Exit code: 1\n--- stdout ---\n✖ create an alert (4 ms)\n  TypeError: fetch failed (ECONNREFUSED)\n";
        }

        await Runner(agent, Verify, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        string retry = string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));
        Assert.Equal(new[] { NodeTestCheckForced, NodeTestCheck, NodeTestCheckRerun, NodeTestCheckForced, NodeTestCheck }, commands);
        Assert.Contains("TypeError: fetch failed (ECONNREFUSED)", retry, StringComparison.Ordinal);
        Assert.Contains(KilledCheckMessage, retry, StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("npm test && npm run lint")]
    public async Task A_check_that_is_not_a_plain_node_test_command_runs_once_without_the_force_exit_flags(string verify)
    {
        PlanRecord plan = InterruptedWorkerPlan(out _, verify: verify);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = true };
        var agent = Agent("I will make the check finish.");
        var commands = new List<string>();

        string Verify(string command)
        {
            if (!command.StartsWith("git ", StringComparison.Ordinal)) commands.Add(command);
            return Pass(command);
        }

        await Runner(agent, Verify, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(new[] { verify }, commands);
    }

    [Fact]
    public async Task A_killed_check_that_is_not_a_node_test_command_is_not_run_again()
    {
        (string retry, PlanRecord after) = await RunWithKilledCheck("✖ create a new alert\n");

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.DoesNotContain("run again with the test runner forced to exit", retry, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public async Task A_killed_check_without_output_keeps_today_s_message(string? partialOutput)
    {
        (string retry, PlanRecord after) = await RunWithKilledCheck(partialOutput);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Contains(KilledCheckMessage + "\n\nThe check never finished. " + InfiniteLoopSentence, retry, StringComparison.Ordinal);
        Assert.DoesNotContain("--- output before it was killed ---", retry, StringComparison.Ordinal);
        Assert.DoesNotContain(TestRunHint, retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_killed_check_whose_output_is_not_a_test_run_gets_no_hint_and_keeps_the_loop_sentence()
    {
        const string printed = "starting the server on 127.0.0.1:8040\nwaiting for the first request\n";

        (string retry, PlanRecord after) = await RunWithKilledCheck(printed);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Contains("--- output before it was killed ---\nstarting the server on 127.0.0.1:8040", retry, StringComparison.Ordinal);
        Assert.Contains(KilledCheckMessage + "\n\n" + "The check never finished. " + InfiniteLoopSentence, retry, StringComparison.Ordinal);
        Assert.DoesNotContain(TestRunHint, retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_killed_check_output_shows_its_newest_lines_not_its_first()
    {
        string printed = string.Join('\n', Enumerable.Range(1, 2000).Select(step => $"step {step} ok")) +
            "\nAssertionError: the newest line\n";

        (string retry, PlanRecord after) = await RunWithKilledCheck(printed);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Contains("AssertionError: the newest line", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("step 1 ok", retry, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("✖ create a new alert\n  AssertionError", true)]
    [InlineData("\u001b[31m✖\u001b[0m create a new alert", true)]
    [InlineData("FAILED tests/test_alerts.py::test_create - AssertionError", true)]
    [InlineData("FAIL src/alerts.test.ts", true)]
    [InlineData("ℹ tests 4", true)]
    [InlineData("  Failed AlertTests.Create [12 ms]", true)]
    [InlineData("Failed to connect to the database", false)]
    [InlineData("fail to bind port 8040", false)]
    [InlineData("starting the server on 127.0.0.1:8040", false)]
    [InlineData("", false)]
    public void A_test_run_is_recognised_by_its_failing_test_or_count_lines(string output, bool expected) =>
        Assert.Equal(expected, PlanFailure.IsTestRunOutput(output));

    [Fact]
    public void The_worker_keeps_the_newest_characters_of_a_killed_command_output_and_both_streams()
    {
        string stdout = "FIRST" + new string('a', 25_000) + "LAST";

        string? kept = WorkerWorkspaceSession.PartialOutputText(stdout, null);

        Assert.NotNull(kept);
        Assert.Equal(20_000, kept.Length);
        Assert.EndsWith("LAST", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST", kept, StringComparison.Ordinal);
        Assert.Equal("out\n--- stderr ---\nerr", WorkerWorkspaceSession.PartialOutputText("out", "err"));
        Assert.Equal("--- stderr ---\nerr", WorkerWorkspaceSession.PartialOutputText(" \n", "err"));
        Assert.Null(WorkerWorkspaceSession.PartialOutputText(null, " "));
        Assert.Null(WorkerWorkspaceSession.PartialOutputText(string.Empty, null));
    }
}
