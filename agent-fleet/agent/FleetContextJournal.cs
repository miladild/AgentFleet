using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// What the running code writes into the durable record: which machine got a call, what it said,
/// which tools ran with what arguments and result, and what context each model was handed. Every
/// method does nothing when there is no context in scope (a request without a GUID thread id) and
/// never throws: the record is valuable, but a full disk must not break a chat.
/// </summary>
internal sealed class FleetContextJournal
{
    private const int MaxToolArgumentCharacters = 2000;
    private const int MaxToolResultCharacters = 2000;
    private const int MaxAssistantCharacters = 4000;
    private const int MaxInjectedCharacters = 4500;

    private readonly FleetRequestContext _requestContext;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _lastDelivered = new(StringComparer.Ordinal);

    public FleetContextJournal(FleetContextStore store, FleetRequestContext requestContext, ILogger logger)
    {
        Store = store;
        _requestContext = requestContext;
        _logger = logger;
    }

    public FleetContextStore Store { get; }

    public FleetRequestIdentity? Current => _requestContext.Current;

    public string? ActualNode => _requestContext.ActualNode;

    public IDisposable Push(FleetRequestIdentity identity) => _requestContext.Push(identity);

    public void RecordRoute(string node, string? tier, string phase, string reason)
    {
        _requestContext.SetActualNode(node);
        Try("route", identity => Store.AppendEvent(
            identity.ContextId, FleetContextEventKind.Route, new { node, tier, phase, reason, surface = identity.Surface },
            identity.TaskId, actor: "router", agent: "fleet", node: node));
    }

    public void RecordAssistantOutput(string? node, string text, int toolCalls) =>
        Try("assistant output", identity =>
        {
            if (string.IsNullOrWhiteSpace(text) && toolCalls == 0)
            {
                return;
            }

            Store.AppendEvent(
                identity.ContextId,
                FleetContextEventKind.AssistantOutput,
                new { text = Clip(text, MaxAssistantCharacters), length = text.Length, toolCalls, node },
                identity.TaskId, actor: "assistant", agent: "fleet", node: node);
        });

    public long? RecordError(string? node, string source, string message, string? partialOutput = null)
    {
        FleetRequestIdentity? identity = Current;
        if (identity is null)
        {
            return null;
        }

        try
        {
            return Store.AppendEvent(
                identity.ContextId,
                FleetContextEventKind.Error,
                new { source, message = Clip(message, 1200), partialOutput = Clip(partialOutput, 1200) },
                identity.TaskId, actor: "fleet", agent: "fleet-router", node: node);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record an error in context {ContextId}.", identity.ContextId);
            return null;
        }
    }

    /// <summary>Records an extra system note that a model received after the main context was assembled.</summary>
    public void RecordContextAddition(string? node, string text, IReadOnlyList<long>? eventIds = null) =>
        Try("context addition", identity => Store.AppendEvent(
            identity.ContextId,
            FleetContextEventKind.ContextAssembled,
            new
            {
                node,
                taskId = identity.TaskId,
                text = Clip(text, MaxInjectedCharacters),
                eventIds = eventIds ?? [],
                artifactIds = Array.Empty<string>(),
                truncated = false,
                supplemental = true
            },
            identity.TaskId, actor: "fleet", agent: "context-assembler", node: node));

