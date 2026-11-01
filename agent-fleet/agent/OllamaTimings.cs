using OllamaSharp.Models.Chat;

namespace AgentFleet;

/// <summary>One model call as Ollama reports it at the end of its reply: tokens and seconds, split into reading the prompt and writing the answer.</summary>
internal readonly record struct OllamaCallTiming(long PromptTokens, double PromptEvalSeconds, long EvalTokens, double EvalSeconds, double LoadSeconds, double TotalSeconds);

internal static class OllamaTimings
{
    private const double NanosecondsPerSecond = 1_000_000_000d;

    /// <summary>The timings in an Ollama reply (the raw object of a chat response or of its last streamed update), or null for anything else.</summary>
    public static OllamaCallTiming? From(object? raw) => raw is ChatDoneResponseStream done
        ? new OllamaCallTiming(done.PromptEvalCount, done.PromptEvalDuration / NanosecondsPerSecond, done.EvalCount, done.EvalDuration / NanosecondsPerSecond,
            done.LoadDuration / NanosecondsPerSecond, done.TotalDuration / NanosecondsPerSecond)
        : null;
}
