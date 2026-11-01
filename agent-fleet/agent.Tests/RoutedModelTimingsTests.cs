using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;

namespace AgentFleet.Tests;

/// <summary>
/// The per-round timings reach a plan step's round through the same chain a live step uses: the router takes the step's key off
/// the options before the node sees them, and the node's own wrappers sit between the router and Ollama. A first version recorded
/// them below the router, saw no key there, and a whole run logged no timings.
/// </summary>
public sealed class RoutedModelTimingsTests : ContextTestBase
{
    private const string Chunk =
        "{\"model\":\"m\",\"created_at\":\"2026-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"done\":false}\n";

    private const string Done =
        "{\"model\":\"m\",\"created_at\":\"2026-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"," +
        "\"total_duration\":5000000000,\"load_duration\":1000000000,\"prompt_eval_count\":1200,\"prompt_eval_duration\":3000000000,\"eval_count\":20,\"eval_duration\":800000000}\n";

    // Some servers put the last words and the totals in one line.
    private const string DoneWithText =
        "{\"model\":\"m\",\"created_at\":\"2026-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"done\":true,\"done_reason\":\"stop\"," +
        "\"total_duration\":5000000000,\"load_duration\":1000000000,\"prompt_eval_count\":1200,\"prompt_eval_duration\":3000000000,\"eval_count\":20,\"eval_duration\":800000000}\n";

    private sealed class OllamaHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") });
    }

    private sealed class TagsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Post
                    ? "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}"
                    : "{\"models\":[{\"name\":\"test-model:latest\"}]}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TagsHandler());
    }

    private FleetRoutingChatClient NewRouter(string body = Chunk + Done)
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
        // The node as a live one is built: a rescue wrapper over the Ollama client.
        IChatClient node = new ToolCallRescueChatClient(
            new OllamaApiClient(new HttpClient(new OllamaHandler(body)) { BaseAddress = new Uri("http://node:11434/") }, "m"), NullLogger.Instance);
        return new FleetRoutingChatClient(
            node,
            [new FleetRouteTarget(options.Nodes[0], node)],
            new FleetHealthMonitor(options, new Factory()),
            new FleetModeService(configStore),
            new FleetPlanModeService(configStore),
            new FleetPlanStore(configuration),
            new HashSet<string>(),
            new FleetActivityLog(),
            NullLogger.Instance,
            Journal);
    }

    private static readonly ChatMessage[] Question = [new(ChatRole.User, "hello")];

    private static ChatOptions StepOptions(StepToolActivity activity, bool withTools)
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [FleetRoutingChatClient.RunnerTierKey] = "standard",
                [StepToolActivity.Key] = activity
            }
        };
        if (withTools)
        {
            options.Tools = [AIFunctionFactory.Create(string () => "ok", "noop", "A tool.")];
        }

        return options;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_streamed_call_through_the_router_is_counted_in_the_steps_round(bool withTools)
    {
        var activity = new StepToolActivity();
        using IDisposable scope = Scope(NewContextId());

        await foreach (ChatResponseUpdate _ in NewRouter().GetStreamingResponseAsync(Question, StepOptions(activity, withTools)))
        {
        }

        Assert.Equal(1, activity.ModelCalls);
        Assert.StartsWith("1 model call: prompt 1200 tokens in 3.0 s", activity.DescribeModelCalls(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_last_line_that_carries_both_words_and_totals_is_still_counted_when_the_tool_rescue_holds_text_back()
    {
        var activity = new StepToolActivity();
        using IDisposable scope = Scope(NewContextId());

        await foreach (ChatResponseUpdate _ in NewRouter(DoneWithText).GetStreamingResponseAsync(Question, StepOptions(activity, withTools: true)))
        {
        }

        Assert.Equal(1, activity.ModelCalls);
    }

    [Fact]
    public async Task A_whole_call_through_the_router_is_counted_in_the_steps_round()
    {
        var activity = new StepToolActivity();
        using IDisposable scope = Scope(NewContextId());

        await NewRouter().GetResponseAsync(Question, StepOptions(activity, withTools: true));

        Assert.Equal(1, activity.ModelCalls);
    }

    [Fact]
    public async Task A_call_without_a_step_round_is_not_counted_and_does_not_fail()
    {
        using IDisposable scope = Scope(NewContextId());

        await foreach (ChatResponseUpdate _ in NewRouter().GetStreamingResponseAsync(Question,
                           new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = "standard" } }))
        {
        }
    }
}
