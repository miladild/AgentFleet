using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace AgentFleet.Tests;

/// <summary>
/// How long a round spent reading the prompt and how long writing the answer, from what Ollama reports at the end of each reply.
/// Rounds of 8 to 30 minutes on a model that is fast per token suggest the prompt is read again at every call; these numbers say.
/// </summary>
public sealed class OllamaTimingsTests
{
    private const string Done =
        "{\"model\":\"m\",\"created_at\":\"2026-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"done\":true,\"done_reason\":\"stop\"," +
        "\"total_duration\":5000000000,\"load_duration\":1000000000,\"prompt_eval_count\":1200,\"prompt_eval_duration\":3000000000,\"eval_count\":20,\"eval_duration\":800000000}";

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Done + "\n", Encoding.UTF8, "application/x-ndjson") });
    }

    private static IChatClient Client() =>
        new OllamaApiClient(new HttpClient(new Handler()) { BaseAddress = new Uri("http://node:11434/") }, "m");

    private static readonly ChatMessage[] Question = [new(ChatRole.User, "hello")];

    [Fact]
    public async Task The_timings_are_read_from_a_whole_reply()
    {
        ChatResponse response = await Client().GetResponseAsync(Question);

        OllamaCallTiming timing = Assert.NotNull(OllamaTimings.From(response.RawRepresentation));
        Assert.Equal((1200, 3.0, 20, 0.8, 1.0, 5.0), (timing.PromptTokens, timing.PromptEvalSeconds, timing.EvalTokens, timing.EvalSeconds, timing.LoadSeconds, timing.TotalSeconds));
    }

    [Fact]
    public async Task The_timings_are_read_from_the_last_update_of_a_streamed_reply()
    {
        OllamaCallTiming? timing = null;
        await foreach (ChatResponseUpdate update in Client().GetStreamingResponseAsync(Question))
        {
            timing = OllamaTimings.From(update.RawRepresentation) ?? timing;
        }

        Assert.NotNull(timing);
        Assert.Equal(1200, timing.Value.PromptTokens);
        Assert.Equal(0.8, timing.Value.EvalSeconds);
    }

    [Fact]
    public void Anything_that_is_not_an_ollama_reply_has_no_timings()
    {
        Assert.Null(OllamaTimings.From(null));
        Assert.Null(OllamaTimings.From("text"));
    }

    [Fact]
    public void A_round_adds_up_its_calls_and_says_where_the_time_went()
    {
        var activity = new StepToolActivity();
        Assert.Null(activity.DescribeModelCalls());

        activity.RecordModelCall(new OllamaCallTiming(1200, 3.0, 20, 0.8, 1.0, 5.0));
        activity.RecordModelCall(new OllamaCallTiming(1200, 3.0, 20, 0.8, 0.0, 4.0));

        Assert.Equal(2, activity.ModelCalls);
        Assert.Equal("2 model calls: prompt 2400 tokens in 6.0 s (400.0 tokens/s), answer 40 tokens in 1.6 s (25.0 tokens/s), model load 1.0 s, total 9.0 s.",
            activity.DescribeModelCalls());
    }

    [Fact]
    public async Task A_step_rounds_calls_are_counted_through_the_node_telemetry_client_whole_or_streamed()
    {
        var activity = new StepToolActivity();
        var options = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [StepToolActivity.Key] = activity } };
        var client = new NodeTelemetryChatClient(Client(), "node", "m");

        await client.GetResponseAsync(Question, options);
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(Question, options))
        {
        }

        Assert.Equal(2, activity.ModelCalls);
        Assert.StartsWith("2 model calls: prompt 2400 tokens in 6.0 s", activity.DescribeModelCalls(), StringComparison.Ordinal);
        await client.GetResponseAsync(Question);
        Assert.Equal(2, activity.ModelCalls);
    }
}
