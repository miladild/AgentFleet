using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// Works around a known, currently-open Ollama bug (ollama/ollama#18530, #18563):
/// its qwen3-coder tool-call parser only recognizes a literal &lt;tool_call&gt;
/// opening tag, and under some prompt conditions (confirmed here: a long system
/// prompt, independent of how many tools are offered) the model omits it - so
/// Ollama gives up parsing and the raw
/// &lt;function=name&gt;&lt;parameter=x&gt;value&lt;/parameter&gt;&lt;/function&gt; text comes back
/// as plain assistant content with finish_reason "stop" instead of a structured
/// tool call. The tool silently never runs.
///
/// This decorator detects that exact leaked-text shape and repairs it into a real
/// FunctionCallContent, so the rest of the pipeline (ChatClientAgent's automatic
/// function-invocation loop) picks it up and executes it normally - same as if
/// Ollama had parsed it correctly in the first place. It only looks when the
/// request actually offered tools, and only acts on the specific leaked-marker
/// pattern, so it's a no-op for every node/model that isn't hitting this bug
/// (confirmed: some other models, such as rnj-1, never trip it).
/// </summary>
internal sealed class ToolCallRescueChatClient : DelegatingChatClient
{
    private const string Marker = "<function=";
    private const int HoldbackLength = 9; // Marker.Length - 1: how much a chunk boundary could split

    private static readonly Regex FunctionPattern = new(
        @"<function=(?<name>[A-Za-z0-9_]+)>(?<body>.*)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParameterPattern = new(
        @"<parameter=(?<name>[A-Za-z0-9_]+)>\s*(?<value>.*?)\s*(?=</parameter>|<parameter=|</function>|$)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ILogger _logger;

    public ToolCallRescueChatClient(IChatClient innerClient, ILogger logger) : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await InnerClient.GetResponseAsync(messages, options, cancellationToken);

        if (options?.Tools is not { Count: > 0 })
        {
            return response;
        }

        foreach (ChatMessage message in response.Messages)
        {
            if (message.Contents.Any(content => content is FunctionCallContent))
            {
                continue;
            }

            FunctionCallContent? rescued = TryRescue(message.Text);
            if (rescued is null)
            {
                continue;
            }

            LogRescue(rescued.Name);
            message.Contents = new List<AIContent> { rescued };
            response.FinishReason = ChatFinishReason.ToolCalls;
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options?.Tools is not { Count: > 0 })
        {
            await foreach (ChatResponseUpdate update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        // Chars withheld from forwarding because they might be the start of a marker
        // that got split across two stream chunks - released once proven harmless.
        string carry = "";
        bool suppressing = false;
        var suppressedRaw = new StringBuilder();
        ChatResponseUpdate? lastUpdate = null;

        await foreach (ChatResponseUpdate update in InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            lastUpdate = update;
            string deltaText = update.Text;

            if (string.IsNullOrEmpty(deltaText))
            {
                // Not a text delta (e.g. a real, already-structured tool call from a
                // well-behaved node/model) - nothing to rescue. Flush any held-back
                // text first so nothing is silently dropped, then pass this through.
                if (carry.Length > 0)
                {
                    yield return new ChatResponseUpdate(update.Role, carry)
                    {
                        ResponseId = update.ResponseId,
                        MessageId = update.MessageId
                    };
                    carry = "";
                }

                yield return update;
                continue;
            }

            if (suppressing)
            {
                suppressedRaw.Append(deltaText);
                continue;
            }

            string combined = carry + deltaText;
            int markerIndex = combined.IndexOf(Marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                string safePrefix = combined[..markerIndex];
                if (safePrefix.Length > 0)
                {
                    yield return new ChatResponseUpdate(update.Role, safePrefix)
                    {
                        ResponseId = update.ResponseId,
                        MessageId = update.MessageId
                    };
                }

                suppressing = true;
                suppressedRaw.Append(combined[markerIndex..]);
                carry = "";
                continue;
            }

            if (combined.Length <= HoldbackLength)
            {
                carry = combined;
                continue;
            }

            string toForward = combined[..^HoldbackLength];
            carry = combined[^HoldbackLength..];
            yield return new ChatResponseUpdate(update.Role, toForward)
            {
                ResponseId = update.ResponseId,
                MessageId = update.MessageId
            };
        }

        if (!suppressing)
        {
            if (carry.Length > 0)
            {
                yield return new ChatResponseUpdate(lastUpdate?.Role, carry)
                {
                    ResponseId = lastUpdate?.ResponseId,
                    MessageId = lastUpdate?.MessageId
                };
            }

            yield break;
        }

        string rawTail = suppressedRaw.ToString();
        FunctionCallContent? rescued = TryRescue(rawTail);
        if (rescued is null)
        {
            // Couldn't parse it after all - forward the raw text so the failure is at
            // least visible instead of silently disappearing.
            _logger.LogWarning(
                "Detected a leaked tool-call marker ('<function=') but could not parse it as a tool call; forwarding the raw text instead.");
            yield return new ChatResponseUpdate(lastUpdate?.Role, rawTail)
            {
                ResponseId = lastUpdate?.ResponseId,
                MessageId = lastUpdate?.MessageId
            };
            yield break;
        }

        LogRescue(rescued.Name);
        yield return new ChatResponseUpdate(lastUpdate?.Role, new List<AIContent> { rescued })
        {
            ResponseId = lastUpdate?.ResponseId,
            MessageId = lastUpdate?.MessageId,
            FinishReason = ChatFinishReason.ToolCalls
        };
    }

    private void LogRescue(string toolName) =>
        _logger.LogWarning(
            "Rescued a leaked tool call ({ToolName}) that Ollama's parser failed to structure - see ollama/ollama#18530.",
            toolName);

    internal static FunctionCallContent? TryRescue(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        Match functionMatch = FunctionPattern.Match(text);
        if (!functionMatch.Success)
        {
            return null;
        }

        string name = functionMatch.Groups["name"].Value;
        string body = functionMatch.Groups["body"].Value;

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (Match parameterMatch in ParameterPattern.Matches(body))
        {
            arguments[parameterMatch.Groups["name"].Value] = parameterMatch.Groups["value"].Value.Trim();
        }

        return new FunctionCallContent(
            callId: $"rescued-{Guid.NewGuid():N}",
            name: name,
            arguments: arguments);
    }
}
