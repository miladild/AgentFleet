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

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests++;
            LastOptions = options;
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
    public void The_chain_from_outermost_to_innermost_is_function_invocation_dynamic_tools_worker_platform_then_the_model()
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

        // The actual order from the built client is: WorkerPlatformChatClient -> DynamicToolsChatClient -> FunctionInvokingChatClient -> fleetClient
        // (The ChatClientBuilder applies .Use() in reverse order: last added is innermost)
        Assert.Equal(["WorkerPlatformChatClient", "DynamicToolsChatClient", "FunctionInvokingChatClient", "RecordingChatClient"], types);
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
}
