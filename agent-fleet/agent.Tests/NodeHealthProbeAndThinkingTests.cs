using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;

namespace AgentFleet.Tests;

/// <summary>
/// Two things measured on a worker that made it write nothing: the health probe went through Ollama's OpenAI endpoint,
/// which loaded the model at 262144 tokens (two thirds of it out of graphics memory) and made every real request reload
/// it; and a model that thinks first spent its whole 4096-token answer on thinking and ended with no tool call.
/// </summary>
public sealed class NodeHealthProbeAndThinkingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-probe-" + Guid.NewGuid().ToString("N"));

    public NodeHealthProbeAndThinkingTests() => Directory.CreateDirectory(_directory);

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

    private sealed record Seen(HttpMethod Method, string Path, string Body);

    private sealed class FakeOllama(string psJson) : HttpMessageHandler
    {
        public readonly List<Seen> Requests = [];

        public string ChatAnswer { get; set; } = "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"done\":true,\"done_reason\":\"stop\"}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            lock (Requests)
            {
                Requests.Add(new Seen(request.Method, path, body));
            }

            string json = path switch
            {
                "/api/tags" => "{\"models\":[{\"name\":\"m:latest\"}]}",
                "/api/ps" => psJson,
                "/api/show" => "{}",
                "/api/chat" => ChatAnswer,
                "/v1/chat/completions" => "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json + "\n", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private FleetConfigStore ConfigStore()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(_directory, "fleet.config.json") })
            .Build();
        return new FleetConfigStore(configuration);
    }

    private (FleetHealthMonitor Monitor, FakeOllama Ollama) Monitor(string psJson, string? api = null)
    {
        FleetConfigStore store = ConfigStore();
        store.Save(store.Current with
        {
            Nodes =
            [
                new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true),
                new("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard", Api: api)
            ]
        });
        var options = FleetOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLEET_CONFIG_PATH"] = Path.Combine(_directory, "fleet.config.json")
        }).Build(), store);
        var ollama = new FakeOllama(psJson);
        return (new FleetHealthMonitor(options, new Factory(ollama)), ollama);
    }

    private static Seen Only(FakeOllama ollama, string path) => Assert.Single(ollama.Requests, r => r.Path == path);

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task The_probe_asks_through_the_native_api_at_the_window_the_model_already_has()
    {
        (FleetHealthMonitor monitor, FakeOllama ollama) = Monitor("{\"models\":[{\"name\":\"m:latest\",\"context_length\":16384}]}");

        NodeHealthSnapshot snapshot = await monitor.GetNodeAsync("worker-a", forceProbe: true);

        Assert.True(snapshot.Ready);
        Assert.DoesNotContain(ollama.Requests, r => r.Path.StartsWith("/v1", StringComparison.Ordinal));
        JsonElement body = Json(Only(ollama, "/api/chat").Body);
        Assert.Equal(16384, body.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(16, body.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(body.GetProperty("think").GetBoolean());
        Assert.False(body.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task The_probe_asks_for_the_smallest_window_when_nothing_is_loaded()
    {
        (FleetHealthMonitor monitor, FakeOllama ollama) = Monitor("{\"models\":[]}");

        Assert.True((await monitor.GetNodeAsync("worker-a", forceProbe: true)).Ready);

        Assert.Equal(ContextSizeChatClient.SmallestSize, Json(Only(ollama, "/api/chat").Body).GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task A_routine_probe_does_not_load_a_model_while_a_different_one_is_working_on_the_machine()
    {
        (FleetHealthMonitor monitor, FakeOllama ollama) = Monitor("{\"models\":[{\"name\":\"other:7b\",\"context_length\":8192}]}");

        NodeHealthSnapshot snapshot = await monitor.GetNodeAsync("worker-a", forceProbe: true);

        Assert.True(snapshot.Ready);
        Assert.Null(snapshot.Failure);
        Assert.DoesNotContain(ollama.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_forced_probe_after_an_outage_still_asks_the_model()
    {
        (FleetHealthMonitor monitor, FakeOllama ollama) = Monitor("{\"models\":[{\"name\":\"other:7b\",\"context_length\":8192}]}");

        NodeHealthSnapshot snapshot = await monitor.GetNodeAsync("worker-a", forceProbe: true, forceInferenceProbe: true);

        Assert.True(snapshot.Ready);
        Assert.Equal(ContextSizeChatClient.SmallestSize, Json(Only(ollama, "/api/chat").Body).GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task A_server_that_is_not_ollama_is_probed_through_its_openai_endpoint()
    {
        (FleetHealthMonitor monitor, FakeOllama ollama) = Monitor("{\"models\":[]}", api: "openai");

        Assert.True((await monitor.GetNodeAsync("worker-a", forceProbe: true)).Ready);

        Assert.Equal(32, Json(Only(ollama, "/v1/chat/completions").Body).GetProperty("max_tokens").GetInt32());
        Assert.DoesNotContain(ollama.Requests, r => r.Path is "/api/chat" or "/api/ps");
    }

    [Fact]
    public async Task A_native_answer_that_never_finishes_is_not_ready()
    {
        (FleetHealthMonitor monitor, FakeOllama ollama) = Monitor("{\"models\":[]}");
        ollama.ChatAnswer = "{}";

        NodeHealthSnapshot snapshot = await monitor.GetNodeAsync("worker-a", forceProbe: true);

        Assert.False(snapshot.Ready);
        Assert.Equal("inference_empty", snapshot.Failure);
    }

    private static async Task<string> ChatBodyAsync(bool thinkingOff, ChatOptions? options = null)
    {
        var ollama = new FakeOllama("{\"models\":[]}");
        var http = new HttpClient(ollama) { BaseAddress = new Uri("http://node:11434/") };
        var client = new ContextSizeChatClient(new OllamaApiClient(http, "m"), http, "m", 32768, NullLogger.Instance, thinkingOff);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "write the file")], options);

        return Only(ollama, "/api/chat").Body;
    }

    [Fact]
    public async Task A_model_is_told_not_to_think_when_the_node_says_off()
    {
        JsonElement body = Json(await ChatBodyAsync(thinkingOff: true));

        Assert.False(body.GetProperty("think").GetBoolean());
        Assert.Equal(8192, body.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(ContextSizeChatClient.MaxAnswerTokens, body.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task A_model_keeps_its_own_behaviour_when_the_node_says_model()
    {
        JsonElement body = Json(await ChatBodyAsync(thinkingOff: false));

        Assert.False(body.TryGetProperty("think", out _));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("off", true)]
    [InlineData("OFF", true)]
    [InlineData("model", false)]
    public async Task A_machines_client_follows_its_nodes_thinking_setting(string? thinking, bool toldNotToThink)
    {
        var ollama = new FakeOllama("{\"models\":[]}");
        IChatClient client = OllamaNodeClient.Create(new Uri("http://node:11434/v1"), "m", null, 32768, TimeSpan.FromSeconds(30),
            NullLogger.Instance, thinking ?? NodeThinking.Off, ollama);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "write the file")]);

        JsonElement body = Json(Only(ollama, "/api/chat").Body);
        Assert.Equal(toldNotToThink, body.TryGetProperty("think", out JsonElement think) && !think.GetBoolean());
    }

    [Fact]
    public async Task A_choice_the_caller_already_made_is_not_overwritten()
    {
        var options = new ChatOptions { RawRepresentationFactory = _ => new OllamaSharp.Models.Chat.ChatRequest { Think = true } };

        JsonElement body = Json(await ChatBodyAsync(thinkingOff: true, options));

        Assert.True(body.GetProperty("think").GetBoolean());
    }

    [Fact]
    public void A_node_saved_without_a_thinking_choice_is_off_and_a_wrong_choice_is_refused()
    {
        FleetConfigStore store = ConfigStore();
        FleetNodeConfig hub = new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);

        FleetConfig saved = store.Save(store.Current with { Nodes = [hub, new("worker-a", "http://w.example.test/v1", "m", "worker", "standard", Thinking: " MODEL ")] });
        Assert.Null(saved.Nodes[0].Thinking);
        Assert.Equal("model", saved.Nodes[1].Thinking);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            store.Save(store.Current with { Nodes = [hub, new("worker-a", "http://w.example.test/v1", "m", "worker", "standard", Thinking: "banana")] }));
        Assert.Contains("thinking", refused.Message);
        Assert.Contains("worker-a", refused.Message);

        Assert.Equal(NodeThinking.Off, NodeThinking.Normalize(null));
        Assert.Equal(NodeThinking.Model, NodeThinking.Normalize("model"));
    }
}
