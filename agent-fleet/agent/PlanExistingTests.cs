using System.Text.RegularExpressions;

namespace AgentFleet;

// A small model writes a new test better when it can copy the project's own test, so the step prompt names a few
// existing ones. The same folder must always give the same list, so every order below is fixed.
internal static class PlanExistingTests
{
    internal const int Limit = 3;
    internal const long MaxBytes = 200_000;
    internal const int HeadChars = 4000;

    private static readonly string[] FolderNames = ["test", "tests", "__tests__", "spec"];

    // An import or require of a project file: a relative path, which starts with a dot.
    private static readonly Regex ImportOfProjectFile = new(@"\b(?:from|import|require)\b\s*\(?\s*['""]\.{1,2}/", RegexOptions.Compiled);

    internal static IReadOnlyList<string> Find(string? folder, IEnumerable<string> ownFiles)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            string root = Path.GetFullPath(folder);
            // The files the step writes itself are not examples of what to copy.
            var excluded = new HashSet<string>(ownFiles.Select(path => path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();

            // Only the named folders, one level deep, and only files directly inside them. A link is skipped.
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string folderName = Path.GetFileName(directory);
                if (!FolderNames.Contains(folderName, StringComparer.OrdinalIgnoreCase) || PlanRunner.HasLinkedPathComponent(root, directory))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    AddIfUsable(root, file, folderName + "/" + Path.GetFileName(file), excluded, candidates);
                }
            }

            // Top-level test and spec files, by name.
            foreach (string file in Directory.EnumerateFiles(root))
            {
                string name = Path.GetFileName(file);
                if (name.Contains(".test.", StringComparison.OrdinalIgnoreCase) || name.Contains(".spec.", StringComparison.OrdinalIgnoreCase))
                {
                    AddIfUsable(root, file, name, excluded, candidates);
                }
            }

            return candidates
                .Select(relative => (Relative: relative, Imports: ImportsAndNamesTests(root, relative)))
                .OrderByDescending(item => item.Imports)
                .ThenBy(item => item.Relative, StringComparer.Ordinal)
                .Take(Limit)
                .Select(item => item.Relative)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddIfUsable(string root, string file, string relative, HashSet<string> excluded, List<string> candidates)
    {
        if (excluded.Contains(relative) || PlanRunner.HasLinkedPathComponent(root, file) || new FileInfo(file).Length > MaxBytes)
        {
            return;
        }

        candidates.Add(relative);
    }

    // Reads only the start of the file, so the result does not depend on how long the rest of it is.
    private static bool ImportsAndNamesTests(string root, string relative)
    {
        using var reader = new StreamReader(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var buffer = new char[HeadChars];
        string head = new(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
        return ImportOfProjectFile.IsMatch(head) &&
            (head.Contains("test", StringComparison.OrdinalIgnoreCase) || head.Contains("describe", StringComparison.OrdinalIgnoreCase));
    }
}
