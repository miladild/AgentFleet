using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// Direct, unsandboxed file access on the hub's own local filesystem - unlike
/// DockerSandboxExecutor, there is no container boundary here. Deliberately
/// unrestricted (no path allowlist) per explicit user choice; the only limit is
/// whatever the backend process's own OS-level permissions allow.
/// </summary>
internal static class HubFileSystemTools
{
    private const int MaxReadCharacters = 50_000;
    private const int MaxSearchMatches = 100;
    private const int MaxFoundPaths = 300;
    private const int MaxSearchLineLength = 240;
    private const long MaxSearchFileBytes = 2 * 1024 * 1024;

    // Directories nobody wants search results from: dependency and build output.
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".next", "dist", "build", "out", ".venv", "venv",
        "__pycache__", ".idea", ".vs", "target", "coverage"
    };

    /// <summary>Dependency and build folders the file tools never descend into.</summary>
    internal static IReadOnlySet<string> SkippedFolderNames => SkippedDirectories;

    /// <summary>A path as every hub tool reads it: absolute as given, relative to FLEET_FILES_ROOT or the working folder.</summary>
    internal static string Resolve(string path) => Path.GetFullPath(ResolvePath(path));

    public static async Task<string> ReadFileAsync(
        string path,
        int? startLine,
        int? endLine,
        CancellationToken cancellationToken)
    {
        string resolved = ResolvePath(path);
        if (!File.Exists(resolved))
        {
            return $"Error: file not found: {resolved}";
        }

        string content = await File.ReadAllTextAsync(resolved, cancellationToken);

        if (startLine is not null || endLine is not null)
        {
            string[] lines = content.Split('\n');
            int first = Math.Max(startLine ?? 1, 1);
            int last = Math.Min(endLine ?? lines.Length, lines.Length);
            if (first > lines.Length)
            {
                return $"Error: startLine {first} is past the end of the file ({lines.Length} lines).";
            }

            if (last < first)
            {
                return $"Error: endLine {last} is before startLine {first}.";
            }

            string slice = string.Join("\n", lines[(first - 1)..last]).TrimEnd('\r');
            string header = $"[lines {first}-{last} of {lines.Length}]\n";
            return Truncate(header + slice, resolved, content.Length);
        }

        return Truncate(content, resolved, content.Length);
    }

    public static async Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        string resolved = ResolvePath(path);
        string? directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(resolved, content, cancellationToken);
        return $"Wrote {content.Length} characters to {resolved}";
    }

    // Exact-text replacement. Far cheaper than rewriting a whole file to change a few
    // lines, and it fails loudly instead of silently corrupting the file when the text
    // is not there or is ambiguous.
    public static async Task<string> EditFileAsync(
        string path,
        string oldText,
        string newText,
        bool? replaceAll,
        CancellationToken cancellationToken)
    {
        string resolved = ResolvePath(path);
        if (!File.Exists(resolved))
        {
            return $"Error: file not found: {resolved}. Use write_file to create a new file.";
        }

        if (string.IsNullOrEmpty(oldText))
        {
            return "Error: oldText must not be empty. Use write_file to create or fully replace a file.";
        }

        if (oldText == newText)
        {
            return "Error: oldText and newText are identical, so there is nothing to change.";
        }

        Encoding encoding;
        string content;
        using (var reader = new StreamReader(resolved, detectEncodingFromByteOrderMarks: true))
        {
            content = await reader.ReadToEndAsync(cancellationToken);
            encoding = reader.CurrentEncoding;
        }

        // Models write \n; a Windows file is usually \r\n, and an exact match would
        // otherwise fail on every multi-line edit.
        if (content.Contains("\r\n", StringComparison.Ordinal))
        {
            oldText = NormalizeToCrLf(oldText);
            newText = NormalizeToCrLf(newText);
        }

        int count = CountOccurrences(content, oldText);
        if (count == 0)
        {
            return $"Error: oldText was not found in {resolved}. It must match exactly, including indentation and " +
                "line breaks. Use read_file (with startLine and endLine) to copy the current text.";
        }

        if (count > 1 && replaceAll != true)
        {
            return $"Error: oldText matches {count} places in {resolved}. Include more surrounding lines so it is " +
                "unique, or set replaceAll to true to change every match.";
        }

        int firstIndex = content.IndexOf(oldText, StringComparison.Ordinal);
        int firstLine = content.AsSpan(0, firstIndex).Count('\n') + 1;

        string updated = replaceAll == true
            ? content.Replace(oldText, newText, StringComparison.Ordinal)
            : string.Concat(content.AsSpan(0, firstIndex), newText, content.AsSpan(firstIndex + oldText.Length));

        await File.WriteAllTextAsync(resolved, updated, encoding, cancellationToken);
        return count == 1
            ? $"Edited {resolved}: replaced 1 occurrence at line {firstLine}."
            : $"Edited {resolved}: replaced {count} occurrences, the first at line {firstLine}.";
    }

    public static string ListDirectory(string path)
    {
        string resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            return $"Error: directory not found: {resolved}";
        }

        var entries = new List<string>();
        foreach (string dir in Directory.EnumerateDirectories(resolved).OrderBy(d => d))
        {
            entries.Add($"{Path.GetFileName(dir)}/");
        }
        foreach (string file in Directory.EnumerateFiles(resolved).OrderBy(f => f))
        {
            entries.Add(Path.GetFileName(file));
        }

        return entries.Count == 0 ? "(empty directory)" : string.Join("\n", entries);
    }

    // Filename search by glob. A pattern with no slash matches file names anywhere
    // under the directory ("*.cs"); one with a slash matches the path relative to it
    // ("src/**/*.tsx").
    public static string FindFiles(string pattern, string? path)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Error: pattern must not be empty, for example \"*.cs\" or \"src/**/*.ts\".";
        }

        string root = ResolvePath(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(root))
        {
            return $"Error: directory not found: {root}";
        }

        Regex matcher = GlobToRegex(pattern);
        bool matchWholePath = pattern.Contains('/') || pattern.Contains('\\');

        var found = new List<string>();
        bool truncated = false;
        foreach (string file in EnumerateFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string candidate = matchWholePath ? relative : Path.GetFileName(file);
            if (!matcher.IsMatch(candidate))
            {
                continue;
            }

            if (found.Count >= MaxFoundPaths)
            {
                truncated = true;
                break;
            }

            found.Add(relative);
        }

        if (found.Count == 0)
        {
            return $"No files matching \"{pattern}\" under {root}.";
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        string result = string.Join("\n", found);
        return truncated ? result + $"\n\n[showing the first {MaxFoundPaths} matches - narrow the pattern or path]" : result;
    }

    // Content search (regular expression), like grep -rn. Skips binary files, big files
    // and dependency/build folders so results are code, not noise.
    public static string SearchFiles(string pattern, string? path, string? fileGlob, bool? ignoreCase)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return "Error: pattern must not be empty.";
        }

        Regex regex;
        try
        {
            regex = new Regex(
                pattern,
                (ignoreCase == true ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException exception)
        {
            return $"Error: pattern is not a valid regular expression: {exception.Message}";
        }

        string resolved = ResolvePath(string.IsNullOrWhiteSpace(path) ? "." : path);
        IEnumerable<string> files;
        string displayRoot;
        if (File.Exists(resolved))
        {
            files = [resolved];
            displayRoot = Path.GetDirectoryName(resolved) ?? resolved;
        }
        else if (Directory.Exists(resolved))
        {
            files = EnumerateFiles(resolved);
            displayRoot = resolved;
        }
        else
        {
            return $"Error: path not found: {resolved}";
        }

        Regex? nameFilter = string.IsNullOrWhiteSpace(fileGlob) ? null : GlobToRegex(fileGlob);
        var matches = new List<string>();
        int filesWithMatches = 0;
        bool truncated = false;

        foreach (string file in files)
        {
            if (nameFilter is not null && !nameFilter.IsMatch(Path.GetFileName(file)))
            {
                continue;
            }

            if (!TryReadSearchable(file, out string[]? lines))
            {
                continue;
            }

            bool fileHit = false;
            for (int i = 0; i < lines!.Length; i++)
            {
                bool hit;
                try
                {
                    hit = regex.IsMatch(lines[i]);
                }
                catch (RegexMatchTimeoutException)
                {
                    return "Error: the pattern took too long to match. Use a simpler regular expression.";
                }

                if (!hit)
                {
                    continue;
                }

                if (matches.Count >= MaxSearchMatches)
                {
                    truncated = true;
                    break;
                }

                fileHit = true;
                string text = lines[i].TrimEnd('\r');
                if (text.Length > MaxSearchLineLength)
                {
                    text = text[..MaxSearchLineLength] + "...";
                }

                matches.Add($"{Path.GetRelativePath(displayRoot, file).Replace('\\', '/')}:{i + 1}: {text.Trim()}");
            }

            if (fileHit)
            {
                filesWithMatches++;
            }

            if (truncated)
            {
                break;
            }
        }

        if (matches.Count == 0)
        {
            return $"No matches for /{pattern}/ under {resolved}.";
        }

        string header = $"{matches.Count} match(es) in {filesWithMatches} file(s) under {displayRoot}:\n";
        string body = string.Join("\n", matches);
        return truncated
            ? header + body + $"\n\n[stopped at {MaxSearchMatches} matches - narrow the pattern, path or fileGlob]"
            : header + body;
    }

    // Relative paths resolve against FLEET_FILES_ROOT if set, otherwise the backend's
    // own working directory - absolute paths are used exactly as given either way.
    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        string root = Environment.GetEnvironmentVariable("FLEET_FILES_ROOT") ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, path);
    }

    private static string Truncate(string content, string resolved, int totalCharacters)
    {
        if (content.Length <= MaxReadCharacters)
        {
            return content;
        }

        return content[..MaxReadCharacters] +
            $"\n\n[truncated: {resolved} is {totalCharacters} characters, showing the first {MaxReadCharacters}. " +
            "Use startLine and endLine to read the rest.]";
    }

    private static string NormalizeToCrLf(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    // Walks the tree ourselves so skipped folders are never descended into (a plain
    // recursive EnumerateFiles would still walk all of node_modules).
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            IEnumerable<string> subdirectories;
            IEnumerable<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (string subdirectory in subdirectories.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(subdirectory)))
                {
                    pending.Push(subdirectory);
                }
            }

            foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static bool TryReadSearchable(string file, out string[]? lines)
    {
        lines = null;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > MaxSearchFileBytes)
            {
                return false;
            }

            byte[] head = new byte[Math.Min(8192, (int)info.Length)];
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read = stream.Read(head, 0, head.Length);
                if (Array.IndexOf(head, (byte)0, 0, read) >= 0)
                {
                    return false;
                }
            }

            lines = File.ReadAllText(file).Split('\n');
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // ** crosses folders, * and ? stay inside one path segment.
    private static Regex GlobToRegex(string glob)
    {
        string normalized = glob.Replace('\\', '/');
        var pattern = new StringBuilder("^");
        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (c == '*')
            {
                if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    i++;
                    // "**/" also matches zero folders, so "src/**/x.ts" finds "src/x.ts".
                    if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    {
                        i++;
                        pattern.Append("(?:.*/)?");
                    }
                    else
                    {
                        pattern.Append(".*");
                    }
                }
                else
                {
                    pattern.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
