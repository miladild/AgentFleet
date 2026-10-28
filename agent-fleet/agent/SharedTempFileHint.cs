using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// The hint for a failing check whose test writes to a fixed name in the temp folder. Every test that uses that name shares
/// one file, so a later test or round sees what an earlier one left, and the output shows results that belong to other rounds.
/// The retry is pointed at the line and asked for a name that belongs to one test only.
/// The test files are the ones the step prompt names as examples, searched by plain text in sorted path order, so the same
/// failure always gets the same hint.
/// </summary>
internal static partial class SharedTempFileHint
{
    internal const long MaxFileBytes = 200_000;

    private static readonly string[] FolderNames = ["test", "tests", "__tests__", "spec"];
    private static readonly string[] FailureWords = ["✖", "FAIL", "failed", "AssertionError", "Assertion failed"];

    // A line that gives each run its own name is not a fixed name, even when it also names the temp folder.
    private static readonly string[] UniqueNameMarks =
        ["randomUUID", "mkdtemp", "mkdtempSync", "Math.random", "Date.now", "process.pid", "uuid", "NamedTemporaryFile", "TemporaryDirectory"];

    // JavaScript or TypeScript: tmpdir() (also os.tmpdir()), a comma, then a quoted or backquoted string with no substitution.
    [GeneratedRegex(@"\btmpdir\(\)\s*,\s*(?:'[^'\r\n]*'|""[^""\r\n]*""|`[^`\r\n]*`)")]
    private static partial Regex NodeTempLiteral();

    // Python: tempfile.gettempdir() as the first argument of a join, followed by a plain string literal.
    [GeneratedRegex(@"\btempfile\.gettempdir\(\)\s*,\s*(?:'[^'\r\n]*'|""[^""\r\n]*"")")]
    private static partial Regex PythonTempLiteral();

    /// <summary>
    /// One hint line when the output reads like a failing test run and a test file in the project writes to a fixed name in
    /// the temp folder; null otherwise. The first such file (sorted path order) and its first such line are named.
    /// </summary>
    public static string? Hint(string? failureOutput, string? projectRoot)
    {
        if (string.IsNullOrEmpty(failureOutput) || string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot) ||
            !FailureWords.Any(word => failureOutput.Contains(word, StringComparison.Ordinal)))
        {
            return null;
        }

        try
        {
            foreach ((string relative, string full) in TestFiles(Path.GetFullPath(projectRoot)))
            {
                if (FixedNameLine(full) is { } line)
                {
                    return $"{relative} line {line} writes to a fixed name in the temp folder, so a later test or round sees what an earlier one left. " +
                        "Give each test its own name (randomUUID from node:crypto, or fs.mkdtempSync) and remove the file in a finally block, " +
                        "or in Python use tempfile.";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    // The first line, counted from 1, that writes to a fixed name; null when there is none or the file is too large to read.
    private static int? FixedNameLine(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes)
            {
                return null;
            }

            string[] lines = File.ReadAllLines(file);
            int index = Array.FindIndex(lines, IsFixedName);
            return index >= 0 ? index + 1 : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsFixedName(string line)
    {
        if (UniqueNameMarks.Any(mark => line.Contains(mark, StringComparison.Ordinal)))
        {
            return false;
        }

        return HasPlainLiteral(NodeTempLiteral(), line) || HasPlainLiteral(PythonTempLiteral(), line);
    }

    // A substitution such as ${...} makes the name differ from run to run, so such a match does not count.
    private static bool HasPlainLiteral(Regex pattern, string line) =>
        pattern.Matches(line).Any(match => !match.Value.Contains("${", StringComparison.Ordinal));

    // The same files PlanExistingTests looks at: the named folders one level deep, and top-level test and spec files. Links are
    // skipped. Dependency and version-control folders are never reached, since neither is one of these folders.
    private static List<(string Relative, string Full)> TestFiles(string root)
    {
        var files = new List<(string Relative, string Full)>();
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (!FolderNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase) ||
                PlanRunner.HasLinkedPathComponent(root, directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                AddIfUsable(root, file, files);
            }
        }

        foreach (string file in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(file);
            if (name.Contains(".test.", StringComparison.OrdinalIgnoreCase) || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase))
            {
                AddIfUsable(root, file, files);
            }
        }

        files.Sort((first, second) => StringComparer.Ordinal.Compare(first.Relative, second.Relative));
        return files;
    }

    private static void AddIfUsable(string root, string file, List<(string Relative, string Full)> files)
    {
        if (PlanRunner.HasLinkedPathComponent(root, file))
        {
            return;
        }

        files.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), file));
    }
}
