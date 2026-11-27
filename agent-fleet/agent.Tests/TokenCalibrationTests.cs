using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// The size of a prompt is estimated before it is sent, and the server then says how big it really was. Measured over 236
/// requests of one plan: the estimate was 1.41 times the real size (1.29 to 1.50), so a machine with a 16384-token window had its
/// conversations shortened at about half of it. The client learns the ratio from the server's own counts.
/// </summary>
public sealed class TokenCalibrationTests
{
    private sealed class NoShow : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    // Answers every call and reports the prompt size the test sets.
    private sealed class Counts : IChatClient
    {
        public long Next;
        public List<IReadOnlyList<ChatMessage>> Sent { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")) { Usage = new UsageDetails { InputTokenCount = Next } });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static ContextSizeChatClient Client(IChatClient inner, int window = 16384) =>
        new(inner, new HttpClient(new NoShow()) { BaseAddress = new Uri("http://node:11434/") }, "m", window, NullLogger.Instance);

    // 2.5 characters are one token by estimate.
    private static IReadOnlyList<ChatMessage> Estimated(int tokens) => [new ChatMessage(ChatRole.User, new string('x', tokens * 5 / 2))];

    private static async Task AskAsync(ContextSizeChatClient client, Counts server, int estimated, long real)
    {
        server.Next = real;
        await client.GetResponseAsync(Estimated(estimated));
    }

    [Fact]
    public async Task A_count_below_the_estimate_lets_a_later_conversation_keep_what_it_had_to_lose_before()
    {
        var server = new Counts();
        ContextSizeChatClient client = Client(server);
        await AskAsync(client, server, 4000, 2800);

        Assert.InRange(client.Scale, 0.75, 0.78);

        // About 14 000 tokens by estimate: more than the 12 288 left beside an answer until the real size is known.
        List<ChatMessage> conversation = [new(ChatRole.System, "You carry out one plan step."), new(ChatRole.User, "Implement the cache.")];
        for (int round = 0; round < 7; round++)
        {
            conversation.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{round}", "run_command", new Dictionary<string, object?> { ["command"] = "node --test" })]));
            conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{round}", new string('r', 4_900))]));
        }

        int estimate = ContextSizeChatClient.EstimateTokens(conversation, null);
        Assert.InRange(estimate, 13_000, 14_500);
        server.Next = estimate * 7 / 10;

        await client.GetResponseAsync(conversation);

        Assert.Equal(conversation.Count, server.Sent[^1].Count);
        Assert.Equal(new string('r', 4_900), ((FunctionResultContent)server.Sent[^1][3].Contents[0]).Result);
    }

    [Fact]
    public async Task The_highest_recent_count_decides_and_an_old_one_is_forgotten()
    {
        var server = new Counts();
        ContextSizeChatClient client = Client(server);
        await AskAsync(client, server, 4000, 2800);
        await AskAsync(client, server, 4000, 3600);

        Assert.InRange(client.Scale, 0.96, 0.98);

        await AskAsync(client, server, 4000, 2800);
        Assert.InRange(client.Scale, 0.96, 0.98);

        for (int call = 0; call < 16; call++)
        {
            await AskAsync(client, server, 4000, 2800);
        }

        Assert.InRange(client.Scale, 0.75, 0.78);
    }

    [Fact]
    public async Task A_small_prompt_teaches_nothing_since_the_servers_own_wrapping_is_most_of_it()
    {
        var server = new Counts();
        ContextSizeChatClient client = Client(server);

        await AskAsync(client, server, 500, 900);
        await AskAsync(client, server, 500, 100);

        Assert.Equal(1.0, client.Scale);
    }

    [Fact]
    public async Task The_scale_never_goes_below_its_floor()
    {
        var server = new Counts();
        ContextSizeChatClient client = Client(server);

        await AskAsync(client, server, 4000, 1000);

        Assert.Equal(0.75, client.Scale);
    }

    [Fact]
    public async Task A_server_that_reports_no_count_changes_nothing()
    {
        var server = new Counts();
        ContextSizeChatClient client = Client(server);

        await AskAsync(client, server, 4000, 0);

        Assert.Equal(1.0, client.Scale);
    }

    // Refuses a request whose real size, at 3.5 characters a token, is past the window, and says how big it was.
    private sealed class Refusing : HttpMessageHandler
    {
        public List<int> Characters { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/chat")
            {
                return Reply(HttpStatusCode.OK, "{}");
            }

            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            int characters = body.RootElement.GetProperty("messages").EnumerateArray()
                .Sum(message => message.TryGetProperty("content", out JsonElement content) ? content.GetString()?.Length ?? 0 : 0);
            Characters.Add(characters);
            int tokens = characters * 2 / 7;
            return tokens > 16384
                ? Reply(HttpStatusCode.BadRequest, $"{{\"error\":\"request ({tokens} tokens) exceeds the available context size (16384 tokens), try increasing it\"}}")
                : Reply(HttpStatusCode.OK, $"{{\"model\":\"m\",\"created_at\":\"2026-01-01T00:00:00Z\",\"message\":{{\"role\":\"assistant\",\"content\":\"done\"}},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":{tokens},\"eval_count\":3}}");
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json + "\n", Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task A_refusal_that_says_how_big_the_prompt_was_is_cut_to_what_fits_and_no_further()
    {
        var server = new Refusing();
        IChatClient client = OllamaNodeClient.Create(new Uri("http://node:11434/v1"), "m", null, 16384, TimeSpan.FromSeconds(30),
            NullLogger.Instance, NodeThinking.Off, server);

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "You carry out one plan step."),
            new ChatMessage(ChatRole.User, "KEY-TASK Implement the holidays."),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c0", "read_file", new Dictionary<string, object?> { ["path"] = "src/a.ts" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c0", new string('r', 70_000))])
        ]);

        Assert.Equal(2, server.Characters.Count);
        // 12 288 tokens are left beside an answer: at the real 3.5 characters a token that is 43 000 characters. Cutting to
        // the estimate alone left under 31 000.
        Assert.InRange(server.Characters[^1], 36_000, 43_000);
    }

    [Theory]
    [InlineData(16384, 24000, 2730)]
    [InlineData(24576, 24000, 4096)]
    [InlineData(32768, 3000, 3000)]
    [InlineData(2048, 24000, 1000)]
    public void The_repair_packet_gets_a_share_of_the_machines_window(int window, int configured, int expected) =>
        Assert.Equal(expected, MachineProfile.PacketTokenBudget(window, configured));

    [Theory]
    [InlineData(16384, 12288)]
    [InlineData(4096, 8000)]
    [InlineData(131072, 48000)]
    public void The_files_sent_with_a_step_are_limited_by_the_machines_window(int window, int expected) =>
        Assert.Equal(expected, MachineProfile.StepFileCharacterBudget(window, 48000));

    [Fact]
    public void A_machine_with_no_window_set_keeps_the_usual_limits()
    {
        Assert.Equal(24000, MachineProfile.PacketTokenBudget(null, 24000));
        Assert.Equal(48000, MachineProfile.StepFileCharacterBudget(null, 48000));
    }
}
