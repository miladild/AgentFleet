using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// A node test run can finish all its tests and then stay alive because a test left a server or a socket open, so the runner waits
/// for its own time limit and kills it, again and again. A short preload, loaded with <c>--import</c>, prints one marker line a
/// moment after the last test of a file when the process is still held open, and nothing at all on a clean file. The command that
/// carries it can then be ended at once, and the worker told what is still open. The preload travels inside the command as a data
/// URL, so no file is written to the project.
/// </summary>
internal static partial class OpenHandlePreload
{
    /// <summary>The start of the line the preload prints, followed by the names of what is still open.</summary>
    internal const string Marker = "[open-after-tests]";

    /// <summary>
    /// The preload's source: after a test file's last test, a timer that does not keep the process alive looks at what is still
    /// open. When something is (a server, a socket, a timer), it names it on stderr and then ends the file's process with the
    /// exit code the tests gave, so the runner reports every result and its summary at once instead of waiting for the time limit.
    /// A file that leaves nothing open prints nothing.
    /// </summary>
    internal static readonly string Source = string.Join("\n",
        "import { after } from 'node:test';",
        "after(() => setTimeout(() => {",
        "  const open = process.getActiveResourcesInfo().filter(r => r !== 'PipeWrap' && r !== 'TTYWrap');",
        "  if (open.length === 0) return;",
        "  process.stderr.write(`\\n[open-after-tests] ${open.join(', ')}\\n`, () => process.exit(process.exitCode ?? 0));",
        "}, 200).unref());",
        "");

    private const string DataUrlPrefix = "data:text/javascript;base64,";

    // The line the preload prints, with the text after the marker.
    [GeneratedRegex(@"^\[open-after-tests\][ \t]*(?<name>[^\r\n]*)", RegexOptions.Multiline)]
    private static partial Regex MarkerLine();

    // One command of a shell line that runs node's own test runner: node or tsx (also through npx) with a "--test" flag of its own
    // (not --test-name-pattern and the like). The group "runner" is the word the preload is put after.
    [GeneratedRegex(@"^\s*(?:npx\s+(?:(?:-y|--yes)\s+)?)?(?<runner>node|tsx)(?=\s)(?:\s.*?)?\s--test(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NodeTestCommand();

    /// <summary>The preload as a data URL: it needs no file on disk and works with <c>--import</c>.</summary>
    internal static string DataUrl() => DataUrlPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(Source));

    /// <summary>The argument that loads the preload. The URL is in double quotes so its semicolon is safe in every shell the fleet uses.</summary>
    internal static string Argument() => $"--import \"{DataUrl()}\"";

    /// <summary>The text after the marker on the first marker line of an output, or null when the output has none.</summary>
    internal static string? ResourceFrom(string? output) =>
        string.IsNullOrEmpty(output) || MarkerLine().Match(output) is not { Success: true } line ? null : line.Groups["name"].Value.Trim();

    /// <summary>Whether a line of output is the preload's marker line.</summary>
    internal static bool IsMarkerLine(string? line) => line is not null && line.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>Whether a command carries this preload, so the marker line in its output is the fleet's own.</summary>
    internal static bool IsLoadedBy(string? command) => command?.Contains(DataUrl(), StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether a command runs node's own test runner: <c>node ... --test</c>, <c>tsx --test</c>, <c>npx tsx --test</c>. Any command
    /// of a pipe or of a chain joined with <c>&amp;&amp;</c>, <c>||</c> or <c>;</c> counts, so <c>cd app &amp;&amp; node --test x</c> does.
    /// </summary>
    internal static bool IsNodeTestRun(string? command) => FirstNodeTestCommand(command) is not null;

    /// <summary>
    /// The place of the first command of a shell line that runs node's test runner: where it starts and ends in the text. Null when
    /// no command of the line does. The operators inside quotes do not split the line.
    /// </summary>
    internal static (int Start, int End)? FirstNodeTestCommand(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        foreach ((int start, int end) in Commands(line))
        {
            if (NodeTestCommand().IsMatch(line.AsSpan(start, end - start)))
            {
                return (start, end);
            }
        }

        return null;
    }

    /// <summary>
    /// The command with the preload put right after <c>node</c> (or <c>tsx</c>) in the first command that runs node's test runner, so
    /// <c>cd app &amp;&amp; node --test x</c> and <c>node --test a 2&gt;&amp;1 | tail</c> both work. A command that already ends by
    /// itself (it carries <c>--test-force-exit</c>), already has the preload or any data URL import, or runs no node test is returned as it is.
    /// </summary>
    internal static string? WithPreload(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) ||
            command.Contains("--test-force-exit", StringComparison.Ordinal) ||
            command.Contains("open-after-tests", StringComparison.Ordinal) ||
            command.Contains("data:text/javascript", StringComparison.Ordinal) ||
            FirstNodeTestCommand(command) is not { } place)
        {
            return command;
        }

        Match runner = NodeTestCommand().Match(command[place.Start..place.End]);
        int insertAt = place.Start + runner.Groups["runner"].Index + runner.Groups["runner"].Length;
        return string.Concat(command.AsSpan(0, insertAt), " ", Argument(), command.AsSpan(insertAt));
    }

    /// <summary>
    /// What a worker reads when a test run ended because something stayed open after its tests: the tests ran to the end, what is
    /// still open, that the process was ended for that, and what to do instead of adding flags.
    /// </summary>
    internal static string Explanation(string? resource)
    {
        string open = string.IsNullOrWhiteSpace(resource) || resource.Trim() == "nothing"
            ? "something is still open (a timer, or a handle node does not name)"
            : $"something is still open ({resource.Trim()})";
        return $"The tests ran to the end, but {open}, so the process was ended for that reason. " +
               "Close it in the code or the test (for example server.close() in an after hook, clear timers, destroy sockets) " +
               "rather than adding flags. Do not run the same command again unchanged: it will end the same way.";
    }

    // The commands of a shell line as positions in it: the text between the operators && || | and ;, leaving out what is quoted.
    private static IEnumerable<(int Start, int End)> Commands(string line)
    {
        int start = 0;
        char quote = '\0';
        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (quote != '\0')
            {
                quote = current == quote ? '\0' : quote;
                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            int operatorLength = current switch
            {
                '&' when index + 1 < line.Length && line[index + 1] == '&' => 2,
                '|' when index + 1 < line.Length && line[index + 1] == '|' => 2,
                '|' or ';' => 1,
                _ => 0
            };
            if (operatorLength == 0)
            {
                continue;
            }

            yield return (start, index);
            index += operatorLength - 1;
            start = index + 1;
        }

        yield return (start, line.Length);
    }
}
