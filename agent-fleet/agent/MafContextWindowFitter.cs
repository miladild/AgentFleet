#pragma warning disable MAAI001

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet;

/// <summary>
/// Two context-window fitting policies built on Microsoft.Agents.AI's compaction library.
/// Both strategies remove oldest non-system groups and optionally collapse tool calls, keeping system messages
/// and the first user message (the task) whole. Compaction happens in quantized steps to keep the start of the
/// conversation unchanged between calls, so Ollama's prompt cache can help.
/// </summary>
internal sealed class MafContextWindowFitter : IContextWindowFitter
{
    private int _fallbackCount = 0;
    private int _fallbackLogged = 0;
    private readonly bool _collapseToolResults;
    private readonly ILogger _logger;
    private readonly Func<CompactionStrategy, IEnumerable<ChatMessage>, ILogger, CancellationToken, Task<IEnumerable<ChatMessage>>> _compactAsync;

    public string Name => _collapseToolResults ? "maf-collapse" : "maf-truncate";

    /// <summary>Incremented when MAF throws and we fall back to the classic policy.</summary>
    internal int FallbackCount => _fallbackCount;

    /// <summary>
    /// For testing: inject a custom compaction function (defaults to CompactionProvider.CompactAsync).
    /// Allows tests to force failures to verify fallback behavior.
    /// </summary>
    public Func<CompactionStrategy, IEnumerable<ChatMessage>, ILogger, CancellationToken, Task<IEnumerable<ChatMessage>>>? CompactAsyncOverride { get; init; }

    public MafContextWindowFitter(
        bool collapseToolResults,
        ILogger? logger = null)
    {
        _collapseToolResults = collapseToolResults;
        _logger = logger ?? NullLogger.Instance;
        _compactAsync = (strategy, messages, log, ct) =>
            CompactionProvider.CompactAsync(strategy, messages, log, ct);
    }

    public async ValueTask<IReadOnlyList<ChatMessage>> FitAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        int budgetTokens,
        double scale,
        CancellationToken cancellationToken)
    {
        // If the conversation fits, return it unchanged.
        int full = (int)(ContextSizeChatClient.EstimateTokens(messages, options) * scale);
        if (full <= budgetTokens)
        {
            return messages;
        }

        try
        {
            // Pin the head: all messages up to and including the first User message (the task).
            // If there is no User message, the head is only the leading System messages.
            int headCount = 0;
            int firstUser = -1;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == ChatRole.User)
                {
                    firstUser = i;
                    break;
                }
            }

            if (firstUser >= 0)
            {
                headCount = firstUser + 1;
            }
            else
            {
                // Count leading System messages
                for (int i = 0; i < messages.Count; i++)
                {
                    if (messages[i].Role != ChatRole.System)
                        break;
                    headCount = i + 1;
                }
            }

            List<ChatMessage> head = new(messages.Take(headCount));
            IEnumerable<ChatMessage> body = messages.Skip(headCount);

            // Quantized target: compute once, used by both triggers.
            double quantum = budgetTokens * 0.25;
            double mustDrop = Math.Ceiling(Math.Max(full - budgetTokens, 1) / quantum) * quantum;

            // Triggers that work with the FLEET's token estimate.
            CompactionTrigger over = index => EstimateWithHead(head, index.GetIncludedMessages(), options, scale) > budgetTokens;
            CompactionTrigger enough = index =>
                full - EstimateWithHead(head, index.GetIncludedMessages(), options, scale) >= mustDrop;

            // Build and run the compaction strategy.
            CompactionStrategy strategy = _collapseToolResults
                ? new PipelineCompactionStrategy(
                    new ToolResultCompactionStrategy(over, minimumPreservedGroups: 3, target: enough)
                    {
                        ToolCallFormatter = FormatCollapsed
                    },
                    new TruncationCompactionStrategy(over, minimumPreservedGroups: 3, target: enough))
                : new TruncationCompactionStrategy(over, minimumPreservedGroups: 3, target: enough);

            var compact = CompactAsyncOverride ?? _compactAsync;
            IEnumerable<ChatMessage> compactedBody = await compact(strategy, body, _logger, cancellationToken);

            List<ChatMessage> result = head.Concat(compactedBody).ToList();

            // Return the same instance if nothing was removed.
            if (result.Count == messages.Count && result.SequenceEqual(messages))
            {
                return messages;
            }

            return result;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            // MAF is experimental; if it throws, fall back to classic.
            Interlocked.Increment(ref _fallbackCount);
            if (Interlocked.CompareExchange(ref _fallbackLogged, 1, 0) == 0)
            {
                _logger.LogWarning(ex, "MAF compaction failed; falling back to classic policy.");
            }
            return await ClassicContextWindowFitter.Instance.FitAsync(messages, options, budgetTokens, scale, cancellationToken);
        }
    }

    /// <summary>Estimate tokens for head + included messages.</summary>
    private static int EstimateWithHead(List<ChatMessage> head, IEnumerable<ChatMessage> included, ChatOptions? options, double scale)
    {
        var toEstimate = head.Concat(included).ToList();
        return (int)(ContextSizeChatClient.EstimateTokens(toEstimate, options) * scale);
    }

    /// <summary>
    /// Format a collapsed tool call group as a single line with minimal detail.
    /// Each tool call is paired with its result: "name(args) -> result".
    /// Arguments are cut at 160 chars with "...", results at 200 chars.
    /// RemovedMarker is added at most once per group.
    /// Unpaired results are shown on their own.
    /// </summary>
    private static string FormatCollapsed(CompactionMessageGroup group)
    {
        var lines = new List<string>();
        bool needsMarker = false;

        // Build a map of call IDs to their results for pairing
        var results = new Dictionary<string, string>();
        foreach (ChatMessage message in group.Messages)
        {
            foreach (var content in message.Contents.OfType<FunctionResultContent>())
            {
                string resultText = content.Result?.ToString() ?? "";
                if (resultText.Length > 200)
                {
                    resultText = resultText[..200];
                    needsMarker = true;
                }
                results[content.CallId] = resultText;
            }
        }

        // Pair calls with results
        foreach (ChatMessage message in group.Messages)
        {
            foreach (var content in message.Contents.OfType<FunctionCallContent>())
            {
                string args = content.Arguments is not null
                    ? JsonSerializer.Serialize(content.Arguments)
                    : "";

                if (args.Length > 160)
                {
                    args = args[..160] + "...";
                }

                if (results.TryGetValue(content.CallId, out string? resultText))
                {
                    lines.Add($"{content.Name}({args}) -> {resultText}");
                }
                else
                {
                    lines.Add($"{content.Name}({args})");
                }
            }
        }

        // Add any unpaired results
        var pairedIds = group.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet();

        foreach (var result in results)
        {
            if (!pairedIds.Contains(result.Key))
            {
                lines.Add(result.Value);
            }
        }

        string content_str = "[earlier tool call] " + string.Join("; ", lines);
        if (needsMarker && !content_str.Contains(ContextSizeChatClient.RemovedMarker))
        {
            content_str += " " + ContextSizeChatClient.RemovedMarker;
        }
        return content_str;
    }
}
