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

    private async Task<string[]> RunAsync(FleetFunctionInvoker invoker, ScriptedModel model, AITool tool, bool runner, string? role = null, string? contextId = null)
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

        using IDisposable scope = Scope(contextId ?? NewContextId());
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
        string[] results = await RunAsync(invoker, model, tool, runner: true, contextId: contextId);

        Assert.Equal(0, runs); // Tool never ran
        Assert.Equal(1, model.Requests); // Turn ended after first request
        Assert.NotEmpty(results);

        // Verify the result matches the policy's refusal message exactly
        string expected = PlanRunnerToolPolicy.Refusal("run_command", "npm publish")!;
        Assert.Equal(expected, results[0]);

        // Verify the journal recorded the refusal as an error
        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(contextId);
        FleetContextEvent result = Assert.Single(events, e => e.Kind == FleetContextEventKind.ToolResult);
        Assert.True(ContextText.Bool(result.Payload, "isError"));
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
        int toolRuns = 0;
        var tool = AIFunctionFactory.Create((string path) => { toolRuns++; return "Error: no such file"; }, "read_file", "Reads a file.");
        var model = new ScriptedModel(round =>
            new FunctionCallContent("c" + round, "read_file", new Dictionary<string, object?> { ["path"] = "f" }));

        // Runner request: should get marker on 3rd failure
        string[] runnerResults = await RunAsync(invoker, model, tool, runner: true);

        Assert.True(runnerResults.Length >= 3, "Expected at least 3 results, got " + runnerResults.Length);
        Assert.Equal(runnerResults.Length, toolRuns); // Verify tool ran for each result
        Assert.DoesNotContain(ToolLoopGuard.Marker, runnerResults[0]);
        Assert.DoesNotContain(ToolLoopGuard.Marker, runnerResults[1]);
        Assert.Contains(ToolLoopGuard.Marker, runnerResults[2]);
        Assert.Equal(5, model.Requests); // Fifth request ends the turn

        // Non-runner request: no marker even on repeated failures
        toolRuns = 0;
        model = new ScriptedModel(round =>
            round < 10 ? new FunctionCallContent("c" + round, "read_file", new Dictionary<string, object?> { ["path"] = "f" }) : null);

        string[] nonRunnerResults = await RunAsync(invoker, model, tool, runner: false);

        Assert.True(nonRunnerResults.Length >= 5, "Expected at least 5 results before checking marker absence");
        Assert.All(nonRunnerResults, result => Assert.DoesNotContain(ToolLoopGuard.Marker, result));
    }

    // Behaviour 4: Non-runner request with plan mode off: propose_plan returning "Plan saved" ends the turn.
    [Fact]
    public async Task T4_non_runner_propose_plan_returning_plan_saved_ends_the_turn()
    {
        var invoker = NewInvoker(planMode: false);
        int runs = 0;
        var tool = AIFunctionFactory.Create((string planName, string content) => { runs++; return "Plan saved. [plan:abc]"; }, "propose_plan", "Proposes a plan.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, "propose_plan", new Dictionary<string, object?> { ["planName"] = "plan1", ["content"] = "steps" }) : null;
        });

        string[] results = await RunAsync(invoker, model, tool, runner: false);

        Assert.Equal(1, runs); // Tool ran
        Assert.Equal(1, model.Requests); // Turn ended after first request
        Assert.NotEmpty(results);
        Assert.Equal("Plan saved. [plan:abc]", results[0]);
    }

    // Behaviour 4 control: The same tool returns "Plan not saved" and model continues asking.
    [Fact]
    public async Task T4b_non_runner_propose_plan_returning_plan_not_saved_continues_the_turn()
    {
        var invoker = NewInvoker(planMode: false);
        int runs = 0;
        var tool = AIFunctionFactory.Create((string planName, string content) => { runs++; return "Plan not saved"; }, "propose_plan", "Proposes a plan.");
        var model = new ScriptedModel(round =>
            round == 0 ? new FunctionCallContent("c" + round, "propose_plan", new Dictionary<string, object?> { ["planName"] = "plan1", ["content"] = "steps" }) : null);

        string[] results = await RunAsync(invoker, model, tool, runner: false);

        Assert.Equal(1, runs); // Tool ran once
        Assert.Equal(2, model.Requests); // Model was asked twice (first for tool call, second got null)
    }

    // Behaviour 5: Runner request with propose_check ends the turn (with CheckAuditRole).
    [Fact]
    public async Task T5_runner_propose_check_ends_the_turn()
    {
        var invoker = NewInvoker();
        int runs = 0;
        var tool = AIFunctionFactory.Create((string check, string why) => { runs++; return "Received."; }, "propose_check", "Proposes a check.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, "propose_check", new Dictionary<string, object?> { ["check"] = "exit 0", ["why"] = "was wrong" }) : null;
        });

        string[] results = await RunAsync(invoker, model, tool, runner: true, role: PlanRunnerToolPolicy.CheckAuditRole);

        Assert.Equal(1, runs); // Tool ran
        Assert.Equal(1, model.Requests); // Turn ended after first request
    }

    // Behaviour 5 control: Runner with different tool (list_directory) and model continues asking.
    [Fact]
    public async Task T5b_runner_list_directory_continues_the_turn()
    {
        var invoker = NewInvoker();
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path) => { runs++; return "a.cs\nb.cs"; }, "list_directory", "Lists a directory.");
        var model = new ScriptedModel(round =>
            round == 0 ? new FunctionCallContent("c" + round, "list_directory", new Dictionary<string, object?> { ["path"] = "src" }) : null);

        string[] results = await RunAsync(invoker, model, tool, runner: true, role: PlanRunnerToolPolicy.CheckAuditRole);

        Assert.Equal(1, runs); // Tool ran once
        Assert.Equal(2, model.Requests); // Model was asked twice (first for tool call, second got null)
    }

    // Behaviour 7: Tool throws exception - InvalidOperationException completes, result contains error, journal records it.
    [Fact]
    public async Task T7_tool_throws_invalid_operation_exception_records_error()
    {
        var invoker = NewInvoker();
        var tool = AIFunctionFactory.Create((string path) =>
        {
            throw new InvalidOperationException("boom");
        }, "read_file", "Reads a file.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, "read_file", new Dictionary<string, object?> { ["path"] = "f" }) : null;
        });

        string contextId = NewContextId();
        string[] results = await RunAsync(invoker, model, tool, runner: false, contextId: contextId);

        Assert.NotEmpty(results);
        Assert.Contains("boom", results[0]);

        // Verify the journal recorded an error for that call
        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(contextId);
        FleetContextEvent result = Assert.Single(events, e => e.Kind == FleetContextEventKind.ToolResult);
        Assert.True(ContextText.Bool(result.Payload, "isError"));
    }

    // Behaviour 7 variant: Tool throws OperationCanceledException - not recorded as an error in the journal.
    [Fact]
    public async Task T7b_tool_throws_operation_canceled_exception_not_recorded_as_error()
    {
        var invoker = NewInvoker();
        var tool = AIFunctionFactory.Create((string path) =>
        {
            throw new OperationCanceledException("canceled");
        }, "read_file", "Reads a file.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, "read_file", new Dictionary<string, object?> { ["path"] = "f" }) : null;
        });

        string contextId = NewContextId();
        try
        {
            await RunAsync(invoker, model, tool, runner: false, contextId: contextId);
        }
        catch (OperationCanceledException)
        {
            // Expected: exception propagates
        }

        // Verify the journal has NO error record for that call (not recorded since exception was thrown, not caught)
        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(contextId);
        var resultEvent = events.FirstOrDefault(e => e.Kind == FleetContextEventKind.ToolResult);
        if (resultEvent is not null)
        {
            Assert.False(ContextText.Bool(resultEvent.Payload, "isError"));
        }
    }
}
