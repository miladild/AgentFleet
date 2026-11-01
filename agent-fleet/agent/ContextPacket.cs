using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>A file of the packet: its text, and the lines of it a stack frame names (marked when the file is shown).</summary>
internal sealed record PacketFile(string Path, string Content, IReadOnlyCollection<int>? FailingLines = null);

/// <summary>Everything a repair round is told, already read: the packet puts it in order and fits it in the limit.</summary>
/// <param name="StepText">What the step asks for and the constraints on it.</param>
/// <param name="WritablePaths">The files the step may write.</param>
/// <param name="FailingTests">The test files that fail, shown whole.</param>
/// <param name="WritableFiles">The files the step may write, as they are now.</param>
/// <param name="Diff">What changed since the step began; empty when nothing did.</param>
/// <param name="Reverted">The round the runner undid, if the last one was.</param>
/// <param name="Tried">One line per earlier round, oldest first.</param>
/// <param name="Output">The failures, already split (see FailureReport).</param>
internal sealed record ContextPacketInput(
    string StepText,
    IReadOnlyList<string> WritablePaths,
    IReadOnlyList<PacketFile> FailingTests,
    IReadOnlyList<PacketFile> WritableFiles,
    string Diff,
    RollbackNote? Reverted,
    IReadOnlyList<string> Tried,
    string Output);

