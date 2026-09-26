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

        ChatOptions big = await client.WithContextAsync(Text(50_000), null, default); // about 20000 tokens plus room to answer
        Assert.Equal(24576, big.AdditionalProperties!["num_ctx"]);
        Assert.Equal(1, show.Calls); // The model's limit is looked up once.
    }

    [Fact]
    public async Task A_size_once_used_is_kept_so_the_model_is_not_reloaded_down_and_up()
    {
        ContextSizeChatClient client = Client("{}", 32768, out _);

        await client.WithContextAsync(Text(50_000), null, default);
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
        AITool[] tools = Enumerable.Range(0, 30).Select(i => (AITool)AIFunctionFactory.Create(() => "x", $"tool_{i}", new string('d', 800))).ToArray();

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

    // Answers with a prompt size the way Ollama reports it, one reply per call.
    private sealed class Reports(params long[] promptTokens) : IChatClient
    {
        public readonly List<int> Sizes = [];
        public Action<IReadOnlyList<ChatMessage>>? Seen;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen?.Invoke(messages.ToList());
            Sizes.Add((int)options!.AdditionalProperties!["num_ctx"]!);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"answer {Sizes.Count}"))
            {
                Usage = new UsageDetails { InputTokenCount = promptTokens[Sizes.Count - 1] }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(response.Text), new UsageContent(response.Usage!)]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static ContextSizeChatClient Client(IChatClient inner) =>
        new(inner, new HttpClient(new Show("{}")) { BaseAddress = new Uri("http://node:11434/") }, "m", 32768, NullLogger.Instance);

    [Fact]
    public async Task A_prompt_that_filled_the_window_is_asked_again_with_a_bigger_one_and_later_prompts_get_more_room()
    {
        // 10000 characters is 4000 tokens by estimate, so 8192 is asked for; Ollama says the prompt filled it.
        var inner = new Reports(8000, 9000, 9000);
        ContextSizeChatClient client = Client(inner);

        ChatResponse answer = await client.GetResponseAsync(Text(10_000));

        Assert.Equal([8192, 16384], inner.Sizes);
        Assert.Equal("answer 2", answer.Text);
        Assert.True(client.Scale > 2);

        // The next request of the same size asks for the bigger window straight away.
        await client.GetResponseAsync(Text(10_000));
        Assert.Equal(16384, inner.Sizes[^1]);
    }

    [Fact]
    public async Task A_prompt_with_room_to_spare_is_not_asked_again_and_a_streamed_one_teaches_the_next_request()
    {
        var calm = new Reports(3000);
        await Client(calm).GetResponseAsync(Text(10_000));
        Assert.Equal([8192], calm.Sizes);

        var streamed = new Reports(8000, 9000);
        ContextSizeChatClient client = Client(streamed);
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(Text(10_000)))
        {
        }

        Assert.Equal([8192], streamed.Sizes); // too late to ask again
        await client.GetResponseAsync(Text(10_000));
        Assert.Equal(16384, streamed.Sizes[^1]);
    }

    [Fact]
    public async Task A_conversation_that_outgrows_the_window_loses_old_tool_output_but_never_its_task()
    {
        string task = "Implement the holidays. " + new string('t', 2_000);
        List<ChatMessage> conversation =
        [
            new(ChatRole.System, "You carry out one plan step."),
            new(ChatRole.User, task)
        ];
        for (int round = 0; round < 20; round++)
        {
            conversation.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{round}", "write_file", new Dictionary<string, object?> { ["path"] = "src/a.ts", ["content"] = new string('w', 3_000) })]));
            conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{round}", new string('r', 4_000))]));
        }

        var inner = new Reports(10_000);
        ContextSizeChatClient client = new(inner, new HttpClient(new Show("{}")) { BaseAddress = new Uri("http://node:11434/") }, "m", 32768, NullLogger.Instance);
        IReadOnlyList<ChatMessage> sent = null!;
        inner.Seen = messages => sent = messages;

        await client.GetResponseAsync(conversation);

        Assert.Equal(32768, inner.Sizes.Single());
        Assert.True(ContextSizeChatClient.EstimateTokens(sent, null) + 4096 <= 32768);
        Assert.Equal(conversation.Count, sent.Count); // every call keeps its result
        Assert.Equal(task, sent[1].Text);
        Assert.Equal(new string('r', 4_000), ((FunctionResultContent)sent[^1].Contents[0]).Result); // the latest turns stay whole
        Assert.Contains("removed to keep this conversation", ((FunctionResultContent)sent[3].Contents[0]).Result!.ToString());
        Assert.Equal(new string('r', 4_000), ((FunctionResultContent)conversation[3].Contents[0]).Result); // the caller's list is untouched
    }

    [Fact]
    public void Latest_turns_that_hold_whole_files_are_shortened_too_but_never_the_last_call_and_its_result()
    {
        string task = "Implement the holidays. " + new string('t', 2_000);
        List<ChatMessage> conversation = [new(ChatRole.System, "You carry out one plan step."), new(ChatRole.User, task)];
        for (int round = 0; round < 3; round++)
        {
            conversation.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{round}", "write_file", new Dictionary<string, object?> { ["path"] = "src/a.ts", ["content"] = new string('w', 20_000) })]));
            conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{round}", new string('r', 20_000))]));
        }

        // Three rounds are the six latest turns, and together they are about 48000 tokens.
        IReadOnlyList<ChatMessage> fitted = ContextSizeChatClient.FitToWindow(conversation, null, 28_672, 1.0);

        Assert.True(ContextSizeChatClient.EstimateTokens(fitted, null) <= 28_672);
        Assert.Equal(task, fitted[1].Text);
        Assert.Contains("removed to keep this conversation", ((FunctionResultContent)fitted[3].Contents[0]).Result!.ToString());
        Assert.Equal(new string('w', 20_000), ((FunctionCallContent)fitted[^2].Contents[0]).Arguments!["content"]);
        Assert.Equal(new string('r', 20_000), ((FunctionResultContent)fitted[^1].Contents[0]).Result);
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