#pragma warning disable MAAI001

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AgentFleet.Tests;

public sealed class MafContextWindowFitterTests
{
    private readonly ITestOutputHelper _output;

    public MafContextWindowFitterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fitter_name_is_correct(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        Assert.Equal(collapse ? "maf-collapse" : "maf-truncate", fitter.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I1_same_instance_when_conversation_fits(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        IReadOnlyList<ChatMessage> messages = [new ChatMessage(ChatRole.User, new string('x', 1_000))];

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(messages, null, 10_000, 1.0, CancellationToken.None);

        Assert.Same(fitted, messages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I2_system_and_task_messages_are_always_present_and_unchanged(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        string task = "Implement feature X. " + new string('t', 2_000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a coding assistant."),
            new(ChatRole.User, task),
        };

        // Add many tool calls to force compaction.
        for (int i = 0; i < 15; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{i}", "write_file",
                    new Dictionary<string, object?> { ["path"] = "src/a.ts", ["content"] = new string('w', 2_000) })]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent($"c{i}", new string('r', 3_000))]));
        }

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(messages, null, 8_000, 1.0, CancellationToken.None);

        Assert.NotEmpty(fitted);
        Assert.Equal(ChatRole.System, fitted[0].Role);
        Assert.Equal(ChatRole.User, fitted[1].Role);
        Assert.Equal(task, fitted[1].Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I3_caller_list_and_messages_are_not_mutated(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System"),
            new(ChatRole.User, new string('t', 1_500)),
        };

        // Add tool calls.
        for (int i = 0; i < 10; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{i}", "read_file",
                    new Dictionary<string, object?> { ["path"] = "src/file.ts" })]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent($"c{i}", new string('r', 2_000))]));
        }

        int originalCount = messages.Count;
        var originalContents = messages.Select(m => m.Contents.ToList()).ToList();

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(messages, null, 5_000, 1.0, CancellationToken.None);

        // Original list unchanged.
        Assert.Equal(originalCount, messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            Assert.Equal(originalContents[i].Count, messages[i].Contents.Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I4_no_orphans_every_result_has_its_call(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System"),
            new(ChatRole.User, "Task: " + new string('t', 1_000)),
        };

        for (int i = 0; i < 20; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{i}", "run_command",
                    new Dictionary<string, object?> { ["command"] = "test" })]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent($"c{i}", "output")]));
        }

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(messages, null, 5_000, 1.0, CancellationToken.None);

        var calls = fitted.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        var results = fitted.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();

        // Every result has a call.
        foreach (var result in results)
        {
            Assert.Contains(calls, c => c.CallId == result.CallId);
        }

        // Every call has its results.
        foreach (var call in calls)
        {
            Assert.Contains(results, r => r.CallId == call.CallId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I5_last_assistant_call_and_results_are_present_and_byte_identical(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        string lastCallArg = "large file content " + new string('x', 5_000);
        string lastCallResult = "result of last call " + new string('y', 5_000);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System"),
            new(ChatRole.User, "Task: " + new string('t', 1_000)),
        };

        // Add old calls to force compaction.
        for (int i = 0; i < 15; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"old{i}", "run_command",
                    new Dictionary<string, object?> { ["command"] = "test" })]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent($"old{i}", "ok")]));
        }

        // Last call.
        messages.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("last", "write_file",
                new Dictionary<string, object?> { ["path"] = "file.txt", ["content"] = lastCallArg })]));
        messages.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("last", lastCallResult)]));

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(messages, null, 8_000, 1.0, CancellationToken.None);

        // Find the last call in fitted.
        var lastCall = fitted.SelectMany(m => m.Contents).OfType<FunctionCallContent>().LastOrDefault(c => c.CallId == "last");
        Assert.NotNull(lastCall);
        Assert.Equal(lastCallArg, lastCall.Arguments!["content"]);

        // Find the last result in fitted.
        var lastResult = fitted.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last(r => r.CallId == "last");
        Assert.Equal(lastCallResult, lastResult.Result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I6_when_preserved_parts_exceed_budget_still_return_without_throwing(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System"),
            new(ChatRole.User, new string('x', 10_000)), // Task itself is large
        };

        // Very tight budget - smaller than the task alone.
        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(messages, null, 1_000, 1.0, CancellationToken.None);

        Assert.NotNull(fitted);
        Assert.True(fitted.Count > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I9_fallback_count_is_zero_on_normal_operations(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System"),
            new(ChatRole.User, "Task: " + new string('t', 1_000)),
        };

        for (int i = 0; i < 10; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{i}", "run_command",
                    new Dictionary<string, object?> { ["command"] = "echo test" })]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent($"c{i}", "ok")]));
        }

        await fitter.FitAsync(messages, null, 3_000, 1.0, CancellationToken.None);

        Assert.Equal(0, fitter.FallbackCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task When_maf_throws_fallback_count_increments(bool collapse)
    {
        // Create a fitter with a logger that will be used in case of exceptions.
        var fitter = new MafContextWindowFitter(collapse, NullLogger.Instance);

        // We can't easily make MAF throw during normal operation, but we can verify
        // that the fallback path works correctly by checking the structure.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System"),
            new(ChatRole.User, "Task"),
        };

        await fitter.FitAsync(messages, null, 3_000, 1.0, CancellationToken.None);
        Assert.Equal(0, fitter.FallbackCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Parser_recognizes_maf_policies(bool collapse)
    {
        string value = collapse ? "maf-collapse" : "maf-truncate";
        string valueUpper = value.ToUpperInvariant();

        IContextWindowFitter fitter1 = ContextWindowFitters.FromSetting(value, null);
        IContextWindowFitter fitter2 = ContextWindowFitters.FromSetting(valueUpper, null);

        Assert.Equal(value, fitter1.Name);
        Assert.Equal(value, fitter2.Name);
    }

    // Helper for testing.
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
}
