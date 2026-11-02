using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// Tests for the agent pipeline composition in FleetHarness.
/// </summary>
public sealed class FleetHarnessTests : ContextTestBase
{
    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public int Requests { get; private set; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests++;
            LastOptions = options;
            LastMessages = messages.ToArray();
            ChatMessage reply = new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static FleetToolRegistry CreateRegistry()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fleet-harness-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new FleetConfigStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(dir, "fleet.config.json") })
            .Build());
        var readOnly = new SwappableNameSet(["read_file", "project_overview"]);
        return new FleetToolRegistry(config, ["read_file", "run_command", "write_file", "project_overview"], readOnly, NullLoggerFactory.Instance);
    }

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
        string plansDir = Path.Combine(Path.GetTempPath(), "fleet-harness-plans-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = plansDir }).Build());
        var validator = new FakeValidator(code => new DiagramCheck(true, true, code, null));
        Func<string, string?, CancellationToken, Task<string>> checkCommand = (command, _, _) =>
            Task.FromResult("Exit code: 0\n--- stdout ---\nok");
        var tools = new PlanTools(plans, validator, checkCommand);
        return new FleetFunctionInvoker(Journal, PlanMode(planMode), plans, new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), tools, NullLogger.Instance, rounds);
    }

    [Fact]
    public void The_chain_from_outermost_to_innermost_is_worker_platform_dynamic_tools_function_invocation_then_the_model()
    {
        var fake = new RecordingChatClient();
        var registry = CreateRegistry();
        var invoker = NewInvoker();

        IChatClient client = FleetHarness.BuildAgentClient(fake, registry, Journal, PlanMode(false), invoker, NullLoggerFactory.Instance);

        var types = new List<string>();
        object? current = client;
        types.Add(current.GetType().Name);
        while (current is DelegatingChatClient delegating)
        {
            PropertyInfo? innerProp = typeof(DelegatingChatClient).GetProperty("InnerClient", BindingFlags.Instance | BindingFlags.NonPublic);
            current = innerProp?.GetValue(delegating);
            if (current is not null)
            {
                types.Add(current.GetType().Name);
            }
        }

        // The actual order from the built client is: DynamicToolsChatClient -> FunctionInvokingChatClient -> fleetClient
        // (The ChatClientBuilder applies .Use() in reverse order: last added is innermost)
        Assert.Equal(["DynamicToolsChatClient", "FunctionInvokingChatClient", "RecordingChatClient"], types);
    }

    [Fact]
    public void The_recovery_note_layer_is_the_outermost_layer_only_when_the_setting_is_on()
    {
        var fake = new RecordingChatClient();
        IChatClient client = FleetHarness.BuildAgentClient(fake, CreateRegistry(), Journal, PlanMode(false), NewInvoker(), NullLoggerFactory.Instance, recoveryNoteEnabled: true);

        Assert.Equal(["ChatRecoveryNoteClient", "DynamicToolsChatClient", "FunctionInvokingChatClient", "RecordingChatClient"], ChainTypes(client));
    }

    [Fact]
    public async Task A_plan_runner_request_never_gets_the_recovery_note_through_the_real_chain()
    {
        var fake = new RecordingChatClient();
        IChatClient client = FleetHarness.BuildAgentClient(fake, CreateRegistry(), Journal, PlanMode(false), NewInvoker(), NullLoggerFactory.Instance, recoveryNoteEnabled: true);
        using (Scope(NewContextId()))
        {
            Journal.RecordToolExecution("lost-call", "read_file", "{\"path\":\"a.txt\"}", "file text", false);
            ChatMessage[] transcript = [new ChatMessage(ChatRole.User, "continue")];

            await client.GetResponseAsync(transcript);
            Assert.Contains(fake.LastMessages!, message => message.Text?.Contains("already ran earlier", StringComparison.Ordinal) == true);

            var runner = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = "heavy" } };
            await client.GetResponseAsync(transcript, runner);
            Assert.DoesNotContain(fake.LastMessages!, message => message.Text?.Contains("already ran earlier", StringComparison.Ordinal) == true);
        }
    }

    private static List<string> ChainTypes(IChatClient client)
    {
        var types = new List<string> { client.GetType().Name };
        object? current = client;
        while (current is DelegatingChatClient delegating)
        {
            PropertyInfo? innerProp = typeof(DelegatingChatClient).GetProperty("InnerClient", BindingFlags.Instance | BindingFlags.NonPublic);
            current = innerProp?.GetValue(delegating);
            if (current is not null)
            {
                types.Add(current.GetType().Name);
            }
        }

        return types;
    }

    [Fact]
    public void The_function_invocation_settings_are_the_measured_ones()
    {
        var fake = new RecordingChatClient();
        var registry = CreateRegistry();
        var invoker = NewInvoker();

        IChatClient client = FleetHarness.BuildAgentClient(fake, registry, Journal, PlanMode(false), invoker, NullLoggerFactory.Instance);

        var invocationClient = client.GetService(typeof(FunctionInvokingChatClient)) as FunctionInvokingChatClient;
        Assert.NotNull(invocationClient);
        Assert.True(invocationClient.IncludeDetailedErrors);
        Assert.Equal(200, invocationClient.MaximumIterationsPerRequest);
        Assert.Equal(6, invocationClient.MaximumConsecutiveErrorsPerRequest);
        Assert.NotNull(invocationClient.FunctionInvoker);
    }

    [Fact]
    public void Context_fitting_is_not_done_upstream_of_the_node_client()
    {
        var fake = new RecordingChatClient();
        var registry = CreateRegistry();
        var invoker = NewInvoker();

        IChatClient client = FleetHarness.BuildAgentClient(fake, registry, Journal, PlanMode(false), invoker, NullLoggerFactory.Instance);

        var types = new List<string>();
        object? current = client;
        while (current is DelegatingChatClient delegating)
        {
            types.Add(current.GetType().Name);
            PropertyInfo? innerProp = typeof(DelegatingChatClient).GetProperty("InnerClient", BindingFlags.Instance | BindingFlags.NonPublic);
            current = innerProp?.GetValue(delegating);
        }
        if (current is not null)
        {
            types.Add(current.GetType().Name);
        }

        // Rule 1: context fitting happens in the node client, not in the pipeline
        Assert.DoesNotContain(types, t => t.Contains("Compaction") || t.Contains("AIContextProvider"));
    }

    [Fact]
    public async Task Only_the_tools_the_registry_offers_reach_the_model()
    {
        var fake = new RecordingChatClient();

        // Create a registry and disable run_command
        string dir = Path.Combine(Path.GetTempPath(), "fleet-harness-registry-4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new FleetConfigStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(dir, "fleet.config.json") })
            .Build());
        var readOnly = new SwappableNameSet(["read_file", "project_overview"]);
        var registry = new FleetToolRegistry(config, ["read_file", "run_command"], readOnly, NullLoggerFactory.Instance);

        // Disable run_command in the config
        config.Save(config.Current with
        {
            Tools = new Dictionary<string, FleetToolConfig>(config.Current.Tools) { ["run_command"] = new FleetToolConfig(false) }
        });

        var invoker = NewInvoker();

        IChatClient client = FleetHarness.BuildAgentClient(fake, registry, Journal, PlanMode(false), invoker, NullLoggerFactory.Instance);

        var tools = new AITool[]
        {
            AIFunctionFactory.Create(() => "ok", "read_file", "Reads a file."),
            AIFunctionFactory.Create(() => "ok", "run_command", "Runs a command.")
        };

        var options = new ChatOptions { Tools = tools.ToList() };
        await client.GetResponseAsync("test", options);

        // Precondition: the fake was called and received a tool list
        Assert.NotNull(fake.LastOptions);
        Assert.NotEmpty(fake.LastOptions!.Tools!);

        // Only read_file should reach the model (run_command is disabled in the registry)
        Assert.Single(fake.LastOptions.Tools, t => t.Name == "read_file");
        Assert.DoesNotContain(fake.LastOptions.Tools, t => t.Name == "run_command");
    }

    // Asks for one web search, then answers.
    private sealed class SearchingModel : IChatClient
    {
        public List<string[]> OfferedTools { get; } = [];
        public string? ToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatMessage[] list = messages.ToArray();
            OfferedTools.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
            if (list.SelectMany(message => message.Contents).OfType<FunctionResultContent>().FirstOrDefault() is { } result)
            {
                ToolResult = result.Result?.ToString();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("w1", "web_search", new Dictionary<string, object?> { ["query"] = "node test server close" })])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stuck_steps_model_is_offered_web_search_and_each_query_is_reported(bool offered)
    {
        var model = new SearchingModel();
        string dir = Path.Combine(Path.GetTempPath(), "fleet-harness-registry-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new FleetConfigStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(dir, "fleet.config.json") })
            .Build());
        var registry = new FleetToolRegistry(config, ["read_file", "web_search"], new SwappableNameSet(["read_file"]), NullLoggerFactory.Instance);
        IChatClient client = FleetHarness.BuildAgentClient(model, registry, Journal, PlanMode(false), NewInvoker(), NullLoggerFactory.Instance);
        var lookups = new List<string>();
        var activity = new StepToolActivity { WebLookup = (tool, target) => lookups.Add($"{tool}: {target}") };
        var properties = new AdditionalPropertiesDictionary
        {
            [FleetRoutingChatClient.RunnerTierKey] = "standard",
            [StepToolActivity.Key] = activity
        };
        if (offered) properties[FleetRoutingChatClient.RunnerWebKey] = true;

        await client.GetResponseAsync("fix the step", new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", "read_file"), AIFunctionFactory.Create((string query) => $"results for {query}", "web_search")],
            AdditionalProperties = properties
        });

        if (offered)
        {
            Assert.Contains("web_search", model.OfferedTools[0]);
            Assert.Equal(["web_search: node test server close"], lookups);
            Assert.Contains("--- BEGIN UNTRUSTED WEB TEXT (web_search) ---\nresults for node test server close\n--- END UNTRUSTED WEB TEXT ---", model.ToolResult);
        }
        else
        {
            Assert.DoesNotContain("web_search", model.OfferedTools[0]);
            Assert.Empty(lookups);
            Assert.DoesNotContain("results for", model.ToolResult);
        }
    }

    [Fact]
    public void Web_lookup_is_for_a_steps_own_model_only()
    {
        Assert.True(PlanRunnerToolPolicy.Offers("web_fetch", fromRunner: true, role: null, webOffered: true));
        Assert.False(PlanRunnerToolPolicy.Offers("web_fetch", fromRunner: true, role: null, webOffered: false));
        Assert.False(PlanRunnerToolPolicy.Offers("web_search", fromRunner: true, role: PlanRunnerToolPolicy.TestRulingRole, webOffered: true));
        Assert.False(PlanRunnerToolPolicy.Offers("web_search", fromRunner: true, role: PlanRunnerToolPolicy.TestWriterRole, webOffered: true));
        Assert.True(PlanRunnerToolPolicy.Offers("web_search", fromRunner: false, role: null));
        Assert.Null(PlanRunnerToolPolicy.Refusal("web_search", role: null, webOffered: true));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal("web_search", role: null, webOffered: false));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal("web_fetch", role: PlanRunnerToolPolicy.StepReviewRole, webOffered: true));
    }
}
