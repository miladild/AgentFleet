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
    private readonly bool _collapseToolResults;
    private readonly ILogger _logger;

    public string Name => _collapseToolResults ? "maf-collapse" : "maf-truncate";

    /// <summary>Incremented when MAF throws and we fall back to the classic policy.</summary>
    internal int FallbackCount => _fallbackCount;

    public MafContextWindowFitter(bool collapseToolResults, ILogger? logger = null)
    {
        _collapseToolResults = collapseToolResults;
        _logger = logger ?? NullLogger.Instance;
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
            // Pin the head: system messages plus the first user message (the task).
            // If there is no user message, the head is only the leading System messages.
            int headCount = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == ChatRole.User)
                {
                    headCount = i + 1;
                    break;
                }
                if (messages[i].Role != ChatRole.System)
                {
                    // Hit a non-system, non-user message; head is all system messages so far.
                    break;
                }
                headCount = i + 1;
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

            IEnumerable<ChatMessage> compactedBody = await CompactionProvider.CompactAsync(
                strategy, body, _logger, cancellationToken);

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
            _logger.LogWarning(ex, "MAF compaction failed; falling back to classic policy.");
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
    /// Format a collapsed tool call group as a single line with minimal detail:
    /// [earlier tool call] tool_name(args up to 160 chars); ...
    /// </summary>
    private static string FormatCollapsed(CompactionMessageGroup group)
    {
        var lines = new List<string>();

        // Extract tool calls from this group.
        foreach (ChatMessage message in group.Messages)
        {
            foreach (var content in message.Contents.OfType<FunctionCallContent>())
            {
                string args = content.Arguments is not null
                    ? JsonSerializer.Serialize(content.Arguments)
                    : "";

                // Truncate arguments to 160 characters.
                if (args.Length > 160)
                {
                    args = args[..160];
                }

                lines.Add($"{content.Name}({args})");
            }
        }

        // Extract results from this group.
        foreach (ChatMessage message in group.Messages)
        {
            foreach (var content in message.Contents.OfType<FunctionResultContent>())
            {
                string resultText = content.Result?.ToString() ?? "";

                // Truncate result to 200 characters.
                if (resultText.Length > 200)
                {
                    resultText = resultText[..200] + " " + ContextSizeChatClient.RemovedMarker;
                }

                lines.Add(resultText);
            }
        }

        return "[earlier tool call] " + string.Join("; ", lines);
    }
}