    /// <summary>A tool call that the backend ran, with what it was given and what came back.</summary>
    public void RecordToolExecution(string? callId, string name, string argumentsJson, string result, bool isError) =>
        Try("tool call", identity =>
        {
            string id = string.IsNullOrEmpty(callId) ? Guid.NewGuid().ToString("N") : callId;
            Store.AppendEvent(
                identity.ContextId, FleetContextEventKind.ToolCall,
                new { callId = id, name, arguments = Clip(argumentsJson, MaxToolArgumentCharacters) },
                identity.TaskId, actor: "assistant", agent: "fleet");
            Store.AppendEvent(
                identity.ContextId, FleetContextEventKind.ToolResult,
                new
                {
                    callId = id,
                    name,
                    result = Clip(result, MaxToolResultCharacters),
                    length = result.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result))).ToLowerInvariant(),
                    isError
                },
                identity.TaskId, actor: "tool", agent: "fleet");
            if (isError)
            {
                Store.AppendEvent(
                    identity.ContextId,
                    FleetContextEventKind.Error,
                    new { source = "tool", tool = name, message = Clip(result, MaxToolResultCharacters) },
                    identity.TaskId, actor: "tool", agent: "fleet");
            }
        });

    /// <summary>Pins a decision or constraint so it is handed to every agent that continues this work.</summary>
    public long? RecordDecision(string text, string? reason, string category = "decision")
    {
        FleetRequestIdentity? identity = Current;
        if (identity is null)
        {
            return null;
        }

        try
        {
            // Small models pin the same fact again and again. An identical one is not added twice.
            string wanted = Normalize(text);
            FleetContextEvent? same = Store.EventsOfKinds(identity.ContextId, [FleetContextEventKind.Decision], 200, pinnedOnly: true)
                .FirstOrDefault(e => Normalize(ContextText.String(e.Payload, "text") ?? string.Empty) == wanted);
            if (same is not null)
            {
                return same.Id;
            }

            return Store.AppendEvent(
                identity.ContextId, FleetContextEventKind.Decision,
                new { text = Clip(text, 600), reason = string.IsNullOrWhiteSpace(reason) ? null : Clip(reason, 400), category },
                identity.TaskId, actor: "assistant", agent: "fleet", pinned: true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record a decision in context {ContextId}.", identity.ContextId);
            return null;
        }
    }

    /// <summary>
    /// The durable context to put in front of the model for this call, or null when there is none worth
    /// sending. What was handed over is recorded, so the inspector can show exactly what an agent received.
    /// </summary>
    public string? BuildInjection(string? node, string? stylePrompt = null)
    {
        FleetRequestIdentity? identity = Current;
        if (identity is null)
        {
            return stylePrompt;
        }

        try
        {
            ContextAssemblyPreview preview = ContextAssembler.Assemble(
                Store, new ContextAssemblyRequest(identity.ContextId, identity.TaskId, MaxCharacters: MaxInjectedCharacters, IncludeTranscript: identity.Joined));
            string? client = ClientContextItem.Describe(identity.ClientContext);
            if (client is not null)
            {
                preview = preview with { Text = string.IsNullOrWhiteSpace(preview.Text) ? client : $"{preview.Text}\n\n{client}" };
            }

            if (!string.IsNullOrWhiteSpace(stylePrompt))
            {
                preview = preview with { Text = string.IsNullOrWhiteSpace(preview.Text) ? stylePrompt : $"{preview.Text}\n\n{stylePrompt}" };
            }

            if (string.IsNullOrWhiteSpace(preview.Text))
            {
                return null;
            }

            // The same block goes out on every model call of a tool loop; record it when it changes.
            string key = $"{identity.ContextId}|{identity.TaskId}|{node}";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Text)));
            if (!_lastDelivered.TryGetValue(key, out string? last) || last != hash)
            {
                _lastDelivered[key] = hash;
                Store.AppendEvent(
                    identity.ContextId, FleetContextEventKind.ContextAssembled,
                    new { node, taskId = identity.TaskId, text = preview.Text, eventIds = preview.EventIds, artifactIds = preview.ArtifactIds, truncated = preview.WasTruncated },
                    identity.TaskId, actor: "fleet", agent: "context-assembler", node: node);
            }

            return preview.Text;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not assemble the durable context for {ContextId}.", identity.ContextId);
            long? errorEvent = RecordError(null, "context assembly", exception.Message);
            string warning = "Fleet could not load durable context for this call. Do not assume earlier decisions, checks, or handoffs were included; inspect the current files and ask the user if needed.";
            string delivered = string.IsNullOrWhiteSpace(stylePrompt) ? warning : $"{warning}\n\n{stylePrompt}";
            RecordContextAddition(node, delivered, errorEvent is { } id ? [id] : []);
            return delivered;
        }
    }

    private void Try(string what, Action<FleetRequestIdentity> action)
    {
        FleetRequestIdentity? identity = Current;
        if (identity is null)
        {
            return;
        }

        try
        {
            action(identity);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record {What} in context {ContextId}.", what, identity.ContextId);
        }
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ';', '!').ToLowerInvariant();

    private static string Clip(string? text, int limit)
    {
        string value = text ?? string.Empty;
        return value.Length <= limit ? value : value[..limit] + "...";
    }
}

/// <summary>Puts the assembled context in front of a model call, after the leading system messages.</summary>
internal static class ContextInjection
{
    public static IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> Apply(
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        string? injection)
    {
        if (string.IsNullOrWhiteSpace(injection))
        {
            return messages;
        }

        int at = 0;
        while (at < messages.Count && messages[at].Role == Microsoft.Extensions.AI.ChatRole.System)
        {
            at++;
        }

        var result = new List<Microsoft.Extensions.AI.ChatMessage>(messages.Count + 1);
        result.AddRange(messages.Take(at));
        result.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, injection));
        result.AddRange(messages.Skip(at));
        return result;
    }
}
