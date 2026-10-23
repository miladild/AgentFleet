using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// Characterisation tests for FleetFunctionInvoker: wiring of policy checks, gate blocks, loop guard markers,
/// termination conditions, and journal recording.
/// </summary>
[Collection(ContextTestBase.SqliteRecordCollection)]
public sealed class FleetFunctionInvokerTests : ContextTestBase
{
    private readonly string _plansDir = Path.Combine(Path.GetTempPath(), "fleet-invoker-plans-" + Guid.NewGuid().ToString("N"));

    private static readonly TimeSpan SafetyLimit = ChatApprovalsTests.SafetyLimit;

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

    private FleetFunctionInvoker NewInvoker(bool planMode = false, int rounds = 25, ChatApprovals? approvals = null, string approvalLevel = "off", IReadOnlySet<string>? readOnly = null)
    {
        Directory.CreateDirectory(_plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = _plansDir }).Build());
        var validator = new FakeValidator(code => new DiagramCheck(true, true, code, null));
        Func<string, string?, CancellationToken, Task<string>> checkCommand = (command, _, _) =>
            Task.FromResult("Exit code: 0\n--- stdout ---\nok");
        var tools = new PlanTools(plans, validator, checkCommand);
        return new FleetFunctionInvoker(Journal, PlanMode(planMode), plans, new SwappableNameSet(readOnly ?? PlanGate.BuiltInReadOnlyTools), tools, NullLogger.Instance, rounds,
            approvals, () => approvalLevel);
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

    private async Task<string[]> RunAsync(FleetFunctionInvoker invoker, ScriptedModel model, AITool tool, bool runner, string? role = null, string? contextId = null, CancellationToken cancellationToken = default, StepToolActivity? activity = null)
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
            if (activity is not null)
                options.AdditionalProperties[StepToolActivity.Key] = activity;
        }

        using IDisposable scope = Scope(contextId ?? NewContextId());
        ChatResponse response = await client.GetResponseAsync("go", options, cancellationToken);
        return response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(c => c.Result?.ToString() ?? "").ToArray();
    }

    [Fact]
    public async Task Tool_span_records_only_name_outcome_and_result_size()
    {
        var stopped = new List<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == FleetTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        const string argumentMarker = "private-tool-argument";
        const string resultMarker = "private-tool-result";
        var tool = AIFunctionFactory.Create((string path) => resultMarker, "list_directory", "Lists.");
        var model = new ScriptedModel(round => round == 0
            ? new FunctionCallContent("tool-call", "list_directory", new Dictionary<string, object?> { ["path"] = argumentMarker })
            : null);

        await RunAsync(NewInvoker(), model, tool, runner: false);

        Activity span = Assert.Single(stopped, item => item.GetTagItem("fleet.tool") is not null);
        Assert.Equal("list_directory", span.GetTagItem("fleet.tool"));
        Assert.Equal("ok", span.GetTagItem("fleet.tool_outcome"));
        Assert.Equal(resultMarker.Length, span.GetTagItem("fleet.result_chars"));
        Assert.DoesNotContain(argumentMarker, string.Join(" ", span.TagObjects.Select(tag => tag.Value)));
        Assert.DoesNotContain(resultMarker, string.Join(" ", span.TagObjects.Select(tag => tag.Value)));
    }

    private const string PromptMarker = "private-prompt-marker";
    private const string ArgumentMarker = "private-tool-argument-marker";
    private const string ResultMarker = "private-tool-result-marker";

    // The chain the backend builds: model telemetry under function invocation and our invoker, then agent telemetry on top.
    private async Task RunProductionLikeTurnAsync()
    {
        var invoker = NewInvoker();
        var tool = AIFunctionFactory.Create((string path) => ResultMarker, "list_directory", "Lists.");
        var model = new ScriptedModel(round => round == 0
            ? new FunctionCallContent("c0", "list_directory", new Dictionary<string, object?> { ["path"] = ArgumentMarker })
            : null);
        IChatClient modelClient = new ChatClientBuilder(model)
            .UseOpenTelemetry(NullLoggerFactory.Instance, "AgentFleet.Model", client => client.EnableSensitiveData = false)
            .Build();
        IChatClient client = new ChatClientBuilder(modelClient).UseFunctionInvocation(null, o =>
        {
            o.IncludeDetailedErrors = true;
            o.MaximumIterationsPerRequest = 200;
            o.MaximumConsecutiveErrorsPerRequest = 6;
            o.FunctionInvoker = invoker.InvokeAsync;
        }).Build();
        var agent = new ChatClientAgent(client, instructions: "Answer briefly.", name: "fleet", tools: [tool]);
        AIAgent instrumented = agent.AsBuilder()
            .UseOpenTelemetry("AgentFleet.Agent", a => a.EnableSensitiveData = false)
            .Build(new ServiceCollection().BuildServiceProvider());

        using IDisposable scope = Scope(NewContextId());
        await instrumented.RunAsync(PromptMarker).WaitAsync(SafetyLimit);
    }

    // Every string an activity carries: its name, tag keys and values, event names and their tags, baggage.
    private static IEnumerable<string> TextOf(Activity activity)
    {
        yield return activity.DisplayName;
        foreach (KeyValuePair<string, object?> tag in activity.TagObjects)
        {
            yield return tag.Key;
            foreach (string value in StringsOf(tag.Value)) yield return value;
        }
        foreach (ActivityEvent evt in activity.Events)
        {
            yield return evt.Name;
            foreach (KeyValuePair<string, object?> tag in evt.Tags)
            {
                yield return tag.Key;
                foreach (string value in StringsOf(tag.Value)) yield return value;
            }
        }
        foreach (KeyValuePair<string, string?> item in activity.Baggage)
        {
            yield return item.Key;
            if (item.Value is not null) yield return item.Value;
        }
    }

    private static IEnumerable<string> StringsOf(object? value) => value switch
    {
        null => [],
        string text => [text],
        System.Collections.IEnumerable items => items.Cast<object?>().SelectMany(StringsOf),
        _ => [value.ToString() ?? string.Empty]
    };

    [Fact]
    public async Task Telemetry_on_the_tool_call_path_never_records_prompt_argument_or_result_text()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fleet-invoker-telemetry-" + Guid.NewGuid().ToString("N"));
        var captured = new List<Activity>();
        try
        {
            using IDisposable? provider = FleetTelemetry.Configure("file", folder);
            using ActivityListener listener = new()
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => { lock (captured) captured.Add(activity); }
            };
            ActivitySource.AddActivityListener(listener);

            await RunProductionLikeTurnAsync();

            Activity[] stopped;
            lock (captured) stopped = captured.ToArray();
            // The probe only means something if the framework's own spans are among those it inspected.
            Assert.Contains(stopped, activity => activity.GetTagItem("fleet.tool") is not null);
            Assert.Contains(stopped, activity => activity.DisplayName.StartsWith("execute_tool", StringComparison.Ordinal));
            Assert.Contains(stopped, activity => activity.DisplayName.StartsWith("invoke_agent", StringComparison.Ordinal));
            string all = string.Join("\n", stopped.SelectMany(TextOf));
            Assert.DoesNotContain(PromptMarker, all);
            Assert.DoesNotContain(ArgumentMarker, all);
            Assert.DoesNotContain(ResultMarker, all);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Telemetry_file_for_the_tool_call_path_never_records_prompt_argument_or_result_text()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fleet-invoker-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FleetTelemetry.Configure("file", folder))
            {
                await RunProductionLikeTurnAsync();
            }

            string text = string.Concat(Directory.GetFiles(folder, "telemetry-*.jsonl").Select(File.ReadAllText));
            Assert.Contains("list_directory", text);
            Assert.DoesNotContain(PromptMarker, text);
            Assert.DoesNotContain(ArgumentMarker, text);
            Assert.DoesNotContain(ResultMarker, text);
            // The allow-list keeps identifiers and drops the static tool schema the framework puts on every model span.
            Assert.DoesNotContain("gen_ai.tool.definitions", text);
            Assert.DoesNotContain("gen_ai.tool.description", text);
            Assert.DoesNotContain("gen_ai.agent.description", text);
            Assert.Contains("gen_ai.operation.name", text);
            Assert.Contains("gen_ai.tool.name", text);
            Assert.Contains("fleet.tool", text);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    // The outcome tag says what the code path did, so words in a tool's own output (a file that says "refused") do not
    // change it.
    private async Task<string?> ToolOutcomeAsync(FleetFunctionInvoker invoker, ScriptedModel model, AITool tool, string toolName, bool runner)
    {
        var stopped = new List<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == FleetTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (stopped) stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);

        await RunAsync(invoker, model, tool, runner: runner).WaitAsync(SafetyLimit);

        Activity span = Assert.Single(stopped, item => toolName.Equals(item.GetTagItem("fleet.tool")));
        return span.GetTagItem("fleet.tool_outcome") as string;
    }

    [Fact]
    public async Task A_tool_result_that_mentions_refused_is_still_tagged_ok()
    {
        var tool = AIFunctionFactory.Create((string path) => "connection refused by the remote host", "read_probe_file", "Reads.");
        var model = new ScriptedModel(round => round == 0 ? new FunctionCallContent("c0", "read_probe_file", new Dictionary<string, object?> { ["path"] = "f" }) : null);

        Assert.Equal("ok", await ToolOutcomeAsync(NewInvoker(), model, tool, "read_probe_file", runner: false));
    }

    [Fact]
    public async Task A_plan_gate_refusal_is_tagged_refused()
    {
        var tool = AIFunctionFactory.Create((string path, string content) => "wrote", "write_file", "Writes a file.");

        Assert.Equal("refused", await ToolOutcomeAsync(NewInvoker(planMode: true), OneWriteCall("write_file"), tool, "write_file", runner: false));
    }

    [Fact]
    public async Task A_runner_policy_refusal_is_tagged_refused()
    {
        var tool = AIFunctionFactory.Create((string command) => "ok", "run_command", "Runs a command.");
        var model = new ScriptedModel(round => new FunctionCallContent("c" + round, "run_command", new Dictionary<string, object?> { ["command"] = "npm publish" }));

        Assert.Equal("refused", await ToolOutcomeAsync(NewInvoker(), model, tool, "run_command", runner: true));
    }

    [Fact]
    public async Task A_rejected_chat_approval_is_tagged_approval_denied()
    {
        var stopped = new List<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == FleetTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (stopped) stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var approvals = NewApprovals();
        var invoker = NewInvoker(approvals: approvals, approvalLevel: "all");
        var tool = AIFunctionFactory.Create((string path, string content) => "wrote", "write_file", "Writes a file.");

        Task<string[]> turn = RunAsync(invoker, OneWriteCall("write_file"), tool, runner: false);
        PendingApproval waiting = (await ChatApprovalsTests.WaitForPendingAsync(approvals)).Single();
        Assert.True(approvals.Decide(waiting.Id, approve: false));
        await turn.WaitAsync(SafetyLimit);

        Activity span = Assert.Single(stopped, item => "write_file".Equals(item.GetTagItem("fleet.tool")));
        Assert.Equal("approval-denied", span.GetTagItem("fleet.tool_outcome"));
    }

    [Fact]
    public async Task A_tool_that_throws_is_tagged_error()
    {
        var tool = AIFunctionFactory.Create((string path) => { throw new InvalidOperationException("disk unavailable"); }, "read_probe_failing", "Reads.");
        var model = new ScriptedModel(round => round == 0 ? new FunctionCallContent("c0", "read_probe_failing", new Dictionary<string, object?> { ["path"] = "f" }) : null);

        Assert.Equal("error", await ToolOutcomeAsync(NewInvoker(), model, tool, "read_probe_failing", runner: false));
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

    private static string RoundBudget(int roundsLeft) =>
        $"[Round budget: {roundsLeft} tool calls left in this round. Leave the project in a state that works, and say what you would do next.]";

    [Fact]
    public async Task A_runner_result_names_the_rounds_left_once_five_or_fewer_remain()
    {
        var invoker = NewInvoker(rounds: 25);
        var tool = AIFunctionFactory.Create((string path) => "ok " + path, "list_directory", "Lists.");
        var model = new ScriptedModel(round => new FunctionCallContent("c" + round, "list_directory", new Dictionary<string, object?> { ["path"] = "dir" + round }));

        string[] results = await RunAsync(invoker, model, tool, runner: true);

        Assert.True(results.Length >= 24, "Expected the results of the first 24 rounds, got " + results.Length);
        for (int index = 0; index < 24; index++)
        {
            int roundsLeft = 25 - (index + 1);
            string expected = roundsLeft <= 5 ? $"ok dir{index}\n\n{RoundBudget(roundsLeft)}" : $"ok dir{index}";
            Assert.Equal(expected, results[index]);
        }
    }

    [Fact]
    public async Task A_chat_result_never_names_the_rounds_left()
    {
        var invoker = NewInvoker(rounds: 25);
        var tool = AIFunctionFactory.Create((string path) => "ok " + path, "list_directory", "Lists.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 30
                ? new FunctionCallContent("c" + round, "list_directory", new Dictionary<string, object?> { ["path"] = "dir" + round })
                : null;
        });

        string[] results = await RunAsync(invoker, model, tool, runner: false);

        Assert.True(results.Length > 25, "Chat should not be cut by the round limit (results=" + results.Length + ")");
        Assert.All(results, result => Assert.DoesNotContain("[Round budget:", result));
    }

    [Fact]
    public async Task A_runner_attempt_counts_its_tool_calls_and_whether_one_edited_a_file()
    {
        var invoker = NewInvoker();
        var activity = new StepToolActivity();
        var write = AIFunctionFactory.Create((string path) => "Wrote " + path, "write_file", "Writes.");
        int callCount = 0;
        var model = new ScriptedModel(round => ++callCount <= 3
            ? new FunctionCallContent("c" + round, "write_file", new Dictionary<string, object?> { ["path"] = "f" + round })
            : null);

        await RunAsync(invoker, model, write, runner: true, activity: activity);

        Assert.Equal(3, activity.ToolCalls);
        Assert.True(activity.EditToolCalled);
    }

    [Fact]
    public async Task A_runner_attempt_that_only_reads_is_counted_without_an_edit()
    {
        var invoker = NewInvoker();
        var activity = new StepToolActivity();
        var list = AIFunctionFactory.Create((string path) => "ok " + path, "list_directory", "Lists.");
        int callCount = 0;
        var model = new ScriptedModel(round => ++callCount <= 2
            ? new FunctionCallContent("c" + round, "list_directory", new Dictionary<string, object?> { ["path"] = "dir" + round })
            : null);

        await RunAsync(invoker, model, list, runner: true, activity: activity);

        Assert.Equal(2, activity.ToolCalls);
        Assert.False(activity.EditToolCalled);
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

    // Behaviour 7 variant 1: Tool throws OperationCanceledException with a live token - comes back as an error result AND is journaled as a tool error.
    // The Microsoft.Extensions.AI FunctionInvokingChatClient turns the exception into an error result for the model
    // when the cancellation token is NOT cancelled; the invoker journals it before rethrowing.
    [Fact]
    public async Task T7b1_A_tool_that_throws_OperationCanceledException_with_a_live_token_comes_back_as_an_error_result_and_is_journaled_as_a_tool_error()
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
        string[] results = await RunAsync(invoker, model, tool, runner: false, contextId: contextId);

        // With a live (not-cancelled) token, the exception is turned into an error result
        Assert.NotEmpty(results);
        Assert.NotEqual("", results[0]); // Error text is not empty

        // The tool's own cancellation is a failed call: the journal records it as an error (the caller did not cancel)
        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(contextId);
        FleetContextEvent result = Assert.Single(events, e => e.Kind == FleetContextEventKind.ToolResult);
        Assert.True(ContextText.Bool(result.Payload, "isError"));
    }

    // Tool cancels itself while the caller's token is live: the invoker journals the error and rethrows.
    [Fact]
    public async Task A_tool_that_cancels_itself_with_a_live_token_is_journaled_as_an_error_and_the_exception_propagates()
    {
        var invoker = NewInvoker();
        var tool = AIFunctionFactory.Create((string path) =>
        {
            throw new OperationCanceledException("tool timed out");
        }, "read_file", "Reads a file.");
        var context = new FunctionInvocationContext
        {
            Function = tool,
            CallContent = new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "f" }),
            Arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["path"] = "f" }),
            Messages = [],
        };

        string contextId = NewContextId();
        using IDisposable scope = Scope(contextId);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await invoker.InvokeAsync(context, CancellationToken.None).AsTask());

        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(contextId);
        FleetContextEvent result = Assert.Single(events, e => e.Kind == FleetContextEventKind.ToolResult);
        Assert.True(ContextText.Bool(result.Payload, "isError"));
    }

    // Behaviour 7 variant 2: Cancelled request propagates cancellation - exception escapes and no ToolResult is journaled.
    // When the cancellation token IS cancelled, the exception propagates out of GetResponseAsync.
    [Fact]
    public async Task T7b2_A_cancelled_request_propagates_cancellation()
    {
        var invoker = NewInvoker();
        var cts = new CancellationTokenSource();
        var tool = AIFunctionFactory.Create((string path) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }, "read_file", "Reads a file.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, "read_file", new Dictionary<string, object?> { ["path"] = "f" }) : null;
        });

        string contextId = NewContextId();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await RunAsync(invoker, model, tool, runner: false, contextId: contextId, cancellationToken: cts.Token);
        });

        // Verify the journal contains NO ToolResult event for that call
        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(contextId);
        Assert.DoesNotContain(events, e => e.Kind == FleetContextEventKind.ToolResult);
    }

    // Chat approvals (see ChatApprovals): a chat tool that can change something waits for the user when the setting is on.
    // The plan runner, read-only tools and the fleet's own bookkeeping never wait.

    private static ChatApprovals NewApprovals(ManualClock? clock = null) =>
        new(clock ?? new ManualClock(), () => 240, NullLogger.Instance);

    private static ScriptedModel OneWriteCall(string toolName) =>
        new(round => round == 0
            ? new FunctionCallContent("c" + round, toolName, new Dictionary<string, object?> { ["path"] = "f", ["content"] = "x" })
            : null);

    [Fact]
    public async Task With_chat_approvals_off_a_chat_tool_runs_without_asking()
    {
        var approvals = NewApprovals();
        var invoker = NewInvoker(approvals: approvals, approvalLevel: "off");
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "wrote"; }, "write_file", "Writes a file.");

        string[] results = await RunAsync(invoker, OneWriteCall("write_file"), tool, runner: false).WaitAsync(SafetyLimit);

        Assert.Equal(1, runs);
        Assert.Equal("wrote", results[0]);
        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public async Task With_chat_approvals_on_a_chat_tool_waits_for_the_user_and_runs_once_approved()
    {
        var approvals = NewApprovals();
        var invoker = NewInvoker(approvals: approvals, approvalLevel: "all");
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "wrote"; }, "write_file", "Writes a file.");
        string contextId = NewContextId();

        Task<string[]> turn = RunAsync(invoker, OneWriteCall("write_file"), tool, runner: false, contextId: contextId);
        PendingApproval waiting = (await ChatApprovalsTests.WaitForPendingAsync(approvals)).Single();

        Assert.Equal(0, runs);
        Assert.Equal("write_file", waiting.Tool);
        Assert.Equal(contextId, waiting.ContextId);
        Assert.True(approvals.Decide(waiting.Id, approve: true));

        string[] results = await turn.WaitAsync(SafetyLimit);
        Assert.Equal(1, runs);
        Assert.Equal("wrote", results[0]);
    }

    [Fact]
    public async Task A_rejected_chat_tool_is_not_run_the_model_is_told_and_the_call_is_recorded_as_an_error()
    {
        var approvals = NewApprovals();
        var invoker = NewInvoker(approvals: approvals, approvalLevel: "all");
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "wrote"; }, "write_file", "Writes a file.");
        var model = OneWriteCall("write_file");
        string contextId = NewContextId();

        Task<string[]> turn = RunAsync(invoker, model, tool, runner: false, contextId: contextId);
        PendingApproval waiting = (await ChatApprovalsTests.WaitForPendingAsync(approvals)).Single();
        Assert.True(approvals.Decide(waiting.Id, approve: false));

        string[] results = await turn.WaitAsync(SafetyLimit);
        Assert.Equal(0, runs);
        Assert.Equal("The user did not approve running write_file.", results[0]);
        FleetContextEvent recorded = Assert.Single(Store.AllEvents(contextId), e => e.Kind == FleetContextEventKind.ToolResult);
        Assert.True(ContextText.Bool(recorded.Payload, "isError"));
        // The turn goes on after a refusal, so the model is asked again to answer.
        Assert.Equal(2, model.Requests);
    }

    [Fact]
    public async Task A_chat_tool_nobody_answers_is_not_run_and_the_model_is_told_it_was_not_approved_in_time()
    {
        var clock = new ManualClock();
        var approvals = NewApprovals(clock);
        var invoker = NewInvoker(approvals: approvals, approvalLevel: "all");
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "wrote"; }, "write_file", "Writes a file.");

        Task<string[]> turn = RunAsync(invoker, OneWriteCall("write_file"), tool, runner: false);
        await ChatApprovalsTests.WaitForPendingAsync(approvals);
        clock.Advance(TimeSpan.FromSeconds(240));

        string[] results = await turn.WaitAsync(SafetyLimit);
        Assert.Equal(0, runs);
        Assert.Equal("write_file was not approved in time.", results[0]);
        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public async Task A_read_only_tool_never_waits_for_approval()
    {
        var approvals = NewApprovals();
        var invoker = NewInvoker(approvals: approvals, approvalLevel: "all");
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path) => { runs++; return "a.cs"; }, "read_file", "Reads a file.");
        var model = new ScriptedModel(round =>
            round == 0 ? new FunctionCallContent("c" + round, "read_file", new Dictionary<string, object?> { ["path"] = "f" }) : null);

        string[] results = await RunAsync(invoker, model, tool, runner: false).WaitAsync(SafetyLimit);

        Assert.Equal(1, runs);
        Assert.Equal("a.cs", results[0]);
        Assert.Empty(approvals.Pending());
    }

    [Theory]
    [InlineData("off", "write_file")]
    [InlineData("risky", "write_file")]
    [InlineData("all", "write_file")]
    [InlineData("off", "run_command")]
    [InlineData("risky", "run_command")]
    [InlineData("all", "run_command")]
    public async Task A_plan_runner_call_never_waits_for_approval(string level, string name)
    {
        var approvals = NewApprovals();
        var invoker = NewInvoker(approvals: approvals, approvalLevel: level);
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "ran"; }, name, "Test tool.");

        string[] results = await RunAsync(invoker, OneWriteCall(name), tool, runner: true).WaitAsync(SafetyLimit);

        Assert.Equal(1, runs);
        Assert.Equal("ran", results[0]);
        Assert.Empty(approvals.Pending());
    }

    [Theory]
    [InlineData("propose_plan")]
    [InlineData("record_decision")]
    public async Task Fleet_bookkeeping_never_waits_for_approval(string toolName)
    {
        var approvals = NewApprovals();
        // No tool counts as read-only here, so only the bookkeeping exemption can keep these calls from waiting.
        var invoker = NewInvoker(approvals: approvals, approvalLevel: "all", readOnly: new HashSet<string>());
        int runs = 0;
        var tool = AIFunctionFactory.Create((string value) => { runs++; return "noted"; }, toolName, "Bookkeeping.");
        int callCount = 0;
        var model = new ScriptedModel(round =>
        {
            callCount++;
            return callCount <= 1 ? new FunctionCallContent("c" + round, toolName, new Dictionary<string, object?> { ["value"] = "x" }) : null;
        });

        string[] results = await RunAsync(invoker, model, tool, runner: false).WaitAsync(SafetyLimit);

        Assert.Equal(1, runs);
        Assert.Equal("noted", results[0]);
        Assert.Empty(approvals.Pending());
    }

    [Theory]
    [InlineData("run_command")]
    [InlineData("run_git_command")]
    [InlineData("http_request")]
    public async Task Risky_command_and_network_tools_wait(string name)
    {
        var approvals = NewApprovals();
        var invoker = NewInvoker(approvals: approvals, approvalLevel: "risky");
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "ran"; }, name, "Test tool.");
        Task<string[]> turn = RunAsync(invoker, OneWriteCall(name), tool, runner: false);
        PendingApproval waiting = (await ChatApprovalsTests.WaitForPendingAsync(approvals)).Single();
        Assert.Equal(0, runs);
        Assert.Equal(name, waiting.Tool);
        Assert.True(approvals.Decide(waiting.Id, approve: true));
        Assert.Equal("ran", (await turn.WaitAsync(SafetyLimit))[0]);
        Assert.Equal(1, runs);
    }

    [Theory]
    [InlineData("risky", "write_file")]
    [InlineData("risky", "read_file")]
    [InlineData("off", "run_command")]
    public async Task Tools_outside_the_selected_level_run_without_waiting(string level, string name)
    {
        var approvals = NewApprovals();
        var invoker = NewInvoker(approvals: approvals, approvalLevel: level);
        int runs = 0;
        var tool = AIFunctionFactory.Create((string path, string content) => { runs++; return "ran"; }, name, "Test tool.");
        string[] results = await RunAsync(invoker, OneWriteCall(name), tool, runner: false).WaitAsync(SafetyLimit);
        Assert.Equal(1, runs);
        Assert.Equal("ran", results[0]);
        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public async Task In_plan_mode_a_command_is_refused_before_anyone_is_asked()
    {
        var approvals = NewApprovals();
        var invoker = NewInvoker(planMode: true, approvals: approvals, approvalLevel: "risky");
        int runs = 0;
        var tool = AIFunctionFactory.Create((string command) => { runs++; return "ran"; }, "run_command", "Runs a command.");

        Assert.Equal("refused", await ToolOutcomeAsync(invoker, OneWriteCall("run_command"), tool, "run_command", runner: false));
        Assert.Equal(0, runs);
        Assert.Empty(approvals.Pending());
    }

    [Fact]
    public void No_risky_tool_is_read_only_or_fleet_bookkeeping()
    {
        Assert.Empty(FleetFunctionInvoker.RiskyTools.Intersect(PlanGate.BuiltInReadOnlyTools));
        foreach (string bookkeeping in new[] { "propose_plan", "record_decision", "search_context" })
        {
            Assert.DoesNotContain(bookkeeping, FleetFunctionInvoker.RiskyTools);
        }
    }
}
