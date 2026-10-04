using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// Stops a model from making the same call and getting the same error back, again and again. Measured: plan turns of 17 to
/// 19 minutes in which a small model retried one failing command or edit until its round budget ran out. The third
/// identical failure in a turn gets a short note added to its result ("this is not working, try something else"); the
/// fifth ends the turn, so the runner runs the step's check and the repair loop (see RepairLadder) takes over with fresh
/// information. "The same" means the same tool with the same arguments and the same error text, apart from timings.
/// Only calls that came back with an error count (see <see cref="IsError"/>): repeating a call that works is not a loop.
/// </summary>
internal static partial class ToolLoopGuard
{
    public const int NudgeAt = 3;
    public const int StopAt = 5;

    /// <summary>Starts every note the guard adds to a result, so the same failure still counts as the same after one was added.</summary>
    internal const string Marker = "[Fleet loop guard]";

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\s*(?:ms|msec|milliseconds?|s|secs?|seconds?)\b|\b\d{4}-\d\d-\d\d[T ][\d:.]+Z?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Timing();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>A tool result that says the call failed: an error line, a failure, or a command that exited with a non-zero code.</summary>
    public static bool IsError(string result)
    {
        string output = result.TrimStart();
        if (output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith("Failed:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string exitCodePrefix = "Exit code:";
        if (output.StartsWith(exitCodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            int end = output.IndexOfAny(['\r', '\n']);
            string value = (end < 0 ? output[exitCodePrefix.Length..] : output[exitCodePrefix.Length..end]).Trim();
            return int.TryParse(value, out int exitCode) && exitCode != 0;
        }

        return false;
    }

    /// <summary>The third call in a turn that leaves out a required argument ends the turn (see <see cref="InvokeAsync"/>).</summary>
    public const int ArgumentFailureStopAt = 3;

    [GeneratedRegex(@"required parameter '([^']+)'")]
    private static partial Regex RequiredParameter();

    /// <summary>
    /// Runs a tool call. A call that leaves out an argument the tool needs (a small model's write_file without its content)
    /// comes back to the model as an error it can read, like any other failed call, instead of as an exception: the exception
    /// is rethrown after a few in a row and the whole round is lost (measured: five rounds of seven minutes each, then a
    /// step parked). The third such call in a turn ends the turn, so the runner runs the step's check and the repair loop
    /// takes over. Outside a plan step the exception is left alone.
    /// </summary>
    public static async Task<object?> InvokeAsync(FunctionInvocationContext context, bool fromRunner, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Function.InvokeAsync(context.Arguments, cancellationToken);
        }
        catch (ArgumentException exception) when (fromRunner && MissingArgument(context.Function.Name, exception) is not null)
        {
            string message = MissingArgument(context.Function.Name, exception)!;
            int failures = 1 + FailuresWithText(context.Messages, message);
            if (failures >= ArgumentFailureStopAt)
            {
                context.Terminate = true;
                return message + $"\n\n{Marker} {failures} calls in this turn left out a required argument, so the turn ends here. " +
                       "The fleet will run the step's check and tell you what it prints.";
            }

            return message;
        }
    }

    /// <summary>The error a model is given for a call that left out a required argument; null for any other exception.</summary>
    public static string? MissingArgument(string tool, Exception exception)
    {
        if (exception is not ArgumentException || !exception.Message.Contains("missing a value for the required parameter", StringComparison.Ordinal))
        {
            return null;
        }

        string parameter = RequiredParameter().Match(exception.Message) is { Success: true } match ? match.Groups[1].Value : "a required argument";
        string hint = tool == "write_file" && parameter == "content"
            ? ": `content` is the complete text of the file, written out in full"
            : string.Empty;
        return $"Error: {tool} was called without `{parameter}`, which it needs. Call {tool} again with every required argument{hint}.";
    }

    // How many results of this turn (everything after the latest user message) carry this same error text, whatever the call's arguments were.
    private static int FailuresWithText(IEnumerable<ChatMessage> messages, string text)
    {
        ChatMessage[] list = messages.ToArray();
        int start = Array.FindLastIndex(list, message => message.Role == ChatRole.User) + 1;
        string wanted = Normalise(text);
        return list.Skip(start).SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Count(result => result.Result?.ToString() is { } answer && IsError(answer) && Normalise(answer) == wanted);
    }

    /// <summary>
    /// Looks at the call that just finished. After the third identical failure in this turn its result gets a note; the fifth
    /// also ends the turn. Returns the result to give back to the model.
    /// </summary>
    public static object? Apply(FunctionInvocationContext context, object? result, ILogger? logger = null)
    {
        if (result?.ToString() is not { } text || context.CallContent is not { } call || !IsError(text))
        {
            return result;
        }

        int repeats = Repeats(context.Messages, call, text);
        if (repeats < NudgeAt || repeats > StopAt)
        {
            return result;
        }

        if (repeats == NudgeAt)
        {
            logger?.LogWarning("Tool {Tool} failed the same way {Count} times in one turn; the model is told to change approach.", call.Name, repeats);
            return text + $"\n\n{Marker} This exact call has now failed the same way {repeats} times in this turn. It is not going to work: " +
                   "read the error again and try a different approach (other arguments, another file, another tool).";
        }

        if (repeats == StopAt)
        {
            logger?.LogWarning("Tool {Tool} failed the same way {Count} times in one turn; the turn ends here.", call.Name, repeats);
            context.Terminate = true;
            return text + $"\n\n{Marker} This exact call has now failed the same way {repeats} times in this turn, so the turn ends here. " +
                   "The fleet will run the step's check and tell you what it prints.";
        }

        return result;
    }

    /// <summary>
    /// How many calls of this turn (everything after the latest user message) came back with this same error from the same
    /// tool and arguments, counting the call that just finished.
    /// </summary>
    public static int Repeats(IEnumerable<ChatMessage> messages, FunctionCallContent call, string result)
    {
        ChatMessage[] list = messages.ToArray();
        int start = Array.FindLastIndex(list, message => message.Role == ChatRole.User) + 1;

        // Each result belongs to the call it answers. Call ids are the hint, not the rule: a provider may give none, or the
        // same one every time, so a result that finds no call by id belongs to the oldest call still waiting for its answer.
        var waiting = new List<FunctionCallContent>();
        var failures = new List<(string Key, string Error)>();
        for (int index = start; index < list.Length; index++)
        {
            foreach (AIContent content in list[index].Contents)
            {
                if (content is FunctionCallContent earlier)
                {
                    waiting.Add(earlier);
                }
                else if (content is FunctionResultContent answer)
                {
                    FunctionCallContent? answered = waiting.FirstOrDefault(candidate => string.Equals(candidate.CallId, answer.CallId, StringComparison.Ordinal))
                        ?? waiting.FirstOrDefault();
                    if (answered is null)
                    {
                        continue;
                    }

                    waiting.Remove(answered);
                    if (answer.Result?.ToString() is { } text && IsError(text))
                    {
                        failures.Add((KeyOf(answered), Normalise(text)));
                    }
                }
            }
        }

        string key = KeyOf(call);
        string error = Normalise(result);
        return failures.Count(failure => string.Equals(failure.Key, key, StringComparison.Ordinal) &&
            string.Equals(failure.Error, error, StringComparison.Ordinal)) + 1;
    }

    // The tool and its arguments, in a form that does not depend on key order or on how long a written file was.
    private static string KeyOf(FunctionCallContent call)
    {
        string arguments;
        try
        {
            arguments = JsonSerializer.Serialize(call.Arguments?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value) ?? []);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            arguments = string.Join(',', call.Arguments?.Keys ?? []);
        }

        return $"{call.Name}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(arguments)))}";
    }

    // The error text without the guard's own notes, timings and timestamps: "ran in 12 ms" and "ran in 14 ms" are the same failure.
    private static string Normalise(string result)
    {
        int note = result.IndexOf(Marker, StringComparison.Ordinal);
        string text = note >= 0 ? result[..note] : result;
        text = Timing().Replace(text, "<time>");
        text = Spaces().Replace(text, " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
