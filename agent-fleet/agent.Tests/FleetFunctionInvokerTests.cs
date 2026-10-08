using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// Characterisation tests for FleetFunctionInvoker: wiring of policy checks, gate blocks, loop guard markers,
/// termination conditions, and journal recording.
/// </summary>
public sealed class FleetFunctionInvokerTests : ContextTestBase
{
    private readonly string _plansDir = Path.Combine(Path.GetTempPath(), "fleet-invoker-plans-" + Guid.NewGuid().ToString("N"));

    private FleetPlanModeService PlanMode(bool on)
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, $$"""
            { "triageModel": "t", "mode": "conservative", "planModeEnabled": {{(on ? "true" : "false")}},
              "nodes": [{ "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "m", "purpose": "", "fallback": true }], "tools": {} }
            """);
        var configStore = new FleetConfigStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = configPath }).Build());
        return new FleetPlanModeService(configStore, RequestContext);
    }

    private FleetFunctionInvoker NewInvoker(bool planMode = false, int rounds = 25)
    {
        Directory.CreateDirectory(_plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = _plansDir }).Build());
        var validator = new FakeValidator(code => new DiagramCheck(true, true, code, null));
        Func<string, string?, CancellationToken, Task<string>> checkCommand = (command, _, _) =>
            Task.FromResult("Exit code: 0\n--- stdout ---\nok");
        var tools = new PlanTools(plans, validator, checkCommand);
        return new FleetFunctionInvoker(Journal, PlanMode(planMode), plans, new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), tools, NullLogger.Instance, rounds);
    }

    private sealed class ScriptedModel(Func<int, FunctionCallContent?> call) : IChatClient
    {
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests++;
            FunctionCallContent? next = call(Requests - 1);
            ChatMessage reply = next is null
                ? new ChatMessage(ChatRole.Assistant, "done")
                : new ChatMessage(ChatRole.Assistant, [next]);
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private async Task<string[]> RunAsync(FleetFunctionInvoker invoker, ScriptedModel model, AITool tool, bool runner, string? role = null)
    {
        IChatClient client = new ChatClientBuilder(model).UseFunctionInvocation(null, o =>
        {
            o.IncludeDetailedErrors = true;
            o.MaximumIterationsPerRequest = 200;
            o.MaximumConsecutiveErrorsPerRequest = 6;
            o.FunctionInvoker = invoker.InvokeAsync;
        }).Build();

        var options = new ChatOptions { Tools = [tool] };
        if (runner)
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = "standard" };
            if (role is not null)
                options.AdditionalProperties[FleetRoutingChatClient.RunnerRoleKey] = role;
        }

        using IDisposable scope = Scope(NewContextId());
        ChatResponse response = await client.GetResponseAsync("go", options);
        return response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(c => c.Result?.ToString() ?? "").ToArray();
    }

    // Behaviour 6: Runner terminates after the configured number of tool rounds.
    [Fact]
    public async Task A_runner_turn_ends_after_the_configured_number_of_tool_rounds()
    {
        var invoker = NewInvoker(rounds: 5);
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path) => { runs++; return "ok " + path; }, "list_directory", "Lists.");
        var model = new ScriptedModel(round => new FunctionCallContent("c" + round, "list_directory", new Dictionary<string, object?> { ["path"] = "dir" + round }));

        string[] results = await RunAsync(invoker, model, tool, runner: true);

        Assert.Equal(5, runs);
        Assert.Equal(5, model.Requests);
    }

    // Behaviour 6 variant: Non-runner turns are not cut by the round limit.
    [Fact]
    public async Task A_non_runner_turn_is_not_cut_by_the_round_limit()
    {
        var invoker = NewInvoker(rounds: 5);
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path) => { runs++; return "ok " + path; }, "list_directory", "Lists.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 9
                ? new FunctionCallContent("c" + round, "list_directory", new Dictionary<string, object?> { ["path"] = "dir" + round })
                : null;
        });

        string[] results = await RunAsync(invoker, model, tool, runner: false);

        Assert.True(runs > 5, "Non-runner should not be cut by round limit (runs=" + runs + ")");
        Assert.True(model.Requests > 5, "Non-runner requests should exceed round limit");
    }

    // Behaviour 1: Runner policy refusal returns the refusal text and ends the turn.
    [Fact]
    public async Task T1_runner_policy_refused_command_returns_refusal()
    {
        var invoker = NewInvoker();
        int runs = 0;
        var tool = AIFunctionFactory.Create((string command) => { runs++; return "ok"; }, "run_command", "Runs a command.");
        // "npm publish" is refused by the policy as it reaches a remote service
        var model = new ScriptedModel(round =>
            new FunctionCallContent("c" + round, "run_command", new Dictionary<string, object?> { ["command"] = "npm publish" }));

        string contextId = NewContextId();
        using (Scope(contextId))
        {
            string[] results = await RunAsync(invoker, model, tool, runner: true);

            Assert.Equal(0, runs); // Tool never ran
            Assert.Equal(1, model.Requests); // Turn ended after first request
            Assert.NotEmpty(results);

            // Verify the result matches the policy's refusal message exactly
            string expected = PlanRunnerToolPolicy.Refusal("run_command", "npm publish")!;
            Assert.Equal(expected, results[0]);
        }
    }

    // Behaviour 1 control: The same command runs when policy allows it.
    [Fact]
    public async Task T1b_an_allowed_command_runs()
    {
        var invoker = NewInvoker();
        int runs = 0;
        var tool = AIFunctionFactory.Create((string command) => { runs++; return "output"; }, "run_command", "Runs a command.");
        // "npm test" is allowed by the policy
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, "run_command", new Dictionary<string, object?> { ["command"] = "npm test" }) : null;
        });

        string[] results = await RunAsync(invoker, model, tool, runner: true);

        Assert.Equal(1, runs); // Tool ran
        Assert.NotEmpty(results);
        Assert.Equal("output", results[0]);
    }

    // Behaviour 2: Non-runner request in planning phase that blocks the tool.
    [Fact]
    public async Task T2_plan_gate_blocks_write_tool_in_planning_phase()
    {
        var invoker = NewInvoker(planMode: true);
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "wrote"; }, "write_file", "Writes a file.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, "write_file", new Dictionary<string, object?> { ["path"] = "f", ["content"] = "x" }) : null;
        });

        string[] results = await RunAsync(invoker, model, tool, runner: false);

        Assert.Equal(0, runs); // Tool never ran (blocked by plan gate)
        Assert.NotEmpty(results);
        Assert.Equal(PlanGate.BlockedMessage("write_file"), results[0]);
    }

    // Behaviour 2 control: The same write_file runs when plan mode is off.
    [Fact]
    public async Task T2b_the_same_write_runs_when_plan_mode_is_off()
    {
        var invoker = NewInvoker(planMode: false);
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "wrote"; }, "write_file", "Writes a file.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, "write_file", new Dictionary<string, object?> { ["path"] = "f", ["content"] = "x" }) : null;
        });

        string[] results = await RunAsync(invoker, model, tool, runner: false);

        Assert.Equal(1, runs); // Tool ran
        Assert.NotEmpty(results);
        Assert.Equal("wrote", results[0]);
    }

    // Behaviour 3: Loop guard marker for runner, not for non-runner.
    [Fact]
    public async Task T3_loop_guard_marker_only_for_runner_on_repeated_failure()
    {
        var invoker = NewInvoker();
        var tool = AIFunctionFactory.Create((string path) => "Error: no such file", "read_file", "Reads a file.");
        int runs = 0;
        var model = new ScriptedModel(round =>
        {
            runs++;
            return new FunctionCallContent("c" + round, "read_file", new Dictionary<string, object?> { ["path"] = "f" });
        });

        // Runner request: should get marker on 3rd failure
        string[] runnerResults = await RunAsync(invoker, model, tool, runner: true);

        Assert.True(runnerResults.Length >= 3, "Expected at least 3 results, got " + runnerResults.Length);
        Assert.Equal(runnerResults.Length, runs); // Verify tool ran for each result
        Assert.DoesNotContain(ToolLoopGuard.Marker, runnerResults[0]);
        Assert.DoesNotContain(ToolLoopGuard.Marker, runnerResults[1]);
        Assert.Contains(ToolLoopGuard.Marker, runnerResults[2]);
        Assert.Equal(5, model.Requests); // Fifth request ends the turn

        // Non-runner request: no marker even on repeated failures
        runs = 0;
        model = new ScriptedModel(round =>
        {
            runs++;
            return round < 10 ? new FunctionCallContent("c" + round, "read_file", new Dictionary<string, object?> { ["path"] = "f" }) : null;
        });

        string[] nonRunnerResults = await RunAsync(invoker, model, tool, runner: false);

        Assert.All(nonRunnerResults, result => Assert.DoesNotContain(ToolLoopGuard.Marker, result));
    }
}
