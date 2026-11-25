using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentFleet;

internal static partial class FailureHints
{
    private const long MaxTestFileBytes = 512 * 1024;
    private const int MaxLinesLookedBack = 200;
    private const int MaxCallCharacters = 2000;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    // node's assert prints the two numbers of a failed strictEqual as "404 !== 204". A 404 (or 405) where a 2xx was expected.
    [GeneratedRegex(@"\b(?<actual>40[45]) !== (?<expected>2[0-9]{2})\b", RegexOptions.None, 1000)]
    private static partial Regex StatusMismatch();

    // The first stack frame in a test file: "at TestContext.<anonymous> (test\api.test.ts:147:12)".
    [GeneratedRegex(@"\((?<file>[^()\r\n]+?\.(?:test|spec)\.[cm]?[jt]sx?):(?<line>[0-9]{1,7}):[0-9]+\)", RegexOptions.None, 1000)]
    private static partial Regex TestFrame();

    // "res1.status" on the failing assertion line.
    [GeneratedRegex(@"\b(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\.status\b", RegexOptions.None, 1000)]
    private static partial Regex StatusRead();

    /// <summary>
    /// A test that calls fetch without a method sends a GET. When the route is registered for another verb (DELETE, POST, PUT) the
    /// server answers 404, and the check prints "404 !== 204" at an assertion on that response's status. The code is then right and the
    /// test is wrong, but nothing in the output says so. The hint is given only when the failing test's own file, in the project
    /// folder, really has a fetch call with no method behind the response the failing line reads; otherwise there is none.
    /// </summary>
    private static string? MissingHttpVerb(string failure, string? projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder))
        {
            return null;
        }

        try
        {
            if (StatusMismatch().Match(failure) is not { Success: true } status ||
                TestFrame().Match(failure, status.Index) is not { Success: true } frame ||
                !int.TryParse(frame.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int line) || line < 1)
            {
                return null;
            }

            string file = frame.Groups["file"].Value.Trim();
            if (ReadTestFile(projectFolder, file) is not { } text)
            {
                return null;
            }

            int[] starts = LineStarts(text);
            if (line > starts.Length)
            {
                return null;
            }

            string failingLine = LineText(text, starts, line - 1);
            if (StatusRead().Match(failingLine) is not { Success: true } read)
            {
                return null;
            }

            string name = read.Groups["name"].Value;
            var declaration = new Regex($@"\b(?:const|let|var)\s+{Regex.Escape(name)}\s*=\s*(?:await\s+)?fetch\s*\(", RegexOptions.None, PatternTimeout);
            for (int index = line - 2; index >= 0 && index >= line - 2 - MaxLinesLookedBack; index--)
            {
                Match found = declaration.Match(LineText(text, starts, index));
                if (!found.Success)
                {
                    continue;
                }

                int open = starts[index] + found.Index + found.Length - 1;
                if (FetchCallSetsNoMethod(text, open))
                {
                    return $"Hint: the check expects status {status.Groups["expected"].Value} but gets {status.Groups["actual"].Value}, and `{name}` " +
                           $"(read on line {line} of {file}) comes from a fetch call on line {index + 1} that has no method option. " +
                           "A fetch without a method sends GET. If that route is registered for another verb (app.delete, app.post, app.put), " +
                           "the server answers a GET with 404. Give that fetch call the verb the route uses, for example " +
                           "fetch(url, { method: \"DELETE\" }), before you change the code.";
                }

                return null;
            }

            return null;
        }
        catch (Exception exception) when (exception is RegexMatchTimeoutException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The text of a test file named in a stack frame, or null when the name leaves the project folder or the file cannot be read.</summary>
    private static string? ReadTestFile(string projectFolder, string file)
    {
        string relative = file.Replace('\\', '/');
        if (relative.Contains(':') || Path.IsPathRooted(relative) || relative.Split('/').Contains(".."))
        {
            return null;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectFolder));
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var info = new FileInfo(path);
        return info.Exists && info.Length <= MaxTestFileBytes ? File.ReadAllText(path) : null;
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return [.. starts];
    }

    private static string LineText(string text, int[] starts, int index)
    {
        int end = index + 1 < starts.Length ? starts[index + 1] : text.Length;
        return text[starts[index]..end].TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Whether the fetch call whose opening bracket is at <paramref name="open"/> has no options at all, or an object literal without
    /// a method. Options passed in a variable, or a call that cannot be read to its closing bracket, are left alone.
    /// </summary>
    private static bool FetchCallSetsNoMethod(string text, int open)
    {
        int depth = 0;
        int firstComma = -1;
        int limit = Math.Min(text.Length, open + MaxCallCharacters);
        for (int index = open; index < limit; index++)
        {
            char character = text[index];
            if (character is '\'' or '"' or '`')
            {
                index = EndOfString(text, index, limit);
                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            if (character is '(' or '[' or '{')
            {
                depth++;
            }
            else if (character is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    if (firstComma < 0)
                    {
                        return true;
                    }

                    string options = text[(firstComma + 1)..index].Trim();
                    return options.StartsWith('{') && !Regex.IsMatch(options, @"\bmethod\b", RegexOptions.None, PatternTimeout);
                }
            }
            else if (character == ',' && depth == 1 && firstComma < 0)
            {
                firstComma = index;
            }
        }

        return false;
    }

    /// <summary>The index of the quote that closes the string starting at <paramref name="start"/>, or -1 when it does not close in range.</summary>
    private static int EndOfString(string text, int start, int limit)
    {
        char quote = text[start];
        for (int index = start + 1; index < limit; index++)
        {
            if (text[index] == '\\')
            {
                index++;
            }
            else if (text[index] == quote)
            {
                return index;
            }
        }

        return -1;
    }
}