/// <summary>
/// The message every repair round starts with, in one order that runs from what stays the same between rounds to what changes, so the
/// start of it can be reused by a model that keeps what it has already read. A failing test is never shortened; the files that are
/// not failing are, largest first, to keep the packet in its limit.
/// </summary>
internal static class ContextPacket
{
    private const int MaxTried = 5;
    private const int MinKeptLines = 20;
    private const int MaxListed = 8;

    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    public static string Build(ContextPacketInput input, int maxTokens)
    {
        var shown = input.WritableFiles
            .Where(file => !input.FailingTests.Any(test => string.Equals(test.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
            .Select(file => new Section(file.Path, file.Content))
            .ToList();
        var diff = new Section("the diff", input.Diff);
        var shortened = new List<Section>(shown) { diff };

        string packet = Compose(input, shown, diff);
        while (EstimateTokens(packet) > maxTokens)
        {
            Section? largest = shortened
                .Where(section => section.Shown > MinKeptLines)
                .OrderByDescending(section => section.Text.Length)
                .FirstOrDefault();
            if (largest is null)
            {
                break;
            }

            largest.Shorten(Math.Max(MinKeptLines, largest.Shown / 2));
            packet = Compose(input, shown, diff);
        }

        return packet;
    }

    private sealed class Section(string name, string text)
    {
        private readonly string _original = text;
        private int _kept = int.MaxValue;

        public string Name { get; } = name;

        private int LineCount => Lines(_original).Length;

        /// <summary>How many lines are shown now.</summary>
        public int Shown => Math.Min(LineCount, _kept);

        public string Text => _kept >= LineCount ? _original : string.Join('\n', Lines(_original).Take(_kept));

        public int Hidden => LineCount - Shown;

        public void Shorten(int keep) => _kept = keep;

        private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    }

    private static string Compose(ContextPacketInput input, List<Section> files, Section diff)
    {
        var text = new StringBuilder();
        text.AppendLine("STEP");
        text.AppendLine(input.StepText.Trim());
        if (input.WritablePaths.Count > 0)
        {
            text.AppendLine($"Files this step may write: {string.Join(", ", input.WritablePaths)}.");
        }

        if (input.FailingTests.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("FAILING TESTS (whole files; the lines the failures point at start with >>>)");
            foreach (PacketFile test in input.FailingTests)
            {
                text.AppendLine($"--- {test.Path}");
                string[] lines = test.Content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    text.AppendLine((test.FailingLines?.Contains(index + 1) == true ? ">>> " : "  ") + lines[index]);
                }
            }
        }

        if (files.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("FILES YOU MAY WRITE (as they are now)");
            foreach (Section file in files)
            {
                text.AppendLine($"--- {file.Name}");
                text.AppendLine(file.Text);
                if (file.Hidden > 0)
                {
                    text.AppendLine($"[... {file.Hidden} lines of {file.Name} are not shown]");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(diff.Text))
        {
            text.AppendLine();
            text.AppendLine("CHANGED SINCE THE STEP BEGAN");
            text.AppendLine(diff.Text);
            if (diff.Hidden > 0)
            {
                text.AppendLine($"[... {diff.Hidden} lines of the diff are not shown]");
            }
        }

        if (input.Reverted is { } reverted)
        {
            text.AppendLine();
            text.AppendLine(Note(reverted));
        }

        if (input.Tried.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("ALREADY TRIED (do not repeat these)");
            foreach (string line in input.Tried.TakeLast(MaxTried))
            {
                text.AppendLine($"- {line}");
            }
        }

        text.AppendLine();
        text.AppendLine(input.Output.Trim());
        return text.ToString();
    }

    // Said the way the runner undid it: which files, and how the failures went. Nothing that was undone is described as if it still stood.
    private static string Note(RollbackNote reverted)
    {
        string files = reverted.Restored.Count == 0
            ? "your edits"
            : $"your edits to {(reverted.Restored.Count <= MaxListed ? string.Join(", ", reverted.Restored) : $"{string.Join(", ", reverted.Restored.Take(MaxListed))} and {reverted.Restored.Count - MaxListed} more")}";
        return $"NOTE: {files} were reverted because failures increased from {reverted.Best.Describe()} to {reverted.Worse.Describe()}. " +
               "The files are back to the content shown above.";
    }
}

/// <summary>
/// A check's failures split by where they come from, so a model can tell a test the step broke from a test the step wrote: the
/// tests that existed before the step (a regression), the tests the step created (the feature), the compiler's errors apart, and then
/// the output as printed.
/// </summary>
internal static partial class FailureReport
{
    private const int MaxLineCharacters = 200;

    [GeneratedRegex(@"\berror\s+(?:CS|TS|MSB|NETSDK)\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerLine();

    /// <param name="output">What the check printed.</param>
    /// <param name="testFiles">The project's test files and their text.</param>
    /// <param name="existedAtStart">The test files that were there when the step began (paths as in testFiles).</param>
    public static string Build(string output, IReadOnlyList<(string Path, string Content)> testFiles, IReadOnlySet<string> existedAtStart)
    {
        string[] lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] compiler = lines.Where(line => CompilerLine().IsMatch(line)).Select(line => line.Trim()).ToArray();
        string[] names = PlanFailure.FailingItems(output).Where(name => !PlanFailure.IsBuildErrorCode(name)).ToArray();
        if (names.Length == 0 && compiler.Length == 0)
        {
            return output;
        }

        var regression = new List<string>();
        var feature = new List<string>();
        var other = new List<string>();
        foreach (string name in names)
        {
            (string Path, string Content)? owner = testFiles.Cast<(string Path, string Content)?>()
                .FirstOrDefault(file => file!.Value.Content.Contains(name, StringComparison.OrdinalIgnoreCase));
            string entry = $"- {name}{(owner is null ? string.Empty : $" ({owner.Value.Path})")}";
            if (owner is null)
            {
                other.Add(entry);
            }
            else if (existedAtStart.Contains(owner.Value.Path))
            {
                regression.Add(entry);
            }
            else
            {
                feature.Add(entry);
            }
        }

        var report = new StringBuilder();
        Add(report, "REGRESSION FAILURES (tests that existed and passed before this step)", regression);
        Add(report, "FEATURE FAILURES (tests created in this step or its paired test step)", feature);
        Add(report, "OTHER FAILURES (no test file names them)", other);
        Add(report, "TYPE ERRORS", compiler.Select(line => Cut(line)).ToList());
        // Nothing the check printed is left out: the split above says which failure is old and which is new, the output says the rest.
        report.AppendLine();
        report.AppendLine("CHECK OUTPUT (as printed):");
        report.Append(output.Trim());
        return report.ToString().TrimEnd();
    }

    private static void Add(StringBuilder report, string heading, List<string> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (report.Length > 0)
        {
            report.AppendLine();
        }

        report.AppendLine($"{heading}:");
        foreach (string entry in entries)
        {
            report.AppendLine(entry);
        }
    }

        private static string Cut(string line) => line.Length > MaxLineCharacters ? line[..MaxLineCharacters] + " ..." : line;
}
