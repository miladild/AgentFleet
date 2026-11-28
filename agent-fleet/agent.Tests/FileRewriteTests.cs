using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class FileRewriteTests
{
    [Fact]
    public void A_reply_gives_the_files_it_holds_whole_and_leaves_out_a_cut_off_one()
    {
        string reply = "Sure.\nFILE: ./src\\a.ts\n```ts\nexport const a = 1;\n\nexport const b = 2;\n```\n\nFILE: `test/a.test.ts`\n\n```\nimport x;\n```\n" +
                       "FILE: src/cut.ts\n```ts\nexport const half =";

        IReadOnlyList<RewriteFile> files = FileRewrite.Parse(reply);

        Assert.Equal(["src/a.ts", "test/a.test.ts"], files.Select(file => file.Path));
        Assert.Equal("export const a = 1;\n\nexport const b = 2;\n", files[0].Text);
        Assert.Equal("import x;\n", files[1].Text);
    }

    [Fact]
    public void A_reply_without_a_file_block_gives_nothing() =>
        Assert.Empty(FileRewrite.Parse("The bug is in the loop. FILE: names the file but there is no block."));

    [Fact]
    public void A_path_written_twice_takes_its_last_block() =>
        Assert.Equal("two\n", Assert.Single(FileRewrite.Parse("FILE: a.ts\n```\none\n```\nFILE: a.ts\n```\ntwo\n```\n")).Text);

    [Fact]
    public void The_request_holds_the_task_the_files_what_failed_and_what_was_tried()
    {
        var step = new PlanStep(1, "Alert store", "Store the alerts.", ["src/a.ts", "test/a.test.ts"], "npm test", "light", StepStatus.Pending,
            null, 0, null, null);

        string prompt = FileRewrite.Prompt(step, [new("src/a.ts", "code"), new("test/a.test.ts", null)], [new("src/types.ts", "types")],
            "FAILED one", ["round 1 changed src/a.ts"]);

        Assert.StartsWith("The task:", prompt, StringComparison.Ordinal);
        Assert.Contains("Store the alerts.", prompt, StringComparison.Ordinal);
        Assert.Contains("FILE: src/a.ts\n```\ncode\n```", prompt.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("test/a.test.ts (does not exist yet)", prompt, StringComparison.Ordinal);
        Assert.Contains("to read only", prompt, StringComparison.Ordinal);
        Assert.Contains("FAILED one", prompt, StringComparison.Ordinal);
        Assert.Contains("round 1 changed src/a.ts", prompt, StringComparison.Ordinal);
        Assert.EndsWith("your reply starts with 'FILE:'.", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" src/a.ts\n```ts\nx\n```")] // The answer goes on from the start the fleet wrote.
    [InlineData("FILE: src/a.ts\n```ts\nx\n```")] // The server started it again.
    [InlineData("\nFILE: src/a.ts\n```ts\nx\n```")]
    public void A_reply_that_goes_on_from_the_started_answer_or_starts_it_again_gives_the_file(string reply)
    {
        RewriteFile file = Assert.Single(FileRewrite.Parse(FileRewrite.Continued(reply)));

        Assert.Equal("src/a.ts", file.Path);
        Assert.Equal("x\n", file.Text);
    }

    [Fact]
    public void Only_a_rewrite_request_has_its_answer_started()
    {
        List<ChatMessage> messages = [new(ChatRole.User, "The task: fix it")];

        IReadOnlyList<ChatMessage> started = FleetRoutingChatClient.StartAnswer(PlanRunnerToolPolicy.FileRewriteRole, messages);

        Assert.Equal(2, started.Count);
        Assert.Equal(ChatRole.Assistant, started[^1].Role);
        Assert.Equal(FileRewrite.AnswerStart, started[^1].Text);
        Assert.Same(messages, FleetRoutingChatClient.StartAnswer(null, messages));
        Assert.Same(messages, FleetRoutingChatClient.StartAnswer(PlanRunnerToolPolicy.TestRulingRole, messages));
    }

    [Fact]
    public void A_stage_that_keeps_rewriting_one_file_is_told_to_look_at_the_others()
    {
        var step = new PlanStep(1, "Alert store", "Store the alerts.", ["src/a.ts", "test/a.test.ts"], "npm test", "light", StepStatus.Pending,
            null, 0, null, null);

        string stuck = FileRewrite.Prompt(step, [new("src/a.ts", "code")], [], "FAILED one", [], onlyRewritten: "test/a.test.ts");

        Assert.Contains("rewrote only test/a.test.ts", stuck, StringComparison.Ordinal);
        Assert.DoesNotContain("rewrote only", FileRewrite.Prompt(step, [new("src/a.ts", "code")], [], "FAILED one", []), StringComparison.Ordinal);
        Assert.EndsWith("your reply starts with 'FILE:'.", stuck, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rewrite_is_offered_no_tool()
    {
        foreach (string tool in new[] { "read_file", "write_file", "run_command", "report_blocker", "web_search" })
        {
            Assert.False(PlanRunnerToolPolicy.Offers(tool, fromRunner: true, PlanRunnerToolPolicy.FileRewriteRole, webOffered: true));
        }
    }

    [Theory]
    [InlineData(16384, 20000)]
    [InlineData(24576, 20000)]
    [InlineData(32768, 20000)]
    [InlineData(8192, 10240)]
    [InlineData(65536, 0)]
    [InlineData(null, 0)]
    public void Only_a_machine_with_a_known_small_or_medium_window_rewrites_files_and_only_ones_that_fit(int? window, int most) =>
        Assert.Equal(most, MachineProfile.RewriteFileBudget(window));

    private sealed class Show : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    private sealed class Nothing : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_request_for_a_longer_answer_gets_a_window_with_room_for_it()
    {
        var client = new ContextSizeChatClient(new Nothing(), new HttpClient(new Show()) { BaseAddress = new Uri("http://node:11434/") }, "m", 32768,
            NullLogger.Instance);
        List<ChatMessage> prompt = [new(ChatRole.User, new string('x', 16_000))]; // About 6 400 tokens by estimate.

        ChatOptions usual = await client.WithContextAsync(prompt, null, default);
        ChatOptions longer = await client.WithContextAsync(prompt, new ChatOptions { MaxOutputTokens = FileRewrite.AnswerTokens }, default);

        Assert.Equal(12288, usual.AdditionalProperties!["num_ctx"]);
        Assert.Equal(16384, longer.AdditionalProperties!["num_ctx"]);
        Assert.Equal(FileRewrite.AnswerTokens, longer.MaxOutputTokens);
    }
}
