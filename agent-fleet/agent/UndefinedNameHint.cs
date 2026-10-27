using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// The hint for a check that fails because a name is not defined, when a source file of the project declares that name at
/// its top level. The usual cause is a test that loads part of a file on its own and does not load the name: the retry is
/// pointed at the declaration and at the list of names the test loads, rather than at the code that runs in the browser.
/// The project is searched by plain text, in sorted path order, so the same failure always gets the same hint.
/// </summary>
internal static partial class UndefinedNameHint
{
    private const long MaxFileBytes = 200_000;

    private static readonly string[] SkippedFolders = ["node_modules", ".git"];
    private static readonly string[] SourceExtensions = [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx"];

    // Node prints "ReferenceError: X is not defined"; browsers print the same with "Uncaught " in front.
    [GeneratedRegex(@"ReferenceError:\s*(?<name>[A-Za-z_$][\w$]*) is not defined")]
    private static partial Regex Undefined();

    /// <summary>
    /// One hint line when the output reports a ReferenceError for a name that a source file of the project declares at its top
    /// level (function, const, let or class, optionally exported); null otherwise, including when the name is declared nowhere.
    /// </summary>
    public static string? Hint(string? output, string? projectRoot)
    {
        if (string.IsNullOrEmpty(output) || string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return null;
        }

        Match match = Undefined().Match(output);
        if (!match.Success || Declaration(Path.GetFullPath(projectRoot), match.Groups["name"].Value) is not { } declared)
        {
            return null;
        }

        string name = match.Groups["name"].Value;
        return $"{name} is declared in {declared.Path} (line {declared.Line}). A test that runs part of that file on its own may not load {name}: " +
            $"look for the list of names it evaluates or the function it extracts, and add {name} there, or move the call so the code the test loads does not need {name}.";
    }

    // The first source file, in sorted path order, whose line at the top level declares the name.
    private static (string Path, int Line)? Declaration(string root, string name)
    {
        var declares = new Regex(@"^(?:export\s+(?:default\s+)?)?(?:async\s+)?(?:function(?:\s*\*\s*|\s+)|const\s+|let\s+|class\s+)" +
            Regex.Escape(name) + @"(?![\w$])");
        foreach ((string relative, string full) in SourceFiles(root))
        {
            string[] lines;
            try
            {
                if (new FileInfo(full).Length > MaxFileBytes)
                {
                    continue;
                }

                lines = File.ReadAllLines(full);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            int index = Array.FindIndex(lines, line => declares.IsMatch(line));
            if (index >= 0)
            {
                return (relative, index + 1);
            }
        }

        return null;
    }

    // The JavaScript and TypeScript files under the project, outside dependency and version-control folders, links and
    // anything that is not a file, sorted by their path with forward slashes.
    private static List<(string Relative, string Full)> SourceFiles(string root)
    {
        var files = new List<(string Relative, string Full)>();
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.Count > 0)
        {
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(folders.Pop()).GetFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo folder)
                {
                    if (!SkippedFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        folders.Push(folder.FullName);
                    }
                }
                else if (entry is FileInfo file && SourceExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add((Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.FullName));
                }
            }
        }

        files.Sort((first, second) => StringComparer.Ordinal.Compare(first.Relative, second.Relative));
        return files;
    }
}
