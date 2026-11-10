namespace AgentFleet.Tests;

// On Windows, node can crash while it exits because a connection is still closing. A forced-exit run that hits that crash says nothing
// about the tests, so the step's check runs its own command as written.
public sealed class ForcedExitCrashTests : PlanTestBase
{
    private const string Crashed = "Exit code: 1\n--- stderr ---\n" +
        "Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\\win\\async.c, line 94";
    private const string Passed = "Exit code: 0\n--- stdout ---\nok";
    private const string AssertionFailure = "Exit code: 1\n--- stdout ---\nAssertionError [ERR_ASSERTION]: expected 1 to equal 2";

    [Theory]
    [InlineData("Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\\win\\async.c, line 94")]
    [InlineData("Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src/win/async.c, line 94")]
    [InlineData("Assertion failed at src/win/async.c:94")]
    public void Node_exiting_on_windows_while_a_connection_closes_is_recognised_as_the_crash(string output) =>
        Assert.True(PlanRunner.ForcedExitCrashed(output));

    [Fact]
    public void An_ordinary_test_failure_is_not_the_crash() =>
        Assert.False(PlanRunner.ForcedExitCrashed(AssertionFailure));

    [Fact]
    public void No_output_is_not_the_crash() =>
        Assert.False(PlanRunner.ForcedExitCrashed(null));

    private PlanTools Tools(List<string> ran, Func<string, string> output) =>
        new(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (command, _, _) =>
        {
            ran.Add(command);
            return Task.FromResult(output(command));
        });

    [Fact]
    public async Task A_check_whose_forced_exit_crashed_runs_its_own_command_and_returns_that_result()
    {
        var ran = new List<string>();
        PlanTools tools = Tools(ran, command => command.Contains("--test-force-exit", StringComparison.Ordinal) ? Crashed : Passed);

        string result = await tools.RunStepCheckAsync(NewPlan(), "node --test test/a.test.js", CancellationToken.None);

        Assert.Equal(["node --test --test-force-exit test/a.test.js", "node --test test/a.test.js"], ran);
        Assert.Equal(Passed, result);
    }

    [Fact]
    public async Task A_forced_run_that_fails_for_another_reason_is_the_result_and_the_command_is_not_run_again()
    {
        var ran = new List<string>();
        PlanTools tools = Tools(ran, _ => AssertionFailure);

        string result = await tools.RunStepCheckAsync(NewPlan(), "node --test test/a.test.js", CancellationToken.None);

        Assert.Equal(["node --test --test-force-exit test/a.test.js"], ran);
        Assert.Equal(AssertionFailure, result);
    }
}
