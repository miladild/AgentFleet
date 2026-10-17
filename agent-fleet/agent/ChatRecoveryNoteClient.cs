using Microsoft.Extensions.AI;

namespace AgentFleet;

/// <summary>Adds a per-call reminder for tool executions omitted from the current transcript.</summary>
internal sealed class ChatRecoveryNoteClient(IChatClient inner, FleetContextJournal journal, bool enabled) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(Prepare(messages, options), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(Prepare(messages, options), options, cancellationToken);

    private IEnumerable<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ChatMessage[] transcript = messages.ToArray();
        if (!enabled || journal.Current is not { } identity || options?.AdditionalProperties?.ContainsKey(FleetRoutingChatClient.RunnerTierKey) == true)
        {
            return transcript;
        }

        var seen = transcript.SelectMany(message => message.Contents.Select(content => content switch
        {
            FunctionCallContent call => call.CallId,
            FunctionResultContent result => result.CallId,
            _ => null
        })).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<FleetContextEvent> events = RecentToolEvents(journal.Store, identity.ContextId);
        var results = events.Where(e => e.Kind == FleetContextEventKind.ToolResult && ContextText.String(e.Payload, "callId") is not null)
            .GroupBy(e => ContextText.String(e.Payload, "callId")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var lost = events.Where(e => e.Kind == FleetContextEventKind.ToolCall && ContextText.String(e.Payload, "callId") is not null)
            .Where(e => !seen.Contains(ContextText.String(e.Payload, "callId")!))
            .TakeLast(10)
            .Select(e =>
            {
                string id = ContextText.String(e.Payload, "callId") ?? string.Empty;
                string name = ContextText.String(e.Payload, "name") ?? "unknown tool";
                string arguments = ContextText.String(e.Payload, "arguments") ?? "";
                string result = results.TryGetValue(id, out FleetContextEvent? item)
                    ? ContextText.String(item.Payload, "result") ?? ""
                    : "Result unavailable.";
                return $"- {name}; arguments: {OneLine(arguments, 200)}; result: {Clip(result, 300)}";
            }).ToArray();
        if (lost.Length == 0)
        {
            return transcript;
        }

        string note = "These tool calls already ran earlier in this conversation, but their results are not in the transcript:\n" +
                      string.Join('\n', lost) + "\nDo not repeat these calls unless the user asks.";
        int at = 0;
        while (at < transcript.Length && transcript[at].Role == ChatRole.System) at++;
        return transcript.Take(at).Append(new ChatMessage(ChatRole.System, note)).Concat(transcript.Skip(at)).ToArray();
    }

    /// <summary>
    /// The newest tool events of a context, oldest first. The store returns the latest rows of the kinds asked for, so a long chat
    /// costs the same to check on every run. 400 rows hold the newest 200 tool executions, far more than the ten the note lists.
    /// </summary>
    internal static IReadOnlyList<FleetContextEvent> RecentToolEvents(FleetContextStore store, string contextId) =>
        store.EventsOfKinds(contextId, [FleetContextEventKind.ToolCall, FleetContextEventKind.ToolResult], RecentEventLimit);

    private const int RecentEventLimit = 400;

    private static string OneLine(string value, int limit)
    {
        string line = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Clip(line, limit);
    }

    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
