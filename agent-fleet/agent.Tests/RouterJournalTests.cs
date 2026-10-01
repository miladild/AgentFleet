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
        public List<ChatOptions?> Options { get; } = [];
        public bool FailAfterFirstChunk { get; set; }
        public bool FailBeforeFirstChunk { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            Options.Add(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hello")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            Options.Add(options);
            if (FailBeforeFirstChunk)
            {
                throw new HttpRequestException("worker is offline");
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "hel");
            if (FailAfterFirstChunk)
            {
                throw new IOException("worker stream disconnected");
            }

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Method == HttpMethod.Post
                ? "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}"
                : "{\"models\":[{\"name\":\"test-model:latest\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
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

    [Fact]
    public async Task An_unpinned_plan_step_uses_the_hub_in_aggressive_mode_and_keeps_its_tier()
    {
        string id = NewContextId();
        using IDisposable scope = Scope(id, "plan-abc-step-2");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONFIG_PATH"] = Path.Combine(Root, "aggressive-plan.config.json"),
                ["FLEET_PLANS_DIR"] = Path.Combine(Root, "plans"),
                ["HUB_OLLAMA_MODEL"] = "test-model"
            })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        var workerConfig = new FleetNodeConfig("worker", "http://127.0.0.1:11434/v1", "test-model", "worker", FleetTiers.Standard);
        configStore.Save(configStore.Current with { Nodes = [configStore.Current.Nodes[0], workerConfig] });
        var modeService = new FleetModeService(configStore);
        modeService.SetMode(FleetMode.Aggressive);
        FleetOptions options = FleetOptions.Load(configuration, configStore);
        FleetNodeDefinition hub = options.Nodes.Single(node => node.Fallback);
        FleetNodeDefinition worker = options.Nodes.Single(node => node.Name == "worker");
        var hubRaw = new RecordingClient();
        var workerRaw = new RecordingClient();
        var router = new FleetRoutingChatClient(
            new RecordingClient(),
            [new FleetRouteTarget(hub, hubRaw), new FleetRouteTarget(worker, workerRaw)],
            new FleetHealthMonitor(options, new Factory()),
            modeService,
            new FleetPlanModeService(configStore),
            new FleetPlanStore(configuration),
            new HashSet<string>(),
            new FleetActivityLog(),
            NullLogger.Instance,
            Journal);
        var requestOptions = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = FleetTiers.Light }
        };

        await foreach (ChatResponseUpdate _ in router.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "do the work")], requestOptions)) { }

        Assert.Single(hubRaw.Calls);
        Assert.Empty(workerRaw.Calls);
        FleetContextEvent route = Assert.Single(Store.AllEvents(id), e => e.Kind == FleetContextEventKind.Route);
        Assert.Equal("hub", route.Node);
        Assert.Equal("light", ContextText.String(route.Payload, "tier"));
        Assert.Equal("plan step (light), aggressive hub mode", ContextText.String(route.Payload, "reason"));
    }

    [Fact]
    public async Task A_selected_plan_machine_overrides_tier_routing_without_changing_tier_or_leaking_router_options()
    {
        string id = NewContextId();
        string task = "plan-abc-step-2";
        using IDisposable scope = Scope(id, task);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONFIG_PATH"] = Path.Combine(Root, "selected-machine.config.json"),
                ["FLEET_PLANS_DIR"] = Path.Combine(Root, "plans"),
                ["HUB_OLLAMA_MODEL"] = "test-model"
            })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        var workerConfig = new FleetNodeConfig("worker", "http://127.0.0.1:11434/v1", "test-model", "worker", FleetTiers.Standard);
        configStore.Save(configStore.Current with { Nodes = [configStore.Current.Nodes[0], workerConfig] });
        var modeService = new FleetModeService(configStore);
        modeService.SetMode(FleetMode.Aggressive);
        FleetOptions options = FleetOptions.Load(configuration, configStore);
        FleetNodeDefinition hub = options.Nodes.Single(node => node.Fallback);
        FleetNodeDefinition worker = options.Nodes.Single(node => node.Name == "worker");
        var hubRaw = new RecordingClient();
        var workerRaw = new RecordingClient();
        var router = new FleetRoutingChatClient(
            new RecordingClient(),
            [new FleetRouteTarget(hub, hubRaw), new FleetRouteTarget(worker, workerRaw)],
            new FleetHealthMonitor(options, new Factory()),
            modeService,
            new FleetPlanModeService(configStore),
            new FleetPlanStore(configuration),
            new HashSet<string>(),
            new FleetActivityLog(),
            NullLogger.Instance,
            Journal);
        var requestOptions = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [FleetRoutingChatClient.RunnerTierKey] = FleetTiers.Light,
                [FleetRoutingChatClient.RunnerMachineKey] = "worker",
                ["num_ctx"] = 4096
            }
        };

        await foreach (ChatResponseUpdate _ in router.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "do the work")], requestOptions)) { }

        Assert.Empty(hubRaw.Calls);
        Assert.Single(workerRaw.Calls);
        ChatOptions sent = Assert.IsType<ChatOptions>(workerRaw.Options.Single());
        Assert.DoesNotContain(FleetRoutingChatClient.RunnerTierKey, sent.AdditionalProperties!.Keys);
        Assert.DoesNotContain(FleetRoutingChatClient.RunnerMachineKey, sent.AdditionalProperties.Keys);
        Assert.Equal(4096, sent.AdditionalProperties["num_ctx"]);
        FleetContextEvent route = Assert.Single(Store.AllEvents(id), e => e.Kind == FleetContextEventKind.Route);
        Assert.Equal("worker", route.Node);
        Assert.Equal("light", ContextText.String(route.Payload, "tier"));
        Assert.Contains("selected machine (worker)", ContextText.String(route.Payload, "reason"));
    }

    [Fact]
    public async Task A_partial_stream_failure_is_recorded_and_given_to_the_next_call()
    {
        string id = NewContextId();
        using IDisposable scope = Scope(id);
        _node.FailAfterFirstChunk = true;

        string firstReply = await Ask("first request");
        Assert.StartsWith("hel", firstReply);
        Assert.Contains("partial, not a complete answer", firstReply);
        Assert.Contains("Open Context for the error details", firstReply);

        FleetContextEvent error = Assert.Single(Store.EventsOfKinds(id, [FleetContextEventKind.Error], 10));
        Assert.Equal("stream", ContextText.String(error.Payload, "source"));
        Assert.Equal("worker stream disconnected", ContextText.String(error.Payload, "message"));
        Assert.Equal("hel", ContextText.String(error.Payload, "partialOutput"));

        _node.FailAfterFirstChunk = false;
        await Ask("continue after the disconnect");

        IReadOnlyList<ChatMessage> received = _node.Calls[^1];
        ChatMessage context = Assert.Single(received, message => message.Role == ChatRole.System);
        Assert.Contains("worker stream disconnected", context.Text);
        Assert.Contains("Partial output before failure: hel", context.Text);
        Assert.Contains(Store.EventsOfKinds(id, [FleetContextEventKind.ContextAssembled], 10), delivery =>
            ContextText.String(delivery.Payload, "text")!.Contains("worker stream disconnected"));
    }

    [Fact]
    public async Task A_pre_response_failover_is_visible_and_attributed_to_the_machine_that_answered()
    {
        string id = NewContextId();
        using IDisposable scope = Scope(id);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONFIG_PATH"] = Path.Combine(Root, "failover.config.json"),
                ["FLEET_PLANS_DIR"] = Path.Combine(Root, "plans"),
                ["HUB_OLLAMA_MODEL"] = "test-model"
            })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        FleetNodeConfig workerConfig = new("worker", "http://127.0.0.1:11434/v1", "test-model", "worker", FleetTiers.Standard);
        configStore.Save(configStore.Current with { Nodes = [configStore.Current.Nodes[0], workerConfig] });
        FleetOptions options = FleetOptions.Load(configuration, configStore);
        FleetNodeDefinition hub = options.Nodes.Single(node => node.Fallback);
        FleetNodeDefinition worker = options.Nodes.Single(node => node.Name == "worker");
        var health = new FleetHealthMonitor(options, new Factory());
        var fallbackRaw = new RecordingClient();
        var workerRaw = new RecordingClient { FailBeforeFirstChunk = true };
        IChatClient fallback = new ResilientChatClient(fallbackRaw, hub, health, NullLogger.Instance, journal: Journal);
        IChatClient workerClient = new ResilientChatClient(workerRaw, worker, health, NullLogger.Instance, fallback, Journal, hub);
        var router = new FleetRoutingChatClient(
            new RecordingClient(),
            [new FleetRouteTarget(hub, fallback), new FleetRouteTarget(worker, workerClient)],
            health,
            new FleetModeService(configStore),
            new FleetPlanModeService(configStore),
            new FleetPlanStore(configuration),
            new HashSet<string>(),
            new FleetActivityLog(),
            NullLogger.Instance,
            Journal);
        var optionsForWorker = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = FleetTiers.Standard }
        };
        var reply = new StringBuilder();
        await foreach (ChatResponseUpdate update in router.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "do the work")], optionsForWorker))
        {
            reply.Append(update.Text);
        }

        Assert.Contains("hello", reply.ToString());
        Assert.Contains("via hub", reply.ToString());
        Assert.Contains(Assert.Single(fallbackRaw.Calls), message =>
            message.Role == ChatRole.System && message.Text.Contains("worker is offline"));
        Assert.Contains(Store.EventsOfKinds(id, [FleetContextEventKind.Error], 10), e => ContextText.String(e.Payload, "source") == "failover");
        Assert.Contains(Store.EventsOfKinds(id, [FleetContextEventKind.Route], 10), e =>
            e.Node == "hub" && ContextText.String(e.Payload, "phase") == "fallback");
        FleetContextEvent addedContext = Assert.Single(Store.EventsOfKinds(id, [FleetContextEventKind.ContextAssembled], 10),
            e => ContextText.Bool(e.Payload, "supplemental") == true);
        Assert.Equal("hub", addedContext.Node);
        Assert.Contains("worker is offline", ContextText.String(addedContext.Payload, "text"));
        Assert.Equal("hub", ContextText.String(Assert.Single(Store.EventsOfKinds(id, [FleetContextEventKind.AssistantOutput], 10)).Payload, "node"));
    }
}
