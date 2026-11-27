using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A model server given more than its window holds drops the oldest messages without saying so, the task among them
/// (measured: a prompt of 23000 tokens sent to a 16384 window was answered from 369 of them). The fleet therefore asks the
/// server not to shorten anything, and shortens the conversation itself when the server refuses it.
/// </summary>
public sealed class ContextRefusalTests
{
    private const string TooLong =
        "{\"error\":\"the prompt is longer than the context length currently available to the model; shorten the prompt, adjust the context length in settings, or use a model with a longer context length\"}";

    private const string Answer =
        "{\"model\":\"m\",\"created_at\":\"2026-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"done reading\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":900,\"eval_count\":3}";

    private sealed class Server(Func<JsonElement, bool> refuses, string refusal = TooLong) : HttpMessageHandler
    {
        public List<JsonElement> Chats { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/chat")
            {
                return Reply(HttpStatusCode.OK, "{}");
            }

            JsonElement body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Chats.Add(body);
            return refuses(body) ? Reply(HttpStatusCode.BadRequest, refusal) : Reply(HttpStatusCode.OK, Answer);
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json + "\n", Encoding.UTF8, "application/json") };
    }

    private static IChatClient Client(Server server, int contextLength = 16384) =>
        OllamaNodeClient.Create(new Uri("http://node:11434/v1"), "m", null, contextLength, TimeSpan.FromSeconds(30),
            NullLogger.Instance, NodeThinking.Off, server);

    private static int Characters(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray().Sum(message => message.TryGetProperty("content", out JsonElement content) ? content.GetString()?.Length ?? 0 : 0);

    private static string[] Contents(JsonElement body) =>
        body.GetProperty("messages").EnumerateArray().Select(message => message.TryGetProperty("content", out JsonElement content) ? content.GetString() ?? string.Empty : string.Empty).ToArray();

    private static int Window(JsonElement body) => body.GetProperty("options").GetProperty("num_ctx").GetInt32();

    // The newest tool result alone is far past a 16384 window, and the usual shortening never touches the newest turn.
    private static List<ChatMessage> HugeNewestResult() =>
    [
        new(ChatRole.System, "You carry out one plan step."),
        new(ChatRole.User, "KEY-TASK Implement the holidays."),
        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c0", "read_file", new Dictionary<string, object?> { ["path"] = "src/a.ts" })]),
        new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c0", "FIRST-LINE " + new string('r', 120_000) + " LAST-LINE")])
    ];

    [Fact]
    public async Task Every_chat_request_asks_the_server_not_to_shorten_the_prompt()
    {
        var server = new Server(_ => false);
        IChatClient client = Client(server);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
        }

        Assert.Equal(2, server.Chats.Count);
        Assert.All(server.Chats, body => Assert.False(body.GetProperty("truncate").GetBoolean()));
        Assert.All(server.Chats, body => Assert.False(body.GetProperty("think").GetBoolean()));
    }

    [Fact]
    public async Task A_refused_prompt_is_shortened_and_sent_again_with_the_task_still_in_it()
    {
        var server = new Server(body => Characters(body) > 32_000);
        List<ChatMessage> conversation = HugeNewestResult();

        ChatResponse response = await Client(server).GetResponseAsync(conversation);

        Assert.Equal("done reading", response.Text);
        Assert.Equal(2, server.Chats.Count);
        string[] sent = Contents(server.Chats[^1]);
        Assert.Contains("KEY-TASK Implement the holidays.", sent);
        // The library sends a tool result wrapped in JSON, which escapes the marker's apostrophe.
        string result = sent[^1];
        Assert.Contains("FIRST-LINE rr", result, StringComparison.Ordinal);
        Assert.Contains("rr LAST-LINE", result, StringComparison.Ordinal);
        Assert.Contains("[the rest was removed to keep this conversation inside the model", result, StringComparison.Ordinal);
        Assert.Equal(120_021, ((FunctionResultContent)conversation[3].Contents[0]).Result!.ToString()!.Length); // the caller's list is untouched
    }

    [Fact]
    public async Task A_refused_streamed_prompt_is_shortened_and_sent_again()
    {
        var server = new Server(body => Characters(body) > 32_000);
        var text = new StringBuilder();

        await foreach (ChatResponseUpdate update in Client(server).GetStreamingResponseAsync(HugeNewestResult()))
        {
            text.Append(update.Text);
        }

        Assert.Equal("done reading", text.ToString());
        Assert.Equal(2, server.Chats.Count);
        Assert.Contains("KEY-TASK Implement the holidays.", Contents(server.Chats[^1]));
    }

    [Fact]
    public async Task A_prompt_refused_below_the_largest_window_is_first_sent_again_whole_with_the_largest_window()
    {
        var server = new Server(body => Window(body) < 32768);
        List<ChatMessage> conversation = [new(ChatRole.User, new string('x', 10_000))];

        await Client(server, contextLength: 32768).GetResponseAsync(conversation);

        Assert.Equal([8192, 32768], server.Chats.Select(Window).ToArray());
        Assert.Equal(10_000, Characters(server.Chats[^1]));
    }

    [Fact]
    public async Task A_server_that_keeps_refusing_ends_in_its_own_error_after_a_few_tries()
    {
        var server = new Server(_ => true);

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => Client(server).GetResponseAsync(HugeNewestResult()));

        Assert.Contains("longer than the context length", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(server.Chats.Count, 2, 5);
        Assert.All(server.Chats, body => Assert.Contains(Contents(body), content => content.StartsWith("KEY-TASK", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_refusal_an_older_server_words_differently_is_handled_the_same()
    {
        const string older =
            "{\"error\":\"{\\\"error\\\":{\\\"code\\\":400,\\\"message\\\":\\\"request (10889 tokens) exceeds the available context size (8192 tokens), try increasing it\\\",\\\"type\\\":\\\"exceed_context_size_error\\\"}}\"}";
        var server = new Server(body => Characters(body) > 32_000, older);

        ChatResponse response = await Client(server).GetResponseAsync(HugeNewestResult());

        Assert.Equal("done reading", response.Text);
        // This server's count says far less is too much than its limit does, so a first cut by the count is not enough.
        Assert.InRange(server.Chats.Count, 2, 3);
    }

    [Fact]
    public async Task Another_bad_request_is_not_sent_again()
    {
        var server = new Server(_ => true, "{\"error\":\"invalid options\"}");

        await Assert.ThrowsAnyAsync<Exception>(() => Client(server).GetResponseAsync(HugeNewestResult()));

        Assert.Single(server.Chats);
    }

    [Fact]
    public void The_task_is_cut_only_when_cutting_everything_else_is_not_enough()
    {
        // The task is the longest text, and shortening the tool result alone is enough to fit.
        string task = "TASK " + new string('t', 20_000);
        List<ChatMessage> conversation =
        [
            new(ChatRole.System, new string('s', 3_000)),
            new(ChatRole.User, task),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c0", "read_file", new Dictionary<string, object?> { ["path"] = "src/a.ts" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c0", new string('r', 15_000))])
        ];

        IReadOnlyList<ChatMessage> cut = ContextSizeChatClient.CutToFit(conversation, null, 11_000);

        Assert.True(ContextSizeChatClient.EstimateTokens(cut, null) <= 11_000);
        Assert.Equal(conversation[0].Text, cut[0].Text);
        Assert.Equal(task, cut[1].Text);
        Assert.Same(conversation[2], cut[2]);
        Assert.Contains(ContextSizeChatClient.RemovedMarker, ((FunctionResultContent)cut[3].Contents[0]).Result!.ToString()!, StringComparison.Ordinal);
        Assert.Equal(15_000, ((FunctionResultContent)conversation[3].Contents[0]).Result!.ToString()!.Length);
    }

    [Fact]
    public async Task A_task_that_is_too_big_by_itself_keeps_its_start_and_its_end()
    {
        var server = new Server(body => Characters(body) > 32_000);
        List<ChatMessage> conversation = [new(ChatRole.System, "You carry out one plan step."), new(ChatRole.User, "TASK-START " + new string('t', 150_000) + " TASK-END")];

        await Client(server).GetResponseAsync(conversation);

        string task = Contents(server.Chats[^1])[^1];
        Assert.StartsWith("TASK-START", task, StringComparison.Ordinal);
        Assert.EndsWith("TASK-END", task, StringComparison.Ordinal);
        Assert.Contains(ContextSizeChatClient.RemovedMarker, task, StringComparison.Ordinal);
    }
}
