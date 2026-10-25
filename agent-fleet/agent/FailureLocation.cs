using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// The source lines a failing check's output points at. A small model given a stack trace does not go and read the line it
/// names: it argues from the names of the failing tests (measured: it asserted that correct code did not throw, when the
/// line the output named was a test assertion that was wrong, and once blamed "the hub's stale node_modules" for an error
/// two lines below the frame that caused it). The runner reads the line and says it, so the cause is in front of the model.
/// Only files inside the project are read, and never through a link.
/// </summary>
internal static partial class FailureLocation
{
    private const int MaxFrames = 6;
    private const int MaxLineCharacters = 200;
    private const int MaxTextCharacters = 3000;
    private const int MaxTestLinesUp = 15;
    private const int ReservedCharacters = 200;
    private const long MaxFileBytes = 1_000_000;

    // file.ext:line or file.ext:line:column, as Node, Jest, Vitest, pytest and many compilers print it.
    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:)?[^\s():""'<>|*?]+\.[A-Za-z0-9]{1,5}):(?<line>\d+)(?::\d+)?")]
    private static partial Regex Colon();

    // File "path", line 12 (Python).
    [GeneratedRegex(@"File ""(?<path>[^""]+)"", line (?<line>\d+)")]
    private static partial Regex Python();

    // in path:line 12 (.NET).
    [GeneratedRegex(@" in (?<path>[^\r\n]+?):line (?<line>\d+)")]
    private static partial Regex Dotnet();

    // The worker's workspace as the runner lays it out: <root>/plans/<plan id>/<machine>/project/.
    [GeneratedRegex(@"[^\s()'""<>]*?plans[\\/][0-9a-f]{32}[\\/][^\\/\s()'""<>]+[\\/]project[\\/]")]
    private static partial Regex WorkerProject();

    // A line that opens a test: test(, it(, specify( and their .only, .skip and .each forms; Python def test_; a .NET test attribute.
    [GeneratedRegex(@"^\s*(?:(?:test|it|specify)(?:\.\w+)*\s*\(|(?:async\s+)?def test_|\[(?:Fact|Theory|Test|TestCase)\b)")]
    private static partial Regex TestOpener();

    /// <summary>
    /// The output of a check with the project's own folder taken off its paths, so a stack frame reads "src/semver.js:35:28" and the model
    /// opens that path. With a worker's absolute path in it (a long path through the worker's plans folder) a small model read the long path
    /// (it is not a path its file tools take), or took it for another machine's copy and blamed that. On the replay of one real repair round
    /// the approved check passed after the round in 6 of 8 trials with the line and the paths cut, 4 of 8 with the line only, 2 of 8 without.
    /// </summary>
    public static string ProjectRelative(string text, string? projectRoot)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string result = WorkerProject().Replace(text, string.Empty);
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            string root = projectRoot.TrimEnd('\\', '/');
            result = result.Replace(root.Replace('/', '\\') + "\\", string.Empty, StringComparison.OrdinalIgnoreCase);
            result = result.Replace(root.Replace('\\', '/') + "/", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static readonly string[] NotTheProject = ["node_modules", "site-packages", "dist-packages", ".nuget", "/usr/lib/", "\\dotnet\\shared\\"];

    /// <summary>
    /// "Where it failed" with each of the first stack frames that name a file of the project: the whole test a failing line sits in,
    /// as written, when the line is inside a test; otherwise the source line the frame names. Null when the output names none. The paths
    /// in the output may be those of another machine: the longest tail of each that is a file of the project is used.
    /// </summary>
    public static string? Describe(string? output, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return null;
        }

        string root = Path.GetFullPath(projectRoot);
        // node:test prints "test at <file>:<line>:<column>" before each failure. That names where the test was declared (its position is
        // often not mapped back to the source), not where it failed, so it is not a frame.
        string frames = string.Join('\n', output.Split('\n').Where(line => !line.TrimStart().StartsWith("test at ", StringComparison.Ordinal)));
        var located = new List<(string Path, int Line, string[] Lines)>();
        foreach ((string path, int line) in Frames(frames))
        {
            if (IsDependency(path) || located.Count == MaxFrames)
            {
                continue;
            }

            if (Read(root, path, line) is { } frame && !located.Any(known => known.Path == frame.Path && known.Line == frame.Line))
            {
                located.Add(frame);
            }
        }

        // Frames in the same test are one block, which runs down to the last of their lines.
        var groups = new List<(string Path, int Start, int Last, string[] Lines)>();
        foreach ((string path, int line, string[] lines) in located)
        {
            int start = TestStart(lines, line);
            int index = start == 0 ? -1 : groups.FindIndex(group => group.Path == path && group.Start == start);
            if (index < 0)
            {
                groups.Add((path, start, line, lines));
            }
            else
            {
                groups[index] = (groups[index].Path, groups[index].Start, Math.Max(groups[index].Last, line), groups[index].Lines);
            }
        }

        var entries = new List<Entry>();
        foreach ((string path, int start, int last, string[] lines) in groups)
        {
            var single = new Entry($"- {path} line {last}: {SourceLine(lines, last)}", false);
            if (start == 0)
            {
                entries.Add(single);
                continue;
            }

            var block = new Entry(TestBlock(path, start, last, lines), true);
            entries.Add(block.Text.Length + Heading.Length + ReservedCharacters <= MaxTextCharacters ? block : single);
        }

        if (entries.Count == 0)
        {
            return null;
        }

        int shown = 0;
        while (shown < entries.Count && Compose(entries, shown + 1).Length <= MaxTextCharacters)
        {
            shown++;
        }

        return Compose(entries, shown);
    }

    private sealed record Entry(string Text, bool IsTest);

    private static string Compose(IReadOnlyList<Entry> entries, int shown)
    {
        var text = new StringBuilder(Heading);
        for (int index = 0; index < shown; index++)
        {
            text.Append('\n').Append(entries[index].Text);
        }

        if (shown < entries.Count)
        {
            text.Append($"\n({entries.Count - shown} more failing locations not shown)");
        }

        if (entries.Take(shown).Any(entry => entry.IsTest))
        {
            text.Append("\nThe inputs and comments in these tests are the rules the code has to meet.");
        }

        return text.ToString();
    }

    private const string Heading = "Where it failed (each failing test as written, or the source line a stack frame names):";

    // The line that opens the test a failing line sits in, looking at the line itself and up to MaxTestLinesUp lines above; 0 when none does.
    private static int TestStart(string[] lines, int line)
    {
        for (int index = line; index >= Math.Max(1, line - MaxTestLinesUp); index--)
        {
            if (TestOpener().IsMatch(lines[index - 1]))
            {
                return index;
            }
        }

        return 0;
    }

    private static string TestBlock(string path, int first, int last, string[] lines)
    {
        var text = new StringBuilder($"- {path} lines {first}-{last} (the failing test as written):");
        for (int index = first; index <= last; index++)
        {
            text.Append($"\n  {index}: {Cut(lines[index - 1].TrimEnd())}");
        }

        return text.ToString();
    }

    private static string SourceLine(string[] lines, int line) => Cut(lines[line - 1].Trim());

    private static string Cut(string text) => text.Length > MaxLineCharacters ? text[..MaxLineCharacters] + " ..." : text;

    // A frame into a dependency, the runtime or a link is not code of the project.
    private static bool IsDependency(string path) =>
        NotTheProject.Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase)) || path.StartsWith("node:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a line of check output is a stack frame (or any other text) that names a file of the project, not a dependency.
    /// </summary>
    internal static bool NamesProjectFile(string line) => Frames(line).Any(frame => !IsDependency(frame.Path));

    // The frames in the order the output gives them.
    private static IEnumerable<(string Path, int Line)> Frames(string output) =>
        new[] { Colon(), Python(), Dotnet() }
            .SelectMany(pattern => pattern.Matches(output).Cast<Match>())
            .OrderBy(match => match.Index)
            .Select(match => (match.Groups["path"].Value, int.TryParse(match.Groups["line"].Value, out int line) ? line : 0));

    private static (string Path, int Line, string[] Lines)? Read(string root, string path, int line)
    {
        string[] parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (line < 1 || parts.Any(part => part is ".." or "."))
        {
            return null;
        }

        for (int start = 0; start < parts.Length; start++)
        {
            string relative = string.Join(Path.DirectorySeparatorChar, parts[start..]);
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, relative));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) ||
                PlanRunner.HasLinkedPathComponent(root, full))
            {
                continue;
            }

            try
            {
                if (new FileInfo(full).Length > MaxFileBytes)
                {
                    return null;
                }

                string[] lines = File.ReadAllLines(full);
                if (line > lines.Length || lines[line - 1].Trim().Length == 0)
                {
                    return null;
                }

                return (string.Join('/', parts[start..]), line, lines);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }
}
