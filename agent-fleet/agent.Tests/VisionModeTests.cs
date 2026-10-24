using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A vision machine's mode: how it is saved and loaded, what a health check asks of it, what a routed image does, and what
/// the overall status reports. "off" contacts nothing; "ondemand" asks only for the model list; "always" is the usual check.
/// </summary>
public sealed class VisionModeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-vision-" + Guid.NewGuid().ToString("N"));

    public VisionModeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLEET_CONFIG_PATH"] = Path.Combine(_directory, "fleet.config.json"),
            ["FLEET_PLANS_DIR"] = Path.Combine(_directory, "plans"),
            ["HUB_OLLAMA_MODEL"] = "m"
        })
        .Build();

    private FleetConfigStore ConfigStore() => new(Configuration());

    private FleetConfig Config(params FleetNodeConfig[] nodes) => ConfigStore().Current with { Nodes = nodes };

    private static FleetNodeConfig Hub() => new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);

    private static FleetNodeConfig Worker() => new("worker", "http://192.0.2.20:11434/v1", "m", "worker", "standard");

    private static FleetNodeConfig Vision(string? mode = null) =>
        new("vision", "http://192.0.2.10:11434/v1", "vision-m", "vision", Vision: true, VisionMode: mode);

    private static FleetConfig WithNodes(FleetConfigStore store, params FleetNodeConfig[] nodes) => store.Current with { Nodes = nodes };

    private FleetOptions Options(FleetConfigStore store) => FleetOptions.Load(Configuration(), store);

    // A machine that answers every request the health check or a routed image can make, and records each one.
    private sealed class Machine(string psJson) : HttpMessageHandler
    {
        public readonly List<string> Paths = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                await request.Content.ReadAsStringAsync(cancellationToken);
            }

            string path = request.RequestUri!.AbsolutePath;
            lock (Paths)
            {
                Paths.Add(path);
            }

            string json = path switch
            {
                "/api/tags" => "{\"models\":[{\"name\":\"vision-m:latest\"},{\"name\":\"m:latest\"}]}",
                "/api/ps" => psJson,
                "/api/chat" => "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"done\":true,\"done_reason\":\"stop\"}",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private (FleetHealthMonitor Monitor, FleetOptions Options, Machine Machine) Health(FleetConfig config, string psJson = "{\"models\":[]}")
    {
        FleetConfigStore store = ConfigStore();
        store.Save(config);
        FleetOptions options = FleetOptions.Load(Configuration(), store);
        var machine = new Machine(psJson);
        return (new FleetHealthMonitor(options, new Factory(machine)), options, machine);
    }

    // ---- Saving and loading the setting ----

    [Theory]
    [InlineData("OFF", "off")]
    [InlineData(" Ondemand ", "ondemand")]
    [InlineData("on-demand", "ondemand")]
    [InlineData("On_Demand", "ondemand")]
    [InlineData("on demand", "ondemand")]
    [InlineData("Always", "always")]
    public void Spellings_are_saved_in_their_canonical_form(string written, string canonical)
    {
        FleetConfigStore store = ConfigStore();

        FleetConfig saved = store.Save(WithNodes(store, Hub(), Vision(written)));

        Assert.Equal(canonical, saved.Nodes[1].VisionMode);
    }

    [Fact]
    public void A_vision_machine_without_a_mode_is_saved_and_loaded_as_ondemand()
    {
        FleetConfigStore store = ConfigStore();

        FleetConfig saved = store.Save(WithNodes(store, Hub(), Vision()));

        Assert.Equal(NodeVisionMode.OnDemand, saved.Nodes[1].VisionMode);
        FleetNodeDefinition vision = Options(store).GetNode("vision");
        Assert.Equal(NodeVisionMode.OnDemand, vision.VisionMode);
        Assert.False(vision.IsTurnedOff);
    }

    [Fact]
    public void An_unknown_mode_is_refused_and_the_message_lists_the_three_choices()
    {
        FleetConfigStore store = ConfigStore();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            store.Save(WithNodes(store, Hub(), Vision("sometimes"))));

        Assert.Contains("vision", refused.Message);
        Assert.Contains("sometimes", refused.Message);
        Assert.Contains("choose off, ondemand (the default) or always", refused.Message);
    }

    [Fact]
    public void A_text_machine_cannot_have_a_vision_mode()
    {
        FleetConfigStore store = ConfigStore();
        FleetNodeConfig text = Worker() with { VisionMode = "off" };

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => store.Save(WithNodes(store, Hub(), text)));

        Assert.Contains("worker", refused.Message);
        Assert.Contains("not a vision node", refused.Message);
    }

    [Fact]
    public void A_text_machine_has_no_mode_after_it_was_a_vision_machine()
    {
        FleetConfigStore store = ConfigStore();
        store.Save(WithNodes(store, Hub(), Vision("off")));

        // A save that changes the machine to a text machine and leaves the mode out must not carry "off" across.
        FleetConfig saved = store.Save(WithNodes(store, Hub(), Worker() with { VisionMode = NodeVisionMode.Resolve(false, null, "off") }));

        Assert.Null(saved.Nodes[1].VisionMode);
    }

    [Fact]
    public void The_mode_survives_a_save_and_a_load()
    {
        FleetConfigStore store = ConfigStore();
        store.Save(WithNodes(store, Hub(), Vision("off")));

        FleetConfigStore reloaded = ConfigStore();

        Assert.Equal("off", reloaded.Current.Nodes[1].VisionMode);
        Assert.True(Options(reloaded).GetNode("vision").IsTurnedOff);
    }

    [Fact]
    public void A_save_that_leaves_the_mode_out_keeps_the_previous_one_for_a_vision_machine()
    {
        Assert.Equal("off", NodeVisionMode.Resolve(vision: true, requested: null, previous: "off"));
        Assert.Equal("always", NodeVisionMode.Resolve(vision: true, requested: "always", previous: "off"));
        Assert.Null(NodeVisionMode.Resolve(vision: false, requested: null, previous: "off"));
    }

    [Fact]
    public void The_routing_fingerprint_changes_with_the_mode()
    {
        FleetConfigStore store = ConfigStore();
        // Saved configs, as the running app fingerprints them: the saved spelling is the canonical one.
        FleetConfig ondemand = store.Save(WithNodes(store, Hub(), Vision("ondemand")));
        FleetConfig off = store.Save(WithNodes(store, Hub(), Vision("off")));
        FleetConfig spelledOut = store.Save(WithNodes(store, Hub(), Vision("on demand")));

        Assert.NotEqual(FleetRoutingChatClient.Fingerprint(ondemand), FleetRoutingChatClient.Fingerprint(off));
        Assert.Equal(FleetRoutingChatClient.Fingerprint(ondemand), FleetRoutingChatClient.Fingerprint(spelledOut));
    }

    [Fact]
    public void An_address_used_only_by_a_turned_off_vision_machine_is_refused()
    {
        FleetConfigStore store = ConfigStore();
        store.Save(WithNodes(store, Hub(), Worker(), Vision("off")));
        IReadOnlyList<FleetNodeDefinition> nodes = Options(store).Nodes;

        Assert.True(NodeVisionMode.IsTurnedOffAt(nodes, new Uri("http://192.0.2.10:11434/api/tags")));
    }

    [Fact]
    public void An_address_with_a_text_machine_on_it_is_not_refused_while_the_vision_machine_is_off()
    {
        // The vision machine and a text worker can share one Ollama: the text worker must stay pullable and listable.
        FleetConfigStore store = ConfigStore();
        store.Save(WithNodes(store, Hub(), new FleetNodeConfig("text-twin", "http://192.0.2.10:11434/v1", "other-m", "worker", "standard"), Vision("off")));

        Assert.False(NodeVisionMode.IsTurnedOffAt(Options(store).Nodes, new Uri("http://192.0.2.10:11434/")));
    }

    [Fact]
    public void An_address_with_a_vision_machine_that_is_on_is_not_refused_even_if_another_machine_there_is_off()
    {
        FleetConfigStore store = ConfigStore();
        store.Save(WithNodes(store, Hub(), Vision("off"), new FleetNodeConfig("vision-two", "http://192.0.2.10:11434/v1", "vision-m", "vision", Vision: true, VisionMode: "always")));

        Assert.False(NodeVisionMode.IsTurnedOffAt(Options(store).Nodes, new Uri("http://192.0.2.10:11434/")));
    }

    [Fact]
    public void An_address_no_machine_uses_is_not_refused()
    {
        FleetConfigStore store = ConfigStore();
        store.Save(WithNodes(store, Hub(), Worker(), Vision("off")));
        IReadOnlyList<FleetNodeDefinition> nodes = Options(store).Nodes;

        Assert.False(NodeVisionMode.IsTurnedOffAt(nodes, new Uri("http://192.0.2.10:11435/")));
        Assert.False(NodeVisionMode.IsTurnedOffAt(nodes, new Uri("http://192.0.2.99:11434/")));
        Assert.False(NodeVisionMode.IsTurnedOffAt(nodes, new Uri("http://192.0.2.20:11434/")));
    }

    // ---- What a health check asks ----

    [Fact]
    public async Task An_off_machine_gets_no_request_of_any_kind_not_even_when_forced()
    {
        var (monitor, _, machine) = Health(Config(Hub(), Worker(), Vision("off")));

        NodeHealthSnapshot routine = await monitor.GetNodeAsync("vision", forceProbe: true);
        NodeHealthSnapshot forced = await monitor.GetNodeAsync("vision", forceProbe: true, forceInferenceProbe: true);

        Assert.Empty(machine.Paths);
        foreach (NodeHealthSnapshot snapshot in new[] { routine, forced })
        {
            Assert.True(snapshot.TurnedOff);
            Assert.False(snapshot.Ready);
            Assert.False(snapshot.Reachable);
            Assert.Equal("turned_off", snapshot.Failure);
        }
    }

    [Fact]
    public async Task An_ondemand_machine_is_asked_only_for_its_model_list()
    {
        var (monitor, _, machine) = Health(Config(Hub(), Worker(), Vision("ondemand")));

        NodeHealthSnapshot snapshot = await monitor.GetNodeAsync("vision", forceProbe: true);

        Assert.True(snapshot.Ready);
        Assert.Null(snapshot.Failure);
        Assert.Equal(new[] { "/api/tags" }, machine.Paths);
    }

    [Fact]
    public async Task An_ondemand_machine_is_asked_for_inference_when_a_forced_probe_asks_for_it()
    {
        var (monitor, _, machine) = Health(Config(Hub(), Worker(), Vision("ondemand")));

        NodeHealthSnapshot snapshot = await monitor.GetNodeAsync("vision", forceProbe: true, forceInferenceProbe: true);

        Assert.True(snapshot.Ready);
        Assert.Contains("/api/chat", machine.Paths);
    }

    [Fact]
    public async Task An_always_machine_is_checked_as_before()
    {
        var (monitor, _, machine) = Health(Config(Hub(), Worker(), Vision("always")), psJson: "{\"models\":[]}");

        Assert.True((await monitor.GetNodeAsync("vision", forceProbe: true)).Ready);

        Assert.Equal(new[] { "/api/tags", "/api/ps", "/api/chat" }, machine.Paths);
    }

    [Fact]
    public async Task A_text_machine_is_still_asked_for_inference_whatever_the_vision_setting()
    {
        var (monitor, _, machine) = Health(Config(Hub(), Worker(), Vision("ondemand")));

        Assert.True((await monitor.GetNodeAsync("worker", forceProbe: true)).Ready);

        Assert.Contains("/api/chat", machine.Paths);
    }

    [Fact]
    public async Task Switching_the_mode_is_seen_at_the_next_check_in_both_directions()
    {
        FleetConfigStore store = ConfigStore();
        store.Save(WithNodes(store, Hub(), Worker(), Vision("ondemand")));
        FleetOptions options = Options(store);
        var machine = new Machine("{\"models\":[]}");
        var monitor = new FleetHealthMonitor(options, new Factory(machine));
        Assert.True((await monitor.GetNodeAsync("vision", forceProbe: true)).Ready);

        // What a save does: the machines are replaced and the health snapshots are dropped.
        store.Save(WithNodes(store, Hub(), Worker(), Vision("off")));
        options.ReplaceNodes(store.Current);
        monitor.Forget();
        int before = machine.Paths.Count;
        Assert.True((await monitor.GetNodeAsync("vision")).TurnedOff);
        Assert.Equal(before, machine.Paths.Count);

        store.Save(WithNodes(store, Hub(), Worker(), Vision("ondemand")));
        options.ReplaceNodes(store.Current);
        monitor.Forget();
        NodeHealthSnapshot back = await monitor.GetNodeAsync("vision");

        Assert.True(back.Ready);
        Assert.Contains(machine.Paths.Skip(before), path => path == "/api/tags");
    }

    // ---- The overall status ----

    private static NodeHealthSnapshot Ready(string name) => new(name, "m", true, true, true, DateTimeOffset.UtcNow, null);

    private static NodeHealthSnapshot TurnedOff(string name) =>
        new(name, "m", false, false, false, DateTimeOffset.UtcNow, NodeHealthSnapshot.TurnedOffFailure);

    private static NodeHealthSnapshot NotReady(string name) =>
        new(name, "m", false, false, false, DateTimeOffset.UtcNow, "unreachable");

    [Fact]
    public void The_status_stays_ok_with_a_turned_off_vision_machine_and_every_other_machine_ready()
    {
        Assert.Equal("ok", FleetHealthRollup.Status([Ready("hub"), Ready("worker"), TurnedOff("vision")], "hub"));
    }

    [Fact]
    public void The_status_is_degraded_when_a_real_machine_is_not_ready()
    {
        Assert.Equal("degraded", FleetHealthRollup.Status([Ready("hub"), NotReady("worker"), TurnedOff("vision")], "hub"));
        Assert.Equal("degraded", FleetHealthRollup.Status([Ready("hub"), NotReady("vision")], "hub"));
    }

    [Fact]
    public void The_status_is_unhealthy_when_the_fallback_is_not_ready_as_before()
    {
        Assert.Equal("unhealthy", FleetHealthRollup.Status([NotReady("hub"), Ready("worker"), TurnedOff("vision")], "hub"));
        Assert.Equal("unhealthy", FleetHealthRollup.Status([NotReady("hub"), Ready("worker")], "hub"));
    }

    // ---- Routing ----

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
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private FleetRoutingChatClient Router(FleetConfig config, RecordingClient hub, RecordingClient vision)
    {
        FleetConfigStore store = ConfigStore();
        store.Save(config);
        FleetOptions options = Options(store);
        var targets = new List<FleetRouteTarget>
        {
            new(options.Nodes.Single(node => node.Fallback), hub)
        };
        if (options.Nodes.FirstOrDefault(node => node.Vision) is { } visionNode)
        {
            targets.Add(new FleetRouteTarget(visionNode, vision));
        }

        return new FleetRoutingChatClient(
            new RecordingClient(),
            targets,
            new FleetHealthMonitor(options, new Factory(new Machine("{\"models\":[]}"))),
            new FleetModeService(store),
            new FleetPlanModeService(store),
            new FleetPlanStore(Configuration()),
            new HashSet<string>(),
            new FleetActivityLog(),
            NullLogger.Instance);
    }

    private static List<ChatMessage> ImageQuestion() =>
    [
        new ChatMessage(ChatRole.User, new List<AIContent> { new TextContent("what is this?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png") })
    ];

    private async Task RouteAsync(FleetRoutingChatClient router)
    {
        await foreach (ChatResponseUpdate _ in router.GetStreamingResponseAsync(ImageQuestion())) { }
    }

    private static string TextSentToModel(RecordingClient client)
    {
        IReadOnlyList<ChatMessage> sent = Assert.Single(client.Calls);
        return string.Join("\n", sent.SelectMany(message => message.Contents).OfType<TextContent>().Select(text => text.Text));
    }

    [Fact]
    public async Task An_image_to_an_off_vision_machine_becomes_a_note_and_the_vision_client_is_never_called()
    {
        var hub = new RecordingClient();
        var vision = new RecordingClient();
        FleetRoutingChatClient router = Router(Config(Hub(), Vision("off")), hub, vision);

        await RouteAsync(router);

        Assert.Empty(vision.Calls);
        string text = TextSentToModel(hub);
        Assert.Contains("turned off", text);
        Assert.Contains("visionMode", text);
        Assert.Contains("Set-FleetVision.ps1", text);
        Assert.DoesNotContain("no vision node configured", text);
        Assert.DoesNotContain(hub.Calls[0].SelectMany(message => message.Contents), content => content is DataContent);
    }

    [Theory]
    [InlineData("ondemand")]
    [InlineData("always")]
    public async Task An_image_to_a_vision_machine_that_is_on_reaches_it(string mode)
    {
        var hub = new RecordingClient();
        var vision = new RecordingClient();
        FleetRoutingChatClient router = Router(Config(Hub(), Vision(mode)), hub, vision);

        await RouteAsync(router);

        Assert.Single(vision.Calls);
        Assert.Empty(hub.Calls);
    }

    [Fact]
    public async Task With_no_vision_machine_at_all_the_image_gets_the_existing_note()
    {
        var hub = new RecordingClient();
        FleetRoutingChatClient router = Router(Config(Hub()), hub, new RecordingClient());

        await RouteAsync(router);

        string text = TextSentToModel(hub);
        Assert.Contains("no vision node configured", text);
        Assert.DoesNotContain("turned off", text);
    }

    // ---- Setup report ----

    [Fact]
    public void A_turned_off_vision_machine_is_not_a_setup_failure()
    {
        FleetConfigStore store = ConfigStore();
        store.Save(WithNodes(store, Hub(), Worker(), Vision("off")));
        FleetNodeDefinition vision = Options(store).GetNode("vision");

        SetupCheck check = FleetSetupService.NodeCheck(vision, TurnedOff("vision"), false, FleetSetupService.Hub(Array.Empty<string>()));

        Assert.Equal(FleetSetupService.Info, check.Status);
        Assert.Contains("turned off", check.Title);
    }
}
