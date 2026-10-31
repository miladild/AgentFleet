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

    [Fact]
    public async Task A_killed_node_test_check_is_run_again_with_the_force_exit_flags_and_the_retry_sees_its_errors()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _, verify: "node --test test/a.test.js");
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = true };
        var agent = Agent("I will fix the test.");
        var commands = new List<string>();
        Exception timeout = new WorkerCommandTimeoutException("Operation has timed out.",
            new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out."), "✖ create an alert (4 ms)\n");

        string Verify(string command)
        {
            commands.Add(command);
            if (command == "node --test test/a.test.js" && commands.Count == 1) throw timeout;
            if (command.Contains("--test-force-exit", StringComparison.Ordinal))
                return "Exit code: 1\n--- stdout ---\n✖ create an alert (4 ms)\n  TypeError: fetch failed (ECONNREFUSED)\n";
            return Pass(command);
        }

        await Runner(agent, Verify, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        string retry = string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));
        Assert.Contains("node --test --test-force-exit --test-timeout=20000 test/a.test.js", commands);
        Assert.Contains("TypeError: fetch failed (ECONNREFUSED)", retry, StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
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
