using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// One policy for shortening a conversation that outgrew the model's window. Policies are seams that can be replaced
/// without touching num_ctx sizing or changing default behaviour, so an experiment arm can measure whether a new policy
/// reduces redundancy (old models copy the shape of the calls they see) better than the classic (shortest old output,
/// stable prefix) without affecting fleet testing or production until a decision is made.
/// </summary>
internal interface IContextWindowFitter
{
    string Name { get; }

    /// <summary>
    /// Returns the SAME list instance when nothing needs to go. Never mutates the caller's list or messages.
    /// </summary>
    ValueTask<IReadOnlyList<ChatMessage>> FitAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, int budgetTokens, double scale, CancellationToken cancellationToken);
}

/// <summary>
/// The original policy: when a conversation outgrows the window, the oldest tool calls go (each with its results),
/// and old tool output is shortened in steps (so the start of the prompt stays the same between calls for Ollama's
/// prompt cache). System messages, the first user message (the task), and the latest turns stay whole.
/// </summary>
internal sealed class ClassicContextWindowFitter : IContextWindowFitter
{
    public static readonly ClassicContextWindowFitter Instance = new();

    public string Name => "classic";

    public ValueTask<IReadOnlyList<ChatMessage>> FitAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, int budgetTokens, double scale, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ContextSizeChatClient.FitToWindow(messages, options, budgetTokens, scale));
}

/// <summary>
/// Parses the FLEET_CONTEXT_FIT setting and builds the corresponding fitter.
/// </summary>
internal static class ContextWindowFitters
{
    public static IContextWindowFitter FromSetting(string? value, ILogger? logger)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "classic", StringComparison.OrdinalIgnoreCase))
        {
            return ClassicContextWindowFitter.Instance;
        }

        logger?.LogWarning("Unknown context-window fitter '{Value}'. Accepted values are: classic, maf-truncate, maf-collapse. Using classic.", value);
        return ClassicContextWindowFitter.Instance;
    }
}
