using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>The router with a fake machine: what it hands the model, and what it writes into the durable record.</summary>
public sealed class RouterJournalTests : ContextTestBase
{
    private sealed class RecordingClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hel");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "lo");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class TagsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"models\":[{\"name\":\"test-model:latest\"}]}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TagsHandler());
    }

    private readonly RecordingClient _node = new();
    private readonly FleetRoutingChatClient _router;

    public RouterJournalTests()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONFIG_PATH"] = Path.Combine(Root, "fleet.config.json"),
                ["FLEET_PLANS_DIR"] = Path.Combine(Root, "plans"),
                ["HUB_OLLAMA_MODEL"] = "test-model"
            })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        FleetOptions options = FleetOptions.Load(configuration, configStore);
        _router = new FleetRoutingChatClient(
            new RecordingClient(),
            [new FleetRouteTarget(options.Nodes[0], _node)],
            new FleetHealthMonitor(options, new Factory()),
            new FleetModeService(configStore),
            new FleetPlanModeService(configStore),
            new FleetPlanStore(configuration),
            new HashSet<string>(),
            new FleetActivityLog(),
            NullLogger.Instance,
            Journal);
    }

    private async Task<string> Ask(string text, ChatOptions? options = null)
    {
        var reply = new StringBuilder();
        await foreach (ChatResponseUpdate update in _router.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, text)], options))
        {
            reply.Append(update.Text);
        }

        return reply.ToString();
    }

    [Fact]
    public async Task A_run_with_a_context_hands_the_model_the_pinned_decision_and_records_the_route_and_the_answer()
    {
        string id = NewContextId();
        using IDisposable scope = Scope(id);
        Journal.RecordDecision("Money is stored as integer cents", "avoids rounding");

        string reply = await Ask("how do I store prices?");

        Assert.StartsWith("hello", reply);
        IReadOnlyList<ChatMessage> received = Assert.Single(_node.Calls);
        int contextAt = received.ToList().FindIndex(m => m.Role == ChatRole.System && m.Text.Contains("Money is stored as integer cents"));
        int questionAt = received.ToList().FindIndex(m => m.Role == ChatRole.User);
        Assert.True(contextAt >= 0, "the pinned decision reached the model");
        Assert.True(contextAt < questionAt, "and it comes before the conversation");

        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(id);
        FleetContextEvent route = Assert.Single(events, e => e.Kind == FleetContextEventKind.Route);
        Assert.Equal("hub", route.Node);
        FleetContextEvent output = Assert.Single(events, e => e.Kind == FleetContextEventKind.AssistantOutput);
        Assert.Equal("hello", ContextText.String(output.Payload, "text"));
        FleetContextEvent delivery = Assert.Single(events, e => e.Kind == FleetContextEventKind.ContextAssembled);
        Assert.Contains("integer cents", ContextText.String(delivery.Payload, "text"));
    }

    [Fact]
    public async Task A_run_with_no_context_is_exactly_as_before_no_extra_message_and_nothing_recorded()
    {
        await Ask("hello there");

        IReadOnlyList<ChatMessage> received = Assert.Single(_node.Calls);
        Assert.Equal(["hello there"], received.Select(m => m.Text));
        Assert.Empty(Store.ListContexts());
    }

    [Fact]
    public async Task A_context_with_nothing_pinned_adds_no_message_but_still_records_the_route()
    {
        string id = NewContextId();
        using IDisposable scope = Scope(id);

        await Ask("just chatting");

        Assert.DoesNotContain(Assert.Single(_node.Calls), m => m.Role == ChatRole.System);
        Assert.Contains(Store.AllEvents(id), e => e.Kind == FleetContextEventKind.Route);
        Assert.DoesNotContain(Store.AllEvents(id), e => e.Kind == FleetContextEventKind.ContextAssembled);
    }

    [Fact]
    public async Task A_plan_step_call_is_recorded_under_the_steps_task_with_its_tier()
    {
        string id = NewContextId();
        string task = "plan-abc-step-2";
        using IDisposable scope = Scope(id, task);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = FleetTiers.Heavy }
        };

        await Ask("do the step", options);

        FleetContextEvent route = Assert.Single(Store.AllEvents(id), e => e.Kind == FleetContextEventKind.Route);
        Assert.Equal(task, route.TaskId);
        Assert.Equal("plan step (heavy)", ContextText.String(route.Payload, "reason"));
        Assert.Equal("heavy", ContextText.String(route.Payload, "tier"));
    }
}
