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
