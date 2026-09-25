using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class OvernightTests
{
    private static FleetConfigStore Store(bool planMode)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            { "triageModel": "t", "mode": "conservative", "planModeEnabled": {{(planMode ? "true" : "false")}},
              "nodes": [{ "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "m", "purpose": "", "fallback": true }], "tools": {} }
            """);
        return new FleetConfigStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = path }).Build());
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void A_chat_can_ask_for_plan_mode_for_itself_whatever_the_switch_says(bool globalSwitch, bool? requested, bool expected)
    {
        var requestContext = new FleetRequestContext();
        var service = new FleetPlanModeService(Store(globalSwitch), requestContext);

        using (requestContext.Push(new FleetRequestIdentity(Guid.NewGuid().ToString("D"), null, "vscode", PlanMode: requested)))
        {
            Assert.Equal(expected, service.Effective);
        }

        Assert.Equal(globalSwitch, service.Effective);
    }

    [Fact]
    public async Task The_plan_mode_request_is_read_from_the_run()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/";
        byte[] body = System.Text.Encoding.UTF8.GetBytes($$"""
            { "threadId": "{{Guid.NewGuid():D}}", "runId": "r", "messages": [], "forwardedProps": { "fleetSurface": "vscode", "fleetPlanMode": true } }
            """);
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;

        FleetRunRequest? run = await FleetRequestContext.ReadRunAsync(context.Request, default);

        Assert.True(run!.Identity.PlanMode);
        Assert.Equal("vscode", run.Identity.Surface);
    }

    private sealed class FailingFirst(Exception error) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw error;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw error;
#pragma warning disable CS0162 // An iterator needs a yield to be one.
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Answers(params string[] words) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(words))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (string word in words)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static (ResilientChatClient Client, FleetHealthMonitor Health) Resilient(IChatClient worker, IChatClient fallback)
    {
        FleetConfigStore store = Store(false);
        FleetOptions options = FleetOptions.Load(new ConfigurationBuilder().Build(), store);
        var health = new FleetHealthMonitor(options, new NoHttp());
        FleetNodeDefinition node = options.Nodes[0];
        // Seen as ready, so the worker is tried first.
        health.MarkUnavailable(node.Name, "x");
        typeof(FleetHealthMonitor).GetField("_snapshots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(health)!.GetType().GetMethod("set_Item")!
            .Invoke(typeof(FleetHealthMonitor).GetField("_snapshots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(health),
                [node.Name, new NodeHealthSnapshot(node.Name, node.Model, true, true, DateTimeOffset.UtcNow, null)]);
        return (new ResilientChatClient(worker, node, health, NullLogger.Instance, fallback), health);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task A_chat_whose_machine_fails_before_its_first_word_is_answered_by_the_fallback()
    {
        (ResilientChatClient client, FleetHealthMonitor health) = Resilient(
            new FailingFirst(new HttpRequestException("connection refused")), new Answers("from ", "the hub"));

        var words = new List<string>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            words.Add(update.Text);
        }

        Assert.Equal("from the hub", string.Concat(words));
        Assert.False((await health.GetNodeAsync("hub")).Ready);
    }

    [Fact]
    public async Task A_healthy_machine_streams_as_before() =>
        Assert.Equal("a b", string.Concat(await Collect(Resilient(new Answers("a", " b"), new Answers("wrong")).Client)));

    private static async Task<List<string>> Collect(IChatClient client)
    {
        var words = new List<string>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            words.Add(update.Text);
        }

        return words;
    }

    [Fact]
    public async Task A_machine_that_timed_out_rests_even_though_its_probe_answers()
    {
        FleetConfigStore store = Store(false);
        FleetOptions options = FleetOptions.Load(new ConfigurationBuilder().Build(), store);
        var health = new FleetHealthMonitor(options, new NoHttp());

        health.CoolDown("hub", "slow", TimeSpan.FromMinutes(10));

        NodeHealthSnapshot probed = await health.GetNodeAsync("hub", forceProbe: true);
        Assert.False(probed.Ready);
        Assert.Equal("slow", probed.Failure);

        health.Forget();
        Assert.NotEqual("slow", (await health.GetNodeAsync("hub", forceProbe: true)).Failure);
    }
}
