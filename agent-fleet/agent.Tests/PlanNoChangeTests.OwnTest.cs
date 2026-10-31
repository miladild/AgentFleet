namespace AgentFleet.Tests;

/// <summary>
/// A test the worker wrote in this step is not a rule the code has to meet: when it fails, the retry says so, so the worker corrects
/// a wrong test instead of bending the code (and older tests) to fit it.
/// </summary>
public sealed partial class PlanNoChangeTests
{
    private const string OwnTestSentence = "You wrote test/mine.test.js in this step.";

    private async Task<string> RetryPromptAfterFailingTest(string failingTestFile, string[] stepFiles)
    {
        WriteProjectFile("test/mine.test.js", "test('keeps quotes', () => {\n  assert.equal(quotes.length, 0);\n});");
        WriteProjectFile("src/a.ts", "old");
        PlanRecord plan = Store.Approve(Store.Create("One step", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Listed("write a", "check1", stepFiles)]).Id)!;
        int writes = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("src/a.ts", writes++ == 0 ? "bad" : "good");
            return Task.FromResult("Done.");
        });
        string Verify(string command) =>
            File.ReadAllText(Path.Combine(ProjectDirectory, "src/a.ts")) == "good"
                ? Pass(command)
                : $"Exit code: 1\n--- stdout ---\n✖ keeps quotes (3 ms)\n  AssertionError: 1 !== 0\n      at TestContext.<anonymous> ({failingTestFile}:2:10)";

        await RepairRunner(agent, Verify).RunPlanAsync(plan.Id, default);

        return string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));
    }

    [Fact]
    public async Task A_failing_test_in_a_file_the_step_lists_is_called_the_workers_own_and_possibly_wrong()
    {
        string retry = await RetryPromptAfterFailingTest("test\\mine.test.js", ["src/a.ts", "test/mine.test.js"]);

        Assert.Contains(OwnTestSentence, retry, StringComparison.Ordinal);
        Assert.Contains("the test is the wrong part", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("The inputs and comments in these tests are the rules the code has to meet.", retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_test_in_a_file_the_step_does_not_list_stays_the_rule()
    {
        string retry = await RetryPromptAfterFailingTest("test\\mine.test.js", ["src/a.ts"]);

        Assert.DoesNotContain("You wrote", retry, StringComparison.Ordinal);
        Assert.Contains("The inputs and comments in these tests are the rules the code has to meet.", retry, StringComparison.Ordinal);
    }
}
