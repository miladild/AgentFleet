using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class PlanPromptTests : PlanTestBase
{
    private PlanRecord NewApproved(string? verify = "node --test") =>
        Store.Approve(NewPlan(Step("build it", verify: verify)).Id)!;

    [Fact]
    public void A_step_with_a_check_tells_the_model_to_run_that_check_itself_first()
    {
        PlanRecord plan = NewApproved();

        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 1, null);

        Assert.Contains("Run the check yourself", prompt);
    }

    [Fact]
    public void A_step_without_a_check_does_not_ask_for_one()
    {
        PlanRecord plan = NewApproved(verify: null);

        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 1, null);

        Assert.DoesNotContain("Run the check yourself", prompt);
        Assert.Contains("no automatic check", prompt);
    }

    [Fact]
    public void A_retry_says_the_cause_may_lie_in_an_earlier_steps_file()
    {
        PlanRecord plan = NewApproved();

        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 2, "Exit code: 1\nexpected 3 got 2");

        Assert.Contains("expected 3 got 2", prompt);
        Assert.Contains("an earlier step wrote", prompt);
        Assert.DoesNotContain("keeps the process alive", prompt);
    }

    [Theory]
    [InlineData("Error: command exceeded the 120s timeout and was killed.")]
    [InlineData("The attempt ran out of time after 20 minutes.")]
    public void A_retry_after_a_hang_points_at_what_keeps_a_process_alive(string failure)
    {
        PlanRecord plan = NewApproved();

        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 2, failure);

        Assert.Contains("keeps the process alive", prompt);
    }

    // Replies with the given texts in turn and remembers what it was sent.
    private sealed class Replies(params string[] texts) : Microsoft.Extensions.AI.IChatClient
    {
        public readonly List<List<Microsoft.Extensions.AI.ChatMessage>> Sent = [];

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(messages.ToList());
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, texts[Sent.Count - 1])));
        }

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Microsoft.Extensions.AI.ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_step_whose_model_did_nothing_is_asked_once_more_in_the_same_session()
    {
        var idle = new Replies("", "Added the backoff.");
        string summary = await new FleetStepAgent(new Microsoft.Agents.AI.ChatClientAgent(idle)).RunStepAsync("Do step 3.", "standard", default);

        Assert.Equal("Added the backoff.", summary);
        Assert.Equal(2, idle.Sent.Count);
        Assert.Contains("Do step 3.", idle.Sent[1].Select(message => message.Text)); // same session: the step is still there
        Assert.Equal(FleetStepAgent.Nudge, idle.Sent[1][^1].Text);

        var busy = new Replies("Done.");
        Assert.Equal("Done.", await new FleetStepAgent(new Microsoft.Agents.AI.ChatClientAgent(busy)).RunStepAsync("Do step 4.", "light", default));
        Assert.Single(busy.Sent);
    }

    [Fact]
    public void A_steps_commands_run_in_its_project_folder_and_the_prompt_says_so()
    {
        PlanRecord plan = Store.Approve(Store.Create("Board", "Test it", PlansDirectory, [], [], [], null, null, [Step("add a runner")]).Id)!;
        string prompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 1, null);
        var tools = new PlanTools(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (_, _, _) => Task.FromResult("Exit code: 0"));

        Assert.Equal(PlansDirectory, tools.StepWorkingDirectory([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, prompt)]));
        Assert.Null(tools.StepWorkingDirectory([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")]));
        Assert.Contains("run_command runs in the project folder", prompt);
        Assert.Contains("Never install anything globally", prompt);

        // A plan whose folder is gone gives no folder rather than a wrong one.
        PlanRecord elsewhere = Store.Approve(NewPlan(Step("x")).Id)!;
        Assert.Null(tools.StepWorkingDirectory([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, PlanRunner.BuildPrompt(elsewhere, elsewhere.Steps[0], 1, null))]));
    }

    [Fact]
    public void A_long_check_output_keeps_every_failing_test_and_the_totals_ahead_of_its_end()
    {
        string[] holidays = ["Good Friday 2026", "Independence Day 2026", "Juneteenth 2026", "Thanksgiving 2026"];
        string Failure(string name) =>
            $"✖ {name} (0.4ms)\n  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:\n\n  false !== true\n\n" +
            string.Concat(Enumerable.Range(0, 12).Select(frame => $"      at Test.run (node:internal/test_runner/test:{frame}:25)\n")) +
            "  {\n    code: 'ERR_ASSERTION',\n    actual: false,\n    expected: true\n  }\n";
        string output = "Exit code: 1\n--- stdout ---\n✔ market session - pre-market (1ms)\n" +
                        string.Concat(holidays.Select(Failure)) + "ℹ tests 11\nℹ pass 7\nℹ fail 4\n\n✖ failing tests:\n\n" +
                        string.Concat(holidays.Select(Failure));

        string shown = PlanTools.ShortenCheckOutput(output);

        Assert.True(output.Length > 2500);
        Assert.All(holidays, name => Assert.Contains($"✖ {name}", shown[..shown.IndexOf("The end of the output", StringComparison.Ordinal)]));
        Assert.Contains("ℹ fail 4", shown);
        Assert.DoesNotContain("✔ market session", shown[..shown.IndexOf("The end of the output", StringComparison.Ordinal)]);
        Assert.EndsWith(output[^200..], shown);
        Assert.Equal("Exit code: 0\nshort", PlanTools.ShortenCheckOutput("Exit code: 0\nshort"));
    }

    [Fact]
    public void The_planner_is_told_a_check_must_exercise_behavior_and_finish_on_its_own()
    {
        string directive = PlanGate.PlanningDirective(null);

        Assert.Contains("exercises the step's", directive);
        Assert.Contains("must finish on its own", directive);
    }

    [Fact]
    public void Prompts_name_the_machines_own_platform_not_a_fixed_one()
    {
        PlanRecord plan = NewApproved();

        string runnerPrompt = PlanRunner.BuildPrompt(plan, plan.Steps[0], 1, null);
        string planning = PlanGate.PlanningInstructions(null);

        Assert.Contains(HubPlatform.Name, runnerPrompt);
        Assert.Contains(HubPlatform.Name, planning);
    }

    [Fact]
    public async Task The_shell_tool_runs_a_command_with_a_pipe_on_this_platform()
    {
        var options = HubShellOptions.Load(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        string result = await HubShellTools.RunCommandAsync(
            "echo one && echo two", null, options, NullLogger.Instance, default);

        Assert.Contains("Exit code: 0", result);
        Assert.Contains("one", result);
        Assert.Contains("two", result);
    }

    [Fact]
    public async Task The_shell_tool_reads_a_commands_UTF8_output_as_UTF8()
    {
        var options = HubShellOptions.Load(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        string file = Path.Combine(PlansDirectory, "marks.txt");
        File.WriteAllText(file, "✖ Juneteenth 2026\n✔ pre-market\n", new System.Text.UTF8Encoding(false));

        string result = await HubShellTools.RunCommandAsync(
            OperatingSystem.IsWindows() ? $"type \"{file}\"" : $"cat \"{file}\"", null, options, NullLogger.Instance, default);

        Assert.Contains("✖ Juneteenth 2026", result);
        Assert.Contains("✔ pre-market", result);
    }
}
