using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class ContextWindowFitterTests
{
    private sealed class Show(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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

    private static IReadOnlyList<ChatMessage> Text(int characters) => [new ChatMessage(ChatRole.User, new string('x', characters))];

    [Fact]
    public void Classic_fitter_name_is_classic()
    {
        Assert.Equal("classic", ClassicContextWindowFitter.Instance.Name);
    }

    [Fact]
    public async Task Classic_fitter_returns_same_instance_when_conversation_fits()
    {
        IReadOnlyList<ChatMessage> messages = Text(1_000);
        IReadOnlyList<ChatMessage> fitted = await ClassicContextWindowFitter.Instance.FitAsync(messages, null, 10_000, 1.0, CancellationToken.None);

        Assert.Same(fitted, messages);
    }

    [Fact]
    public async Task Classic_fitter_with_context_size_client_behaves_as_before()
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
        ContextSizeChatClient client = new(inner, new HttpClient(new Show("{}")) { BaseAddress = new Uri("http://node:11434/") }, "m", 32768, NullLogger.Instance, fitter: ClassicContextWindowFitter.Instance);
        IReadOnlyList<ChatMessage> sent = null!;
        inner.Seen = messages => sent = messages;

        await client.GetResponseAsync(conversation);

        Assert.Single(inner.Sizes);
        Assert.True(ContextSizeChatClient.EstimateTokens(sent, null) + 4096 <= 32768);
        Assert.True(sent.Count < conversation.Count); // the oldest calls, each with its results, are left out
        Assert.Equal(task, sent[1].Text);
        ContextSizeTests.AssertCallsAreWholeAndAnswered(sent, conversation);
    }

    [Fact]
    public void Context_fitter_parser_recognizes_classic()
    {
        Assert.Same(ClassicContextWindowFitter.Instance, ContextWindowFitters.FromSetting("classic", null));
        Assert.Same(ClassicContextWindowFitter.Instance, ContextWindowFitters.FromSetting("CLASSIC", null));
        Assert.Same(ClassicContextWindowFitter.Instance, ContextWindowFitters.FromSetting("   classic   ", null));
    }

    [Fact]
    public void Context_fitter_parser_handles_null_and_empty()
    {
        Assert.Same(ClassicContextWindowFitter.Instance, ContextWindowFitters.FromSetting(null, null));
        Assert.Same(ClassicContextWindowFitter.Instance, ContextWindowFitters.FromSetting("", null));
        Assert.Same(ClassicContextWindowFitter.Instance, ContextWindowFitters.FromSetting("   ", null));
    }

    [Fact]
    public void Context_fitter_parser_recognizes_maf_truncate()
    {
        IContextWindowFitter fitter1 = ContextWindowFitters.FromSetting("maf-truncate", null);
        IContextWindowFitter fitter2 = ContextWindowFitters.FromSetting("MAF-TRUNCATE", null);
        IContextWindowFitter fitter3 = ContextWindowFitters.FromSetting("  maf-truncate  ", null);

        Assert.Equal("maf-truncate", fitter1.Name);
        Assert.Equal("maf-truncate", fitter2.Name);
        Assert.Equal("maf-truncate", fitter3.Name);
    }

    [Fact]
    public void Context_fitter_parser_recognizes_maf_collapse()
    {
        IContextWindowFitter fitter1 = ContextWindowFitters.FromSetting("maf-collapse", null);
        IContextWindowFitter fitter2 = ContextWindowFitters.FromSetting("MAF-COLLAPSE", null);
        IContextWindowFitter fitter3 = ContextWindowFitters.FromSetting("  maf-collapse  ", null);

        Assert.Equal("maf-collapse", fitter1.Name);
        Assert.Equal("maf-collapse", fitter2.Name);
        Assert.Equal("maf-collapse", fitter3.Name);
    }

    [Fact]
    public void Context_fitter_parser_defaults_unknown_to_classic_with_warning()
    {
        var logger = new ContextWindowTestHelper.CollectingLogger();
        IContextWindowFitter fitter = ContextWindowFitters.FromSetting("unknown-fitter", logger);

        Assert.Same(ClassicContextWindowFitter.Instance, fitter);
        Assert.NotEmpty(logger.Warnings);
        Assert.Contains("unknown-fitter", logger.Warnings[0]);
    }
}
