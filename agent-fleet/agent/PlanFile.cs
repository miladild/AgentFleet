using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentFleet;

internal sealed record PlanFileContent(
    string Title,
    string Goal,
    string? WorkingDirectory,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Risks,
    IReadOnlyList<PlanStepInput> Steps);

/// <summary>
/// Reads a plan written as Markdown in the fleet's plan format, without a model in between. Measured: asked to carry
/// out a detailed plan file, the planner kept every step, tier and check but cut each step's instructions to a
/// sentence or two, and the machine that runs a step sees only that step. Read here, every step keeps its
/// instructions exactly as written.
///
/// <code>
/// # Title
/// Working directory: `C:\src\project`      (optional; else the file's folder)
/// Goal: what the plan achieves.            (optional; else the title)
/// Decisions: (or Assumptions:)             (optional bullets)
/// Risks:                                   (optional bullets)
/// ## Step 1: Title
/// - Tier: heavy | standard | light
/// - Parallel group: name                   (optional)
/// - Files: `a.ts`, `b.ts`
/// - Check: `npm test`
/// Everything after those lines is the step's instructions.
/// </code>
/// </summary>
internal static partial class PlanFile
{
    public const long MaxBytes = 200_000;

    public static PlanFileContent Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            throw new FormatException("give the plan file's full path.");
        }

        string full = Path.GetFullPath(path.Trim());
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new FormatException($"{full} does not exist on the hub.");
        }

        if (info.Extension.ToLowerInvariant() is not (".md" or ".markdown" or ".txt"))
        {
            throw new FormatException($"{full} is not a Markdown or text file.");
        }

        if (info.Length > MaxBytes)
        {
            throw new FormatException($"{full} is larger than {MaxBytes / 1000} KB.");
        }

        return Parse(File.ReadAllText(full), info.DirectoryName, Path.GetFileNameWithoutExtension(full));
    }

    /// <summary>
    /// The plan file a propose_plan call is copying, when the planner did not say so itself. Measured: told to pass
    /// planFile, the planner read the file and still retyped the steps, shortening each one. A call with no planFile
    /// whose step count matches a fleet-format plan file that the user named, or the planner read, in this
    /// conversation takes its steps from that file instead. Null when there is no such file.
    /// </summary>
    public static string? FindTranscribed(IEnumerable<ChatMessage> messages, IDictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("planFile", out object? given) && !string.IsNullOrWhiteSpace(given?.ToString()))
        {
            return null;
        }

        int proposed = arguments.TryGetValue("steps", out object? steps)
            ? PlanTools.ToSteps(steps switch
            {
                JsonElement element => element,
                null => null,
                _ => JsonSerializer.SerializeToElement(steps)
            }).Count
            : 0;
        if (proposed == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChatMessage message in messages.Reverse())
        {
            foreach (string path in MentionedFiles(message))
            {
                if (seen.Add(path) && TryReadFleetPlan(path)?.Steps.Count == proposed)
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The fleet-format plan file the user's latest message names, until the planner has proposed a plan for it.
    /// Measured: told to hand such a file over straight away, the planner still read 13 of the project's files first
    /// (101 s on the hub), and a weaker planner can loop there and never propose.
    /// </summary>
    public static (string Path, int Steps)? NamedInLatestRequest(IReadOnlyList<ChatMessage> messages)
    {
        int latest = LatestRequest(messages);
        return latest < 0 || messages.Skip(latest + 1).SelectMany(message => message.Contents)
                .Any(content => content is FunctionCallContent { Name: "propose_plan" })
            ? null
            : NamedIn(messages[latest]);
    }

    /// <summary>
    /// The fleet-format plan file the user's latest message names, for a propose_plan call in answer to it. Measured:
    /// even told to hand the file over, the planner once read ten project files and proposed a two-step plan of its
    /// own for the file's first step, and FindTranscribed let it through because the step counts differed. A user who
    /// names a plan file wants that plan, so the call gets the file's steps whatever the planner typed.
    /// </summary>
    public static string? NamedByLatestRequest(IReadOnlyList<ChatMessage> messages, IDictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("planFile", out object? given) && !string.IsNullOrWhiteSpace(given?.ToString()))
        {
            return null;
        }

        int latest = LatestRequest(messages);
        return latest < 0 ? null : NamedIn(messages[latest])?.Path;
    }

    private static int LatestRequest(IReadOnlyList<ChatMessage> messages)
    {
        for (int index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.User)
            {
                return index;
            }
        }

        return -1;
    }

    private static (string Path, int Steps)? NamedIn(ChatMessage request)
    {
        foreach (string path in MentionedFiles(request).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryReadFleetPlan(path) is { } plan)
            {
                return (path, plan.Steps.Count);
            }
        }

        return null;
    }

    // A plan file in the fleet's format: at least half its steps carry a tier or a check.
    private static PlanFileContent? TryReadFleetPlan(string path)
    {
        try
        {
            PlanFileContent plan = Read(path);
            int withFields = plan.Steps.Count(step => step.Verify is not null || step.Tier is not null);
            return withFields * 2 >= plan.Steps.Count ? plan : null;
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            // Not a plan file.
            return null;
        }
    }

    // Markdown files named in what the user wrote, and files the planner read.
    private static IEnumerable<string> MentionedFiles(ChatMessage message)
    {
        foreach (AIContent content in message.Contents)
        {
            if (message.Role == ChatRole.User && content is TextContent text)
            {
                foreach (Match match in MarkdownPath().Matches(text.Text ?? string.Empty))
                {
                    yield return match.Value.TrimEnd('.', ',', ';', ':', ')');
                }
            }
            else if (content is FunctionCallContent { Name: "read_file" } call &&
                     call.Arguments?.TryGetValue("path", out object? path) == true &&
                     path?.ToString() is { } file &&
                     file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    public static PlanFileContent Parse(string markdown, string? defaultWorkingDirectory, string fallbackTitle = "Plan")
    {
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int firstStep = Array.FindIndex(lines, line => StepHeading().IsMatch(line));
        if (firstStep < 0)
        {
            throw new FormatException("it has no \"## Step 1: title\" sections.");
        }

        string[] preamble = lines[..firstStep];
        string title = preamble.Select(line => TitleHeading().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value.Trim()
                       ?? fallbackTitle;
        title = PlanPrefix().Replace(title, string.Empty).Trim();
        if (title.Length > 0 && char.IsLower(title[0]))
        {
            title = char.ToUpperInvariant(title[0]) + title[1..];
        }

        string? workingDirectory = preamble.Select(line => WorkingDirectoryLine().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value.Trim()
                                   ?? defaultWorkingDirectory;
        string goal = Paragraph(preamble, "goal") is { Length: > 0 } text ? text : title;
        List<string> assumptions = [.. Bullets(preamble, "decisions"), .. Bullets(preamble, "assumptions")];
        List<string> risks = Bullets(preamble, "risks");

        var steps = new List<PlanStepInput>();
        int index = firstStep;
        while (index < lines.Length)
        {
            Match heading = StepHeading().Match(lines[index]);
            int end = index + 1;
            while (end < lines.Length && !SectionHeading().IsMatch(lines[end]))
            {
                end++;
            }

            steps.Add(Step(heading.Groups[1].Value.Trim(), lines[(index + 1)..end]));

            // Any other "## " section after the steps (notes, an appendix) is not a step.
            index = end;
            while (index < lines.Length && !StepHeading().IsMatch(lines[index]))
            {
                index++;
            }
        }

        return new PlanFileContent(title, goal, workingDirectory, assumptions, risks, steps);
    }

    private static PlanStepInput Step(string title, string[] body)
    {
        string? tier = null, group = null, check = null;
        string[] files = [];
        int line = 0;
        while (line < body.Length && string.IsNullOrWhiteSpace(body[line]))
        {
            line++;
        }

        // The field lines at the top of the section; the rest is the instructions, kept as written.
        for (; line < body.Length; line++)
        {
            Match field = FieldLine().Match(body[line]);
            if (!field.Success)
            {
                break;
            }

            string value = field.Groups[2].Value.Trim();
            switch (field.Groups[1].Value.ToLowerInvariant().Replace(" ", string.Empty))
            {
                case "tier":
                    tier = Unquote(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
                    break;
                case "parallelgroup":
                    group = NoneToNull(Unquote(value));
                    break;
                case "files":
                    files = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(Unquote).Where(file => NoneToNull(file) is not null).ToArray();
                    break;
                default:
                    check = NoneToNull(Unquote(value));
                    break;
            }
        }

        string detail = string.Join('\n', body[line..]).Trim();
        return new PlanStepInput(title, detail.Length > 0 ? detail : title, files, check, tier, group);
    }

    // The text after "Goal:" up to the next blank line.
    private static string Paragraph(string[] lines, string label)
    {
        int start = Array.FindIndex(lines, line => LabelLine(label).IsMatch(line));
        if (start < 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder(LabelLine(label).Replace(lines[start], string.Empty).Trim());
        for (int line = start + 1; line < lines.Length && !string.IsNullOrWhiteSpace(lines[line]); line++)
        {
            text.Append(' ').Append(lines[line].Trim());
        }

        return text.ToString().Trim();
    }

    // The "- " bullets under "Decisions:"; an indented line continues the bullet above it.
    private static List<string> Bullets(string[] lines, string label)
    {
        var items = new List<string>();
        int start = Array.FindIndex(lines, line => LabelLine(label).IsMatch(line));
        if (start < 0)
        {
            return items;
        }

        for (int line = start + 1; line < lines.Length; line++)
        {
            if (Bullet().Match(lines[line]) is { Success: true } bullet)
            {
                items.Add(bullet.Groups[1].Value.Trim());
            }
            else if (items.Count > 0 && lines[line].StartsWith(' ') && !string.IsNullOrWhiteSpace(lines[line]))
            {
                items[^1] += " " + lines[line].Trim();
            }
            else if (items.Count > 0 || !string.IsNullOrWhiteSpace(lines[line]))
            {
                break;
            }
        }

        return items;
    }

    private static string Unquote(string value)
    {
        string text = value.Trim();
        while (text.Length >= 2 && text[0] == '`' && text[^1] == '`')
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    private static string? NoneToNull(string value) =>
        value.Trim().TrimEnd('.').ToLowerInvariant() is "" or "none" or "-" or "n/a" ? null : value.Trim();

    private static Regex LabelLine(string label) =>
        new($@"^\s*(?:\*\*)?{label}(?:\*\*)?\s*:(?:\*\*)?", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"^#\s+(.+)$")]
    private static partial Regex TitleHeading();

    [GeneratedRegex(@"^plan\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PlanPrefix();

    [GeneratedRegex(@"^##\s+Step\s+\d+\s*[:.)\-]?\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex StepHeading();

    [GeneratedRegex(@"^#{1,2}\s")]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^\s*(?:\*\*)?Working directory(?:\*\*)?\s*:\s*(?:\*\*)?\s*`?([^`]+?)`?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WorkingDirectoryLine();

    [GeneratedRegex(@"^\s*[-*]\s*(?:\*\*)?(Tier|Parallel group|Files|Check|Verify)(?:\*\*)?\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"^\s*[-*]\s+(.+)$")]
    private static partial Regex Bullet();

    // A drive path (C:\src\PLAN.md) or a rooted Unix path, but not the path part of a URL.
    [GeneratedRegex(@"(?:(?<![\w/])[A-Za-z]:[\\/]|(?<![\w:/])/)[^\s""'`<>|*?]+?\.md\b", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownPath();
}
