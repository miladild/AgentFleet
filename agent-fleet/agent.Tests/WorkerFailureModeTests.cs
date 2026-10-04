using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

/// <summary>
/// Failures measured on the two workers in the first benchmark with real models. A conversation that outgrew a small window
/// had its old file writes cut with a marker, and the model copied the marker into the next file it wrote. A worker's reply
/// was thrown away after five minutes of silence although it was only a long write on a slow machine.
/// </summary>
public sealed class WorkerFailureModeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-failure-" + Guid.NewGuid().ToString("N"));

    public WorkerFailureModeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static List<ChatMessage> LongWriteConversation(string task, int rounds)
    {
        List<ChatMessage> conversation = [new(ChatRole.System, "You carry out one plan step."), new(ChatRole.User, task)];
        for (int round = 0; round < rounds; round++)
        {
            conversation.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{round}", "write_file",
                new Dictionary<string, object?> { ["path"] = "src/a.ts", ["content"] = new string('w', 20_000) })]));
            conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{round}", new string('r', 20_000))]));
        }

        return conversation;
    }

    [Fact]
    public void A_call_that_stays_in_a_shortened_conversation_is_whole_because_the_model_copies_its_shape()
    {
        List<ChatMessage> conversation = LongWriteConversation("Implement the holidays.", rounds: 3);

        IReadOnlyList<ChatMessage> fitted = ContextSizeChatClient.FitToWindow(conversation, null, 28_672, 1.0);

        // Measured on a worker: shown its earlier write_file calls without their content, or with a placeholder in it, its
        // next write_file had no content or the placeholder as its content, 15 times in 15.
        FunctionCallContent[] calls = fitted.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        Assert.NotEmpty(calls);
        Assert.All(calls, call =>
        {
            Assert.Equal(new string('w', 20_000), call.Arguments!["content"]);
            Assert.DoesNotContain(call.Arguments.Values, value => value?.ToString()?.Contains("removed to keep", StringComparison.Ordinal) == true);
        });
        Assert.True(calls.Length < 3, "the oldest write is left out, with its result, instead of being shown without its content");
        ContextSizeTests.AssertCallsAreWholeAndAnswered(fitted, conversation);
    }

    [Fact]
    public void A_long_old_result_that_stays_still_says_that_something_was_cut()
    {
        var conversation = new List<ChatMessage>
        {
            new(ChatRole.System, "x"),
            new(ChatRole.User, "task"),
            new(ChatRole.Assistant, "I will look at the files first. " + new string('t', 3_000)),
            new(ChatRole.Assistant, "More thinking. " + new string('u', 3_000)),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "run_command", new Dictionary<string, object?> { ["command"] = "npm test" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            new(ChatRole.Assistant, [new FunctionCallContent("c2", "run_command", new Dictionary<string, object?> { ["command"] = "npm test" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c2", "ok")]),
            new(ChatRole.Assistant, [new FunctionCallContent("c3", "run_command", new Dictionary<string, object?> { ["command"] = "npm test" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c3", "ok")])
        };

        IReadOnlyList<ChatMessage> fitted = ContextSizeChatClient.FitToWindow(conversation, null, 1_500, 1.0);

        Assert.Contains(ContextSizeChatClient.RemovedMarker, fitted[2].Text); // an old long answer is cut, and says so
        Assert.Equal("task", fitted[1].Text);
    }

    [Fact]
    public async Task A_write_that_carries_the_history_placeholder_is_refused_and_writes_nothing()
    {
        string path = Path.Combine(_directory, "cron.test.js");

        string result = await WorkspaceTools.WriteFileAsync(path, "const assert = " + ContextSizeChatClient.RemovedMarker, default);

        Assert.StartsWith("Error: nothing was written", result);
        Assert.Contains(path, result);
        Assert.Contains("not part of any file", result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task An_edit_that_carries_the_history_placeholder_is_refused()
    {
        string path = Path.Combine(_directory, "a.js");
        File.WriteAllText(path, "const a = 1;");

        string result = await WorkspaceTools.EditFileAsync(path, "const a = 1;", "const a = " + ContextSizeChatClient.RemovedMarker, null, default);

        Assert.StartsWith("Error: nothing was written", result);
        Assert.Equal("const a = 1;", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("const rest = 'the rest was removed';", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a [the rest was removed to keep this conversation inside the model's window] b", true)]
    public void Only_the_real_placeholder_is_taken_for_one(string? text, bool expected)
    {
        Assert.Equal(expected, WorkspaceTools.CarriesHistoryPlaceholder(text));
    }

    private static ChatOptions WithHubParagraph() => new()
    {
        Instructions = "You are a local coding assistant." + Environment.NewLine + Environment.NewLine + HubPlatform.PathInstructions +
                       Environment.NewLine + Environment.NewLine + "Available tools: read_file, write_file."
    };

    [Fact]
    public void A_model_on_a_linux_worker_is_not_told_its_machine_is_the_hubs()
    {
        ChatOptions original = WithHubParagraph();

        ChatOptions? changed = WorkerPlatformChatClient.ForWorker(original, "linux");

        Assert.NotSame(original, changed);
        Assert.DoesNotContain(HubPlatform.PathInstructions, changed!.Instructions);
        Assert.Contains("Linux worker", changed.Instructions);
        Assert.Contains("/dev/null", changed.Instructions);
        Assert.StartsWith("You are a local coding assistant.", changed.Instructions);
        Assert.EndsWith("Available tools: read_file, write_file.", changed.Instructions);
        Assert.Contains(HubPlatform.PathInstructions, original.Instructions); // the caller's options are untouched
    }

    [Fact]
    public void A_model_on_a_windows_worker_is_told_about_that_worker()
    {
        ChatOptions? changed = WorkerPlatformChatClient.ForWorker(WithHubParagraph(), "Windows");

        Assert.Contains("Windows worker", changed!.Instructions);
        Assert.Contains("PowerShell", changed.Instructions);
        Assert.DoesNotContain(HubPlatform.PathInstructions, changed.Instructions);
    }

    [Fact]
    public void Outside_a_worker_step_or_without_the_hub_paragraph_the_options_are_left_alone()
    {
        ChatOptions chat = WithHubParagraph();
        Assert.Same(chat, WorkerPlatformChatClient.ForWorker(chat, null)); // ordinary chat: the tools act on the hub

        var other = new ChatOptions { Instructions = "No machine paragraph here." };
        Assert.Same(other, WorkerPlatformChatClient.ForWorker(other, "linux"));

        Assert.Null(WorkerPlatformChatClient.ForWorker(null, "linux"));
        var empty = new ChatOptions();
        Assert.Same(empty, WorkerPlatformChatClient.ForWorker(empty, "linux"));
    }

    [Theory]
    [InlineData(null, 900)]
    [InlineData("120", 120)]
    public void A_model_gets_fifteen_minutes_to_answer_unless_the_machine_says_otherwise(string? setting, int expectedSeconds)
    {
        var values = new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(_directory, "fleet.config.json") };
        if (setting is not null)
        {
            values["FLEET_NETWORK_TIMEOUT_SECONDS"] = setting;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var store = new FleetConfigStore(configuration);
        store.Save(store.Current with { Nodes = [new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true)] });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), FleetOptions.Load(configuration, store).NetworkTimeout);
    }

    [Fact]
    public void A_wait_longer_than_the_maximum_is_a_startup_error_not_a_silent_clamp()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLEET_CONFIG_PATH"] = Path.Combine(_directory, "fleet.config.json"),
            ["FLEET_NETWORK_TIMEOUT_SECONDS"] = "5000"
        }).Build();
        var store = new FleetConfigStore(configuration);
        store.Save(store.Current with { Nodes = [new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true)] });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => FleetOptions.Load(configuration, store));
        Assert.Contains("between 10 and 900", error.Message);
    }
}
