namespace AgentFleet.Tests;

// A worker's node test command that pipes its output, or is chained with another command, is still a test run. Its first command gets
// the per-test limit and the force-exit flag, so the run ends when its tests are done instead of being waited out at the kill. The rest of
// the command text is left as the worker wrote it.
public sealed class WorkerPipedTestCommandTests
{
    [Theory]
    [InlineData("node --test test/x.test.ts 2>&1 | Select-Object -Last 40",
        "node --test --test-force-exit --test-timeout=30000 test/x.test.ts 2>&1 | Select-Object -Last 40")]
    [InlineData("node --test test/a.test.js | tail -20", "node --test --test-force-exit --test-timeout=30000 test/a.test.js | tail -20")]
    [InlineData("node --test test/a.test.js && echo done", "node --test --test-force-exit --test-timeout=30000 test/a.test.js && echo done")]
    [InlineData("node --test test/a.test.js; echo done", "node --test --test-force-exit --test-timeout=30000 test/a.test.js; echo done")]
    [InlineData("node --import tsx --test test/a.test.ts | Select-Object -Last 40",
        "node --import tsx --test --test-force-exit --test-timeout=30000 test/a.test.ts | Select-Object -Last 40")]
    [InlineData("node --test --test-force-exit test/a.test.js | tail -20", "node --test --test-timeout=30000 --test-force-exit test/a.test.js | tail -20")]
    public void A_piped_or_chained_node_test_command_gets_the_flags_after_its_first_test_flag(string command, string expected) =>
        Assert.Equal(expected, PlanRunner.WorkerTestCommand(command));

    [Theory]
    [InlineData("npm run typecheck && node --test test/a.test.js")]
    [InlineData("cd app && node --test test/a.test.js")]
    [InlineData("echo start | node --test test/a.test.js")]
    [InlineData("node app.js | node --test")]
    [InlineData("npm test | tail -20")]
    [InlineData("dotnet test; node --test test/a.test.js")]
    public void A_chain_whose_first_command_is_not_a_node_test_command_is_left_alone(string command) =>
        Assert.Null(PlanRunner.WorkerTestCommand(command));

    [Theory]
    [InlineData("node --test --test-timeout=5000 test/a.test.js | tail -20")]
    [InlineData("node --test --test-force-exit --test-timeout=5000 test/a.test.js && echo done")]
    [InlineData("node --test --test-force-exit --test-timeout=30000 test/a.test.js | tail -20")]
    public void A_piped_or_chained_command_that_already_has_its_flags_is_left_alone(string command) =>
        Assert.Null(PlanRunner.WorkerTestCommand(command));

    [Fact]
    public void A_piped_command_that_already_has_the_force_exit_flag_gets_only_the_per_test_limit() =>
        Assert.Equal("node --test --test-timeout=30000 --test-force-exit test/a.test.js | tail -20",
            PlanRunner.WorkerTestCommand("node --test --test-force-exit test/a.test.js | tail -20"));

    [Fact]
    public void A_plain_node_test_command_gets_the_same_flags_as_before() =>
        Assert.Equal("node --test --test-force-exit --test-timeout=30000 test/a.test.js", PlanRunner.WorkerTestCommand("node --test test/a.test.js"));

    [Theory]
    [InlineData("node --test test/a.test.js | tail -20")]
    [InlineData("node --test test/a.test.js && echo done")]
    [InlineData("node --test test/a.test.js; echo done")]
    public void The_step_check_rerun_is_still_never_given_a_piped_or_chained_command(string command)
    {
        Assert.Null(PlanRunner.ForcedExitCommand(command));
        Assert.Null(PlanRunner.ForcedExitCommand(command, PlanRunner.FirstRunForcedExitFlags));
    }
}
