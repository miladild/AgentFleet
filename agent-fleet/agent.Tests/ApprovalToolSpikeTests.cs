using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class ApprovalToolSpikeTests : ContextTestBase
{
    private sealed class ScriptedModel(params string[] toolNames) : IChatClient
    {
        private int _requests;
        public IList<AITool>? OfferedTools { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) > 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }

            OfferedTools = options?.Tools?.ToList();
            var calls = toolNames.Select((name, index) => new FunctionCallContent($"call-{index + 1}", name,
                new Dictionary<string, object?> { ["value"] = "synthetic" })).ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList())));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) > 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                yield break;
            }

            OfferedTools = options?.Tools?.ToList();
            var calls = toolNames.Select((name, index) => new FunctionCallContent($"call-{index + 1}", name,
                new Dictionary<string, object?> { ["value"] = "synthetic" })).ToList();
            yield return new ChatResponseUpdate(ChatRole.Assistant, calls.Cast<AIContent>().ToList());
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
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
                Content = new StringContent(request.RequestUri?.AbsolutePath == "/api/tags"
                    ? "{\"models\":[{\"name\":\"synthetic:latest\"}]}"
                    : "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8,
                    "application/json")
            });
    }

    private async Task<(IHost Host, ScriptedModel Model)> BuildHost(AIFunction tool, params string[] calls)
    {
        string configPath = Path.Combine(Root, "fleet.config.json");
        await File.WriteAllTextAsync(configPath, """
            { "triageModel": "synthetic", "mode": "conservative", "planModeEnabled": false,
              "nodes": [{ "name": "worker", "url": "http://worker.example.test/v1", "model": "synthetic", "purpose": "", "fallback": true }],
              "tools": {} }
            """);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONFIG_PATH"] = configPath,
                ["FLEET_PLANS_DIR"] = Path.Combine(Root, "plans")
            })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        FleetOptions fleetOptions = FleetOptions.Load(configuration, configStore);
        var scripted = new ScriptedModel(calls);
        var rescue = new ToolCallRescueChatClient(scripted, NullLogger.Instance);
        var router = new FleetRoutingChatClient(
            new ScriptedModel("unused"),
            [new FleetRouteTarget(Assert.Single(fleetOptions.Nodes), rescue)],
            new FleetHealthMonitor(fleetOptions, new StubHttpClientFactory()),
            new FleetModeService(configStore),
            new FleetPlanModeService(configStore, RequestContext),
            new FleetPlanStore(configuration),
            new HashSet<string>(),
            new FleetActivityLog(),
            NullLogger.Instance,
            Journal);
        // The registry owns/filter names but has no API for registering an AITool instance. The wrapped function below
        // is therefore supplied via ChatClientAgent.tools; registering the wrapper through the tool registry itself is not covered here.
        var registry = new FleetToolRegistry(configStore, ["mutate"], new SwappableNameSet([]), NullLoggerFactory.Instance);
        var planStore = new FleetPlanStore(configuration);
        var planTools = new PlanTools(planStore, new FakeValidator(code => new DiagramCheck(true, true, code, null)),
            (_, _, _) => Task.FromResult("Exit code: 0\n--- stdout ---\nok"));
        var invoker = new FleetFunctionInvoker(Journal, new FleetPlanModeService(configStore, RequestContext), planStore,
            new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), planTools, NullLogger.Instance, 25);
        IChatClient client = FleetHarness.BuildAgentClient(router, registry, Journal,
            new FleetPlanModeService(configStore, RequestContext), invoker, NullLoggerFactory.Instance);
        var agent = new ChatClientAgent(client, instructions: "Synthetic approval spike.", name: "approval-spike", tools: [tool]);

        IHost host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAGUIServer();
                    services.AddKeyedSingleton<AgentSessionStore>(agent.Name!, Store);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapAGUIServer("/", agent));
                }))
            .StartAsync();
        return (host, scripted);
    }

    [Fact(Skip = "Spike: an approval answered in a new request did not run the tool; re-enable after a supported approval and session-resume path exists.")]
    public async Task Approval_required_function_reaches_the_model_through_the_fleet_and_agui_http_chains()
    {
        int invocations = 0;
        AIFunction inner = AIFunctionFactory.Create((string value) =>
        {
            Interlocked.Increment(ref invocations);
            return "ran " + value;
        }, "mutate", "A synthetic mutating action.");
        var wrapped = new ApprovalRequiredAIFunction(inner);

        string threadId = NewContextId();
        string input = $$"""
            { "threadId": "{{threadId}}", "runId": "run-1",
              "messages": [{ "id": "user-1", "role": "user", "content": "Use the mutating action." }],
              "tools": [{ "name": "frontend_action", "description": "A synthetic UI action.",
                "parameters": { "type": "object", "properties": { "value": { "type": "string" } } } }] }
            """;
        (IHost host, ScriptedModel scripted) = await BuildHost(wrapped, "frontend_action", "mutate");
        using (host)
        {
            using HttpResponseMessage response = await host.GetTestClient().PostAsync("/", new StringContent(input, Encoding.UTF8, "application/json"));
            string events = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("data:", events);
            Assert.True(scripted.OfferedTools is not null, events);
            Assert.Contains(scripted.OfferedTools!, tool => tool.Name == "mutate" && tool.GetType().Name.Contains("ApprovalRequiredAIFunction", StringComparison.Ordinal));
            Assert.Contains(scripted.OfferedTools!, tool => tool.Name == "frontend_action");
            Assert.Contains("\"$type\":\"toolApprovalRequest\"", events);
            Assert.Contains("\"requiresConfirmation\":true", events);
            Assert.Contains("\"requiresConfirmation\":false", events);
            Assert.Contains("\"type\":\"RUN_ERROR\"", events);
            int frontendEvent = events.IndexOf("\"toolCallName\":\"frontend_action\"", StringComparison.Ordinal);
            int mutateEvent = events.IndexOf("\"toolCallName\":\"mutate\"", StringComparison.Ordinal);
            Assert.True(frontendEvent >= 0 && mutateEvent > frontendEvent, events);
            Assert.Equal(0, Volatile.Read(ref invocations));
            using var sessionDb = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Store.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            sessionDb.Open();
            using SqliteCommand sessionCount = sessionDb.CreateCommand();
            sessionCount.CommandText = "SELECT count(*) FROM agent_sessions WHERE agent_name=@agent AND context_id=@thread;";
            sessionCount.Parameters.AddWithValue("@agent", "approval-spike");
            sessionCount.Parameters.AddWithValue("@thread", threadId);
            Assert.Equal(0L, sessionCount.ExecuteScalar());

            string continuation = $$"""
            { "threadId": "{{threadId}}", "runId": "run-2",
              "messages": [
                { "id": "user-1", "role": "user", "content": "Use the mutating action." },
                { "id": "assistant-1", "role": "assistant", "content": "", "toolCalls": [
                  { "id": "call-2", "type": "function", "function": { "name": "mutate", "arguments": "{\"value\":\"synthetic\"}" } } ] }
              ],
              "resume": [{ "interruptId": "ficc_call-2", "status": "resolved", "payload": true }] }
            """;
            using HttpResponseMessage resumed = await host.GetTestClient().PostAsync("/", new StringContent(continuation, Encoding.UTF8, "application/json"));
            string resumedEvents = await resumed.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            Assert.Contains("data:", resumedEvents);
            Assert.Equal(0, Volatile.Read(ref invocations));

            string rejection = $$"""
            { "threadId": "{{threadId}}", "runId": "run-3",
              "messages": [{ "id": "user-1", "role": "user", "content": "Use the mutating action." }],
              "resume": [{ "interruptId": "ficc_call-2", "status": "cancelled" }] }
            """;
            using HttpResponseMessage rejected = await host.GetTestClient().PostAsync("/", new StringContent(rejection, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            Assert.Equal(0, Volatile.Read(ref invocations));
        }

        AIFunction plain = AIFunctionFactory.Create((string value) =>
        {
            Interlocked.Increment(ref invocations);
            return "ran " + value;
        }, "mutate", "A synthetic mutating action.");
        (IHost plainHost, _) = await BuildHost(plain, "mutate");
        using (plainHost)
        {
            using HttpResponseMessage plainResponse = await plainHost.GetTestClient().PostAsync("/", new StringContent(input, Encoding.UTF8, "application/json"));
            string plainEvents = await plainResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, plainResponse.StatusCode);
            Assert.DoesNotContain("toolApprovalRequest", plainEvents, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref invocations));
        }
    }
}
