using System.Text;

namespace AgentFleet;

/// <summary>
/// Reads a plan step's project folder and puts the repair packet together (see ContextPacket): the step, the failing tests whole, the
/// files the step may write as they are now, what changed in them since the step began, what was undone, what was tried, and the
/// failures split into old and new. Only text files inside the project are read, none through a link, none of a dependency folder.
/// </summary>
internal static class RepairPacket
{
    private const long MaxFileBytes = 200_000;
    private const int MaxWritableFiles = 40;
    private const int MaxTestFiles = 200;
    private const int MaxDiffLinesPerFile = 60;

    /// <summary>The text of the files the step lists that exist now: what the step began with, so the packet can say what changed.</summary>
    public static IReadOnlyDictionary<string, string> ReadStepFiles(PlanRecord plan, PlanStep step)
    {
        var content = new Dictionary<string, string>(PathComparer);
        foreach (string file in WritableFiles(plan, step))
        {
            if (TryRead(plan.WorkingDirectory, file) is { } text)
            {
                content[file] = text;
            }
        }

        return content;
    }

    /// <param name="filesAtStart">Every file of the project when the step began (full paths).</param>
    /// <param name="contentAtStart">The step's files as they were then (see ReadStepFiles).</param>
    /// <param name="tried">One line per earlier round, oldest first.</param>
    public static string Build(
        PlanRecord plan,
        PlanStep step,
        string failure,
        IReadOnlySet<string> filesAtStart,
        IReadOnlyDictionary<string, string> contentAtStart,
        RollbackNote? reverted,
        IReadOnlyList<string> tried,
        int maxTokens)
    {
        string root = plan.WorkingDirectory ?? string.Empty;
        string[] writable = WritableFiles(plan, step).ToArray();

        var failing = new List<PacketFile>();
        foreach (IGrouping<string, (string Path, int Line)> group in FailureLocation.ProjectFrames(failure, root).GroupBy(frame => frame.Path, PathComparer))
        {
            if (PlanRunner.IsTestPath(group.Key) && TryRead(root, Full(root, group.Key)) is { } text)
            {
                failing.Add(new PacketFile(group.Key, text, group.Select(frame => frame.Line).ToHashSet()));
            }
        }

        var shown = new List<PacketFile>();
        var diff = new StringBuilder();
        foreach (string file in writable)
        {
            if (TryRead(root, file) is not { } now)
            {
                continue;
            }

            string relative = Relative(root, file);
            shown.Add(new PacketFile(relative, now));
            AppendChange(diff, relative, file, now, filesAtStart, contentAtStart);
        }

        var testFiles = new List<(string Path, string Content)>();
        var existedAtStart = new HashSet<string>(PathComparer);
        foreach (string file in FleetPlanContext.SnapshotFiles(root).Where(file => PlanRunner.IsTestPath(Relative(root, file))).Take(MaxTestFiles))
        {
            if (TryRead(root, file) is { } text)
            {
                string relative = Relative(root, file);
                testFiles.Add((relative, text));
                if (filesAtStart.Contains(file))
                {
                    existedAtStart.Add(relative);
                }
            }
        }

        var stepText = new StringBuilder($"{step.Title}\n{step.Detail}");
        if (step.Verify is not null)
        {
            stepText.Append($"\nThe approved check: `{step.Verify}`");
        }

        return ContextPacket.Build(new ContextPacketInput(stepText.ToString(), step.Files, failing, shown, diff.ToString().TrimEnd(), reverted, tried,
            FailureReport.Build(failure, testFiles, existedAtStart)), maxTokens);
    }

    private static void AppendChange(StringBuilder diff, string relative, string full, string now, IReadOnlySet<string> filesAtStart,
        IReadOnlyDictionary<string, string> contentAtStart)
    {
        if (!contentAtStart.TryGetValue(full, out string? before))
        {
            // A file that was there when the step began but was too big or unreadable has no start text to compare with.
            if (!filesAtStart.Contains(full))
            {
                diff.AppendLine($"{relative} (new file, {LineCount(now)} lines)");
            }

            return;
        }

        LineChange change = LineDiff.Compute(before, now);
        if (change.TooLarge)
        {
            diff.AppendLine($"{relative}: changed (too large to compare)");
            return;
        }

        if (change.Removed.Count == 0 && change.Added.Count == 0)
        {
            return;
        }

        diff.AppendLine($"{relative}: +{change.Added.Count} -{change.Removed.Count}");
        foreach (string line in change.Removed.Select(line => $"- {line}").Concat(change.Added.Select(line => $"+ {line}")).Take(MaxDiffLinesPerFile))
        {
            diff.AppendLine(line);
        }
    }

    private static int LineCount(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').Length;

    private static IEnumerable<string> WritableFiles(PlanRecord plan, PlanStep step)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return [];
        }

        string[] declared = FleetPlanContext.DeclaredPaths(plan, step).ToArray();
        return FleetPlanContext.SnapshotFiles(plan.WorkingDirectory)
            .Where(file => declared.Any(path => FleetPlanContext.Names(path, file)))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxWritableFiles);
    }

    // The text of a file inside the project, or null: missing, big, behind a link, not text, or unreadable.
    private static string? TryRead(string? root, string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (string.IsNullOrWhiteSpace(root) || !info.Exists || info.Length > MaxFileBytes || PlanRunner.HasLinkedPathComponent(Path.GetFullPath(root), full))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(full);
            return Array.IndexOf(bytes, (byte)0) >= 0 ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Full(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Relative(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
