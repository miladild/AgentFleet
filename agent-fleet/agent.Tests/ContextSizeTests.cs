using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class ContextSizeTests
{
    private sealed class Show(string json) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Nothing : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static ContextSizeChatClient Client(string showJson, int wanted, out Show handler)
    {
        handler = new Show(showJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://node:11434/") };
        return new ContextSizeChatClient(new Nothing(), http, "m", wanted, NullLogger.Instance);
    }

    private static IReadOnlyList<ChatMessage> Text(int characters) => [new ChatMessage(ChatRole.User, new string('x', characters))];

    [Fact]
    public async Task A_request_asks_for_what_it_needs_in_a_few_fixed_sizes()
    {
        ContextSizeChatClient client = Client("""{ "model_info": { "qwen3moe.context_length": 262144 } }""", 32768, out Show show);

        ChatOptions small = await client.WithContextAsync(Text(3_000), new ChatOptions { Temperature = 0.2f }, default);
        Assert.Equal(8192, small.AdditionalProperties!["num_ctx"]);
        Assert.Equal(0.2f, small.Temperature);

        ChatOptions big = await client.WithContextAsync(Text(60_000), null, default); // about 20000 tokens plus room to answer
        Assert.Equal(24576, big.AdditionalProperties!["num_ctx"]);
        Assert.Equal(1, show.Calls); // The model's limit is looked up once.
    }

    [Fact]
    public async Task A_size_once_used_is_kept_so_the_model_is_not_reloaded_down_and_up()
    {
        ContextSizeChatClient client = Client("{}", 32768, out _);

        await client.WithContextAsync(Text(60_000), null, default);
        ChatOptions next = await client.WithContextAsync(Text(100), null, default);

        Assert.Equal(24576, next.AdditionalProperties!["num_ctx"]);
    }

    [Fact]
    public async Task The_machine_maximum_and_the_model_window_cap_the_size()
    {
        ContextSizeChatClient configured = Client("{}", 16384, out _);
        Assert.Equal(16384, (await configured.WithContextAsync(Text(200_000), null, default)).AdditionalProperties!["num_ctx"]);

        ContextSizeChatClient model = Client(
            """{ "model_info": { "gemma3.rope.scaling.original_context_length": 8192, "gemma3.context_length": 12288 } }""", 32768, out _);
        Assert.Equal(12288, (await model.WithContextAsync(Text(200_000), null, default)).AdditionalProperties!["num_ctx"]);
    }

    [Fact]
    public async Task The_tool_list_counts_towards_what_a_request_needs()
    {
        ContextSizeChatClient client = Client("{}", 32768, out _);
        AITool[] tools = Enumerable.Range(0, 30).Select(i => (AITool)AIFunctionFactory.Create(() => "x", $"tool_{i}", new string('d', 1000))).ToArray();

        ChatOptions sized = await client.WithContextAsync(Text(100), new ChatOptions { Tools = tools }, default);

        Assert.Equal(16384, sized.AdditionalProperties!["num_ctx"]);
    }

    [Fact]
    public async Task An_answer_is_capped_so_a_looping_model_cannot_hold_a_machine()
    {
        ContextSizeChatClient client = Client("{}", 32768, out _);

        Assert.Equal(4096, (await client.WithContextAsync(Text(100), null, default)).MaxOutputTokens);
        Assert.Equal(ContextSizeChatClient.MaxAnswerTokens, (await client.WithContextAsync(Text(60_000), null, default)).MaxOutputTokens);
        Assert.Equal(100, (await client.WithContextAsync(Text(100), new ChatOptions { MaxOutputTokens = 100 }, default)).MaxOutputTokens);
    }

    [Fact]
    public async Task A_caller_that_sets_its_own_size_keeps_it()
    {
        ContextSizeChatClient client = Client("{}", 32768, out _);
        var options = new ChatOptions { AdditionalProperties = new() { ["num_ctx"] = 4096 } };

        Assert.Equal(4096, (await client.WithContextAsync(Text(100_000), options, default)).AdditionalProperties!["num_ctx"]);
    }
    [Theory]
    [InlineData(null, OllamaNodeClient.DefaultContextLength)]
    [InlineData("16384", 16384)]
    [InlineData("12", OllamaNodeClient.DefaultContextLength)]
    [InlineData("lots", OllamaNodeClient.DefaultContextLength)]
    public void The_default_can_be_set_by_environment(string? value, int expected) =>
        Assert.Equal(expected, OllamaNodeClient.ConfiguredDefault(value));

    [Fact]
    public void An_ollama_failure_is_an_outage_but_a_model_without_tools_is_not()
    {
        Assert.True(PlanRunner.IsTransient(new OllamaSharp.Models.Exceptions.OllamaException("llama runner process has terminated")));
        Assert.False(PlanRunner.IsTransient(new OllamaSharp.Models.Exceptions.ModelDoesNotSupportToolsException("does not support tools")));
    }
}