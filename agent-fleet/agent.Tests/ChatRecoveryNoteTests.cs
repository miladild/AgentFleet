using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class ChatRecoveryNoteTests : ContextTestBase
{
    private sealed class CaptureClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? Input { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Input = messages.ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Input = messages.ToArray();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_and_plan_runner_calls_do_not_get_a_note(bool enabled, bool planRunner)
    {
        string id = NewContextId();
        var capture = new CaptureClient();
        var client = new ChatRecoveryNoteClient(capture, Journal, enabled);
        ChatOptions options = new();
        if (planRunner) options.AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = "heavy" };
        using (Scope(id))
        {
            Journal.RecordToolExecution("lost", "action", "{}", "result", false);
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "continue")], options);
        }
        Assert.DoesNotContain(capture.Input!, message => message.Text?.Contains("already ran earlier", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Note_lists_last_ten_lost_calls_and_cuts_arguments_and_results()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            for (int index = 0; index < 12; index++)
                Journal.RecordToolExecution($"call-{index}", $"action-{index}", new string('a', 5000), new string('r', 5000), false);

            var capture = new CaptureClient();
            await new ChatRecoveryNoteClient(capture, Journal, true)
                .GetResponseAsync([new ChatMessage(ChatRole.User, "continue")]);
            string note = Assert.Single(capture.Input!, message => message.Text?.Contains("already ran earlier", StringComparison.Ordinal) == true).Text!;
            Assert.DoesNotContain("action-0", note, StringComparison.Ordinal);
            Assert.DoesNotContain("- action-1;", note, StringComparison.Ordinal);
            Assert.Contains("action-2", note, StringComparison.Ordinal);
            Assert.Equal(10, note.Split("- action-", StringSplitOptions.None).Length - 1);
            string line = Assert.Single(note.Split('\n'), line => line.Contains("action-11", StringComparison.Ordinal));
            Assert.Equal(200, line.Split("; arguments: ", StringSplitOptions.None)[1].Split("; result: ", StringSplitOptions.None)[0].Length);
            Assert.Equal(300, line.Split("; result: ", StringSplitOptions.None)[1].Length);
        }
    }

    [Fact]
    public async Task Call_id_matching_is_exact()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            Journal.RecordToolExecution("Call-Id", "action", "{}", "result", false);
            var capture = new CaptureClient();
            var transcript = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-id", "action", new Dictionary<string, object?>())]);
            await new ChatRecoveryNoteClient(capture, Journal, true).GetResponseAsync([transcript]);
            Assert.Contains(capture.Input!, message => message.Text?.Contains("already ran earlier", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task A_transcript_with_all_recorded_ids_is_identical_when_enabled_or_disabled()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            Journal.RecordToolExecution("call", "action", "{}", "result", false);
            ChatMessage[] transcript = [
                new(ChatRole.Assistant, [new FunctionCallContent("call", "action", new Dictionary<string, object?>())]),
                new(ChatRole.Tool, [new FunctionResultContent("call", "result")]),
                new(ChatRole.User, "continue")
            ];
            var enabled = new CaptureClient();
            var disabled = new CaptureClient();
            await new ChatRecoveryNoteClient(enabled, Journal, true).GetResponseAsync(transcript);
            await new ChatRecoveryNoteClient(disabled, Journal, false).GetResponseAsync(transcript);
            Assert.Equal(disabled.Input, enabled.Input);
        }
    }

    [Fact]
    public async Task A_long_chat_reads_a_bounded_number_of_events_and_the_note_names_only_the_latest_lost_calls()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            const int executions = 1000;
            for (int index = 0; index < executions; index++)
                Journal.RecordToolExecution($"call-{index}", $"action-{index}", "{}", $"result-{index}", false);

            IReadOnlyList<FleetContextEvent> read = ChatRecoveryNoteClient.RecentToolEvents(Store, id);
            Assert.True(read.Count <= 400, $"The note read {read.Count} events for one run; the bound is 400.");

            // The transcript carries every execution except the three most recent, which the client dropped.
            ChatMessage[] transcript = [.. Enumerable.Range(0, executions - 3).SelectMany(index => new[]
            {
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{index}", $"action-{index}", new Dictionary<string, object?>())]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{index}", $"result-{index}")])
            }), new ChatMessage(ChatRole.User, "continue")];
            var capture = new CaptureClient();
            await new ChatRecoveryNoteClient(capture, Journal, true).GetResponseAsync(transcript);

            string note = Assert.Single(capture.Input!, message => message.Text?.Contains("already ran earlier", StringComparison.Ordinal) == true).Text!;
            string[] lines = note.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("- action-997; ", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("- action-998; ", lines[1], StringComparison.Ordinal);
            Assert.StartsWith("- action-999; ", lines[2], StringComparison.Ordinal);
            Assert.EndsWith("; result: result-999", lines[2], StringComparison.Ordinal);
            Assert.DoesNotContain("action-996", note, StringComparison.Ordinal);
        }
    }
}
