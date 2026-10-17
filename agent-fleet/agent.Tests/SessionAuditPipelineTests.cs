using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class SessionAuditPipelineTests : ContextTestBase
{
    private sealed class ScriptedModel(bool crashOnThirdRequest) : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _inputs = [];
        private int _requests;

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Inputs
        {
            get { lock (_inputs) return _inputs.ToArray(); }
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatMessage[] input = messages.ToArray();
            int request = Record(input);
            if (crashOnThirdRequest && request == 3)
            {
                throw new InvalidOperationException("synthetic model failure");
            }

            if (request <= 3 || crashOnThirdRequest && request == 4)
            {
                int action = crashOnThirdRequest && request == 4 ? 1 : request;
                var call = new FunctionCallContent($"call-{request}", $"action_{action}", new Dictionary<string, object?> { ["value"] = action.ToString() });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "finished")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatMessage[] input = messages.ToArray();
            int request = Record(input);
            if (crashOnThirdRequest && request == 3)
            {
                throw new InvalidOperationException("synthetic model failure");
            }

            if (request <= 3 || crashOnThirdRequest && request == 4)
            {
                int action = crashOnThirdRequest && request == 4 ? 1 : request;
                var call = new FunctionCallContent($"call-{request}", $"action_{action}", new Dictionary<string, object?> { ["value"] = action.ToString() });
                yield return new ChatResponseUpdate(ChatRole.Assistant, [call]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "finished");
            }

            await Task.CompletedTask;
        }

        private int Record(ChatMessage[] input)
        {
            lock (_inputs) _inputs.Add(input);
            return Interlocked.Increment(ref _requests);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private async Task<(IHost Host, ScriptedModel Model, int[] Invocations)> BuildHost(bool crashOnThirdRequest, bool registerSessionStore)
    {
        string configPath = Path.Combine(Root, "fleet.config.json");
        await File.WriteAllTextAsync(configPath, """
            { "triageModel": "synthetic", "mode": "conservative", "planModeEnabled": false,
              "nodes": [{ "name": "worker", "url": "http://worker.example.test/v1", "model": "synthetic", "purpose": "", "fallback": true }],
              "tools": {} }
            """);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLEET_CONFIG_PATH"] = configPath,
            ["FLEET_PLANS_DIR"] = Path.Combine(Root, "plans")
        }).Build();
        var configStore = new FleetConfigStore(configuration);
        FleetOptions fleetOptions = FleetOptions.Load(configuration, configStore);
        var model = new ScriptedModel(crashOnThirdRequest);
        var routeModel = new ScriptedModel(false);
        IChatClient router = new FleetRoutingChatClient(
            routeModel,
            [new FleetRouteTarget(Assert.Single(fleetOptions.Nodes), model)],
            new FleetHealthMonitor(fleetOptions, new StubHttpClientFactory()),
            new FleetModeService(configStore),
            new FleetPlanModeService(configStore, RequestContext),
            new FleetPlanStore(configuration),
            new HashSet<string>(),
            new FleetActivityLog(),
            NullLogger.Instance,
            Journal);
        var registry = new FleetToolRegistry(configStore, [], new SwappableNameSet([]), NullLoggerFactory.Instance);
        var plans = new FleetPlanStore(configuration);
        var planTools = new PlanTools(plans, new FakeValidator(code => new DiagramCheck(true, true, code, null)),
            (_, _, _) => Task.FromResult("Exit code: 0\n--- stdout ---\nok"));
        var invoker = new FleetFunctionInvoker(Journal, new FleetPlanModeService(configStore, RequestContext), plans,
            new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), planTools, NullLogger.Instance, 25);
        IChatClient client = FleetHarness.BuildAgentClient(router, registry, Journal,
            new FleetPlanModeService(configStore, RequestContext), invoker, NullLoggerFactory.Instance);

        int[] invocations = new int[3];
        AITool[] tools = Enumerable.Range(0, 3).Select(index => AIFunctionFactory.Create((string value) =>
        {
            Interlocked.Increment(ref invocations[index]);
            return $"action {index + 1} ran with {value}";
        }, $"action_{index + 1}", "A synthetic action.")).ToArray();
        var agent = new ChatClientAgent(client, instructions: "Synthetic session audit.", name: "fleet", tools: tools);

        IHost host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAGUIServer();
                    if (registerSessionStore)
                    {
                        services.AddKeyedSingleton<AgentSessionStore>(agent.Name!, Store);
                    }
                })
                .Configure(app =>
                {
                    FleetContextMiddleware.Use(app, Store, RequestContext, NullLogger.Instance);
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapAGUIServer("/", agent));
                }))
            .StartAsync();
        return (host, model, invocations);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"models\":[{\"name\":\"synthetic:latest\"}]}", Encoding.UTF8, "application/json")
            });
    }

    private static string RunJson(string threadId, string runId, params object[] messages) => JsonSerializer.Serialize(new
    {
        threadId,
        runId,
        messages
    });

    private async Task<string> Post(IHost host, string json)
    {
        using HttpResponseMessage response = await host.GetTestClient().PostAsync("/", new StringContent(json, Encoding.UTF8, "application/json"));
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return body;
    }

    private string[] JournalTools(string contextId) => Store.AllEvents(contextId)
        .Where(e => e.Kind == FleetContextEventKind.ToolCall)
        .Select(e => ContextText.String(e.Payload, "name")!)
        .ToArray();

    private static string ModelInput(IReadOnlyList<ChatMessage> messages) => string.Join("\n", messages.Select(m => m.Text));

    private static string[] ModelToolCalls(IReadOnlyList<ChatMessage> messages) => messages
        .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
        .Select(call => call.Name)
        .ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_clean_run_records_each_tool_and_the_next_turn_input(bool registerSessionStore)
    {
        string threadId = NewContextId();
        (IHost host, ScriptedModel model, int[] invocations) = await BuildHost(false, registerSessionStore);
        using (host)
        {
            await Post(host, RunJson(threadId, "run-1", new { id = "user-1", role = "user", content = "Run all three actions." }));
            string[] calls = JournalTools(threadId);
            int journalResults = Store.AllEvents(threadId).Count(e => e.Kind == FleetContextEventKind.ToolResult);
            await Post(host, RunJson(threadId, "run-2",
                new { id = "user-1", role = "user", content = "Run all three actions." },
                new
                {
                    id = "assistant-1",
                    role = "assistant",
                    content = "",
                    toolCalls = Enumerable.Range(1, 3).Select(index => new
                    {
                        id = $"call-{index}",
                        type = "function",
                        function = new { name = $"action_{index}", arguments = $"{{\"value\":\"{index}\"}}" }
                    }).ToArray()
                },
                new { id = "tool-1", role = "tool", toolCallId = "call-1", name = "action_1", content = "action 1 ran with 1" },
                new { id = "tool-2", role = "tool", toolCallId = "call-2", name = "action_2", content = "action 2 ran with 2" },
                new { id = "tool-3", role = "tool", toolCallId = "call-3", name = "action_3", content = "action 3 ran with 3" },
                new { id = "assistant-2", role = "assistant", content = "finished" },
                new { id = "user-2", role = "user", content = "Next turn." }));

            Assert.Equal(["action_1", "action_2", "action_3"], calls);
            Assert.Equal(3, journalResults);
            Assert.Equal([1, 1, 1], invocations);
            Assert.Contains(model.Inputs, input => input.Any(message => message.Text?.Contains("Run all three actions.", StringComparison.Ordinal) == true));
            Assert.Contains(model.Inputs[^1], message => message.Text?.Contains("Next turn.", StringComparison.Ordinal) == true);
            string[] expectedModelCalls = registerSessionStore
                ? ["action_1", "action_2", "action_3", "action_1", "action_2", "action_3"]
                : ["action_1", "action_2", "action_3"];
            Assert.Equal(expectedModelCalls, ModelToolCalls(model.Inputs[^1]));
            Assert.Equal(2, Store.AllEvents(threadId).Count(e => e.Kind == FleetContextEventKind.RunInput));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_model_failure_after_two_tools_leaves_the_failed_run_out_of_the_next_client_transcript(bool registerSessionStore)
    {
        string threadId = NewContextId();
        (IHost host, ScriptedModel model, int[] invocations) = await BuildHost(true, registerSessionStore);
        using (host)
        {
            string first = await Post(host, RunJson(threadId, "run-1", new { id = "user-1", role = "user", content = "Run all three actions." }));
            string[] calls = JournalTools(threadId);
            int journalResults = Store.AllEvents(threadId).Count(e => e.Kind == FleetContextEventKind.ToolResult);
            int firstRunModelCalls = model.Inputs.Count;
            await Post(host, RunJson(threadId, "run-2",
                new { id = "user-1", role = "user", content = "Run all three actions." },
                new { id = "user-2", role = "user", content = "Try again." }));

            Assert.Contains("Fleet stopped while worker was answering.", first, StringComparison.Ordinal);
            Assert.Equal(["action_1", "action_2"], calls);
            Assert.Equal(2, journalResults);
            Assert.Equal([2, 1, 0], invocations);
            IReadOnlyList<ChatMessage> retryInput = model.Inputs[firstRunModelCalls];
            string[] expectedHistory = registerSessionStore ? ["action_1", "action_2"] : [];
            Assert.Equal(expectedHistory, ModelToolCalls(retryInput));
            if (!registerSessionStore)
            {
                Assert.DoesNotContain("action_1 ran", ModelInput(retryInput), StringComparison.Ordinal);
                Assert.DoesNotContain("action_2 ran", ModelInput(retryInput), StringComparison.Ordinal);
            }
            Assert.Contains("Run all three actions.", ModelInput(retryInput), StringComparison.Ordinal);
            Assert.Contains("Try again.", ModelInput(retryInput), StringComparison.Ordinal);
        }
    }
}
