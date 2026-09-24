using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class HotReloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-hot-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _configuration;
    private readonly FleetConfigStore _store;

    public HotReloadTests()
    {
        Directory.CreateDirectory(_dir);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(_dir, "fleet.config.json") })
            .Build();
        _store = new FleetConfigStore(_configuration);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Tags(string model) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"models\":[{{\"name\":\"{model}\"}}]}}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class Factory(string model) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Tags(model));
    }

    private FleetConfig WithNodes(params FleetNodeConfig[] nodes) => _store.Save(_store.Current with { Nodes = nodes });

    [Fact]
    public async Task A_machine_added_and_another_removed_are_seen_by_health_without_a_restart()
    {
        FleetOptions options = FleetOptions.Load(_configuration, _store);
        var health = new FleetHealthMonitor(options, new Factory("m:latest"));
        Assert.Equal(["hub"], options.Nodes.Select(n => n.Name));

        options.ReplaceNodes(WithNodes(
            new FleetNodeConfig("hub", "http://127.0.0.1:11434/v1", "m", "", "heavy", Fallback: true),
            new FleetNodeConfig("worker1", "http://10.0.0.5:11434", "m", "", "standard")));

        Assert.Equal(["hub", "worker1"], options.Nodes.Select(n => n.Name));
        Assert.Equal("http://10.0.0.5:11434/v1", options.GetNode("worker1").OpenAiEndpoint.ToString());
        Assert.True((await health.GetNodeAsync("worker1")).Ready);

        options.ReplaceNodes(WithNodes(new FleetNodeConfig("hub", "http://127.0.0.1:11434/v1", "m", "", "heavy", Fallback: true)));

        NodeHealthSnapshot gone = await health.GetNodeAsync("worker1");
        Assert.False(gone.Ready);
        Assert.Equal("not_configured", gone.Failure);
        health.MarkUnavailable("worker1", "timeout");
        Assert.Single(await health.GetAllAsync());
    }

    [Fact]
    public async Task A_changed_model_is_probed_afresh_after_forget_instead_of_serving_the_cached_answer()
    {
        FleetOptions options = FleetOptions.Load(_configuration, _store);
        var health = new FleetHealthMonitor(options, new Factory("old:latest"));
        options.ReplaceNodes(WithNodes(new FleetNodeConfig("hub", "http://127.0.0.1:11434/v1", "old", "", "heavy", Fallback: true)));
        Assert.True((await health.GetNodeAsync("hub")).Ready);

        options.ReplaceNodes(WithNodes(new FleetNodeConfig("hub", "http://127.0.0.1:11434/v1", "new", "", "heavy", Fallback: true)));
        health.Forget();

        NodeHealthSnapshot fresh = await health.GetNodeAsync("hub");
        Assert.Equal("new", fresh.Model);
        Assert.Equal("model_missing", fresh.Failure);
    }

    private sealed class Named(string text) : IChatClient
    {
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task After_a_swap_the_next_request_uses_the_new_router_and_the_old_one_is_not_cut_off()
    {
        var old = new Named("old");
        var swappable = new SwappableChatClient(old);
        Assert.Equal("old", (await swappable.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])).Text);

        swappable.Swap(new Named("new"));

        Assert.Equal("new", (await swappable.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])).Text);
        Assert.False(old.Disposed, "a request still streaming from the old router must be allowed to finish");
    }
}
