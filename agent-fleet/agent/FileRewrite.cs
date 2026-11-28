using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>A file of a rewrite request: its path in the project, and its text, or null when it does not exist yet.</summary>
internal sealed record RewriteFile(string Path, string? Text);

/// <summary>
/// A stuck step on small files, asked for whole: one request with the step's text, its files, what the check printed and what the
/// earlier rounds led to, answered with the complete new files and no tool call. Measured on four states a step was stuck in for
/// many rounds of tool use: the same model fixed three of them this way, one in its first answer.
/// </summary>
internal static partial class FileRewrite
{
    /// <summary>The most an answer may run to: two files of a few hundred lines each.</summary>
    public const int AnswerTokens = 6144;

    /// <summary>
    /// The start of the answer, sent as the model's own words, so that it goes on with the files. Measured: asked only in words not
    /// to explain, a worker's answers ran to their cap as analysis and wrote nothing in three of four rounds, eight minutes each;
    /// with this start the answer was the files.
    /// </summary>
    public const string AnswerStart = "FILE: ";

    /// <summary>The reply as a whole answer: what goes on from the start, or the reply as it is when it starts the answer again.</summary>
    public static string Continued(string? reply)
    {
        string text = reply ?? string.Empty;
        return text.TrimStart().StartsWith("FILE:", StringComparison.Ordinal) ? text : AnswerStart + text.TrimStart(' ', '\t');
    }

    /// <summary>The check's output is cut to this many characters, keeping its start and its end.</summary>
    internal const int MaxOutputCharacters = 6000;

    public const string Instructions =
        "You fix the files of one step of a programming task. Reply with the complete new content of every file you change, and " +
        "nothing of a file you do not change. Write each one as a line 'FILE: <path>' followed by one fenced code block holding the " +
        "whole file. Do not analyse or explain: your reply starts with 'FILE:'.";

    [GeneratedRegex(@"^[ \t]*FILE:[ \t]*[`*]*([^\r\n`*]+?)[`*]*[ \t]*\r?\n(?:[ \t]*\r?\n)*[ \t]*```[^\r\n]*\r?\n(.*?)\r?\n[ \t]*```[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FileBlock();

    /// <summary>The files a reply holds whole: a block cut off before its closing fence is left out. The last of a path wins.</summary>
    public static IReadOnlyList<RewriteFile> Parse(string? reply)
    {
        var files = new Dictionary<string, RewriteFile>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in FileBlock().Matches((reply ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n"))
        {
            string path = Normalize(block.Groups[1].Value);
            files[path] = new RewriteFile(path, block.Groups[2].Value + "\n");
        }

        return [.. files.Values];
    }

    /// <summary>A path as the step lists it: forward slashes, no leading "./".</summary>
    public static string Normalize(string path)
    {
        string normal = path.Trim().Replace('\\', '/');
        while (normal.StartsWith("./", StringComparison.Ordinal))
        {
            normal = normal[2..];
        }

        return normal;
    }

    /// <summary>The request: the task, the files to write, files to read only, what the check printed and what was tried.</summary>
    public static string Prompt(PlanStep step, IReadOnlyList<RewriteFile> files, IReadOnlyList<RewriteFile> readOnly, string failure,
        IReadOnlyList<string> tried, string? onlyRewritten = null)
    {
        var text = new StringBuilder();
        text.AppendLine("The task:");
        text.AppendLine($"{step.Title}\n{step.Detail}".Trim());
        text.AppendLine();
        text.AppendLine("The files you may write, as they are now:");
        foreach (RewriteFile file in files)
        {
            Append(text, file);
        }

        if (readOnly.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Files the check's output names, to read only. Do not write them:");
            foreach (RewriteFile file in readOnly)
            {
                Append(text, file);
            }
        }

        text.AppendLine();
        text.AppendLine(step.Verify is null ? "What went wrong:" : $"The check `{step.Verify}` fails. What it printed:");
        text.AppendLine(Shorten(failure.Trim()));
        if (tried.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("What the earlier rounds led to:");
            foreach (string line in tried.TakeLast(5))
            {
                text.AppendLine($"- {line}");
            }
        }

        if (onlyRewritten is not null)
        {
            // Measured: nine rounds rewrote a step's test while the fault was in its code.
            text.AppendLine();
            text.AppendLine($"The last rounds rewrote only {onlyRewritten} and the check got no better. Look at the step's other files " +
                            "against the task: the fault may be there.");
        }

        text.AppendLine();
        text.Append("Fix the cause. The code or the test can be wrong: the task text decides. Do not analyse or explain: your reply starts with 'FILE:'.");
        return text.ToString();
    }

    private static void Append(StringBuilder text, RewriteFile file)
    {
        if (file.Text is null)
        {
            text.AppendLine($"FILE: {file.Path} (does not exist yet)");
            return;
        }

        text.AppendLine($"FILE: {file.Path}");
        text.AppendLine("```");
        text.AppendLine(file.Text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
        text.AppendLine("```");
    }

    private static string Shorten(string output) => output.Length <= MaxOutputCharacters
        ? output
        : $"{output[..(MaxOutputCharacters / 3)]}\n[...]\n{output[^(MaxOutputCharacters * 2 / 3)..]}";
}
