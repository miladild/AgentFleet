using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet.Tests;

/// <summary>
/// Test helpers for context window fitter tests. Patterns adapted from planner's review probe.
/// </summary>
internal static class ContextWindowTestHelper
{
    /// <summary>Helper for collecting log messages in tests.</summary>
    internal sealed class CollectingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// Creates a byte-significant signature of a message for comparison by value (not reference).
    /// </summary>
    internal static string Signature(ChatMessage m) => m.Role + ":" + string.Join("|", m.Contents.Select(c => c switch
    {
        TextContent t => "T:" + t.Text,
        FunctionCallContent f => "C:" + f.CallId + f.Name + JsonSerializer.Serialize(f.Arguments),
        FunctionResultContent r => "R:" + r.CallId + r.Result,
        _ => c.GetType().Name
    }));

    /// <summary>
    /// Builds a realistic conversation with multiple tool rounds, each ~3-4k characters.
    /// </summary>
    internal static List<ChatMessage> BuildConversation(
        int rounds,
        bool withSystem = true,
        bool withUser = true,
        int systemChars = 800,
        int taskChars = 1500,
        int resultChars = 3500)
    {
        var list = new List<ChatMessage>();
        if (withSystem)
            list.Add(new ChatMessage(ChatRole.System, "You carry out one plan step. " + new string('s', systemChars)));
        if (withUser)
            list.Add(new ChatMessage(ChatRole.User, "TASK: implement compare and sort. " + new string('t', taskChars)));

        for (int i = 0; i < rounds; i++)
        {
            string name = (i % 3) switch
            {
                0 => "read_file",
                1 => "write_file",
                _ => "run_command"
            };
            var args = new Dictionary<string, object?> { ["path"] = $"src/file{i}.js" };
            if (name == "write_file")
                args["content"] = new string('w', 3000);
            if (name == "run_command")
                args["command"] = "node --test";

            list.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{i}", name, args)]));
            list.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent($"c{i}",
                    name == "write_file" ? $"Wrote 3000 characters to src/file{i}.js" : new string((char)('a' + i % 20), resultChars))]));
        }

        return list;
    }

    /// <summary>
    /// Checks if the conversation has orphaned results (results without calls or vice versa).
    /// </summary>
    internal static bool HasOrphans(IReadOnlyList<ChatMessage> msgs)
    {
        var calls = msgs.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();
        var results = msgs.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
        return !calls.SetEquals(results) && (calls.Except(results).Any() || results.Except(calls).Any());
    }
}
