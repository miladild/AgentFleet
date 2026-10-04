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
    private const int MaxFrames = 2;
    private const int MaxLineCharacters = 200;
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
    /// "Where it failed" with the source line of each of the first stack frames that name a file of the project; null when the output
    /// names none. The paths in the output may be those of another machine: the longest tail of each that is a file of the project is used.
    /// </summary>
    public static string? Describe(string? output, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return null;
        }

        string root = Path.GetFullPath(projectRoot);
        var found = new List<(string Path, int Line, string Text)>();
        foreach ((string path, int line) in Frames(output))
        {
            if (NotTheProject.Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase)) ||
                path.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Read(root, path, line) is { } located && !found.Any(known => known.Path == located.Path && known.Line == located.Line))
            {
                found.Add(located);
                if (found.Count == MaxFrames)
                {
                    break;
                }
            }
        }

        if (found.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder("Where it failed (the source line each stack frame names):");
        foreach ((string path, int line, string source) in found)
        {
            text.Append($"\n- {path} line {line}: {source}");
        }

        return text.ToString();
    }

    // The frames in the order the output gives them.
    private static IEnumerable<(string Path, int Line)> Frames(string output) =>
        new[] { Colon(), Python(), Dotnet() }
            .SelectMany(pattern => pattern.Matches(output).Cast<Match>())
            .OrderBy(match => match.Index)
            .Select(match => (match.Groups["path"].Value, int.TryParse(match.Groups["line"].Value, out int line) ? line : 0));

    private static (string Path, int Line, string Text)? Read(string root, string path, int line)
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
                if (line > lines.Length)
                {
                    return null;
                }

                string source = lines[line - 1].Trim();
                if (source.Length == 0)
                {
                    return null;
                }

                if (source.Length > MaxLineCharacters)
                {
                    source = source[..MaxLineCharacters] + " ...";
                }

                return (string.Join('/', parts[start..]), line, source);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }
}
