namespace AgentFleet;

/// <summary>
/// The rung of a stuck step on small files: instead of a fresh conversation that works with tools, each round asks for the step's
/// files whole (see FileRewrite) and the runner writes them itself, only the ones the step lists. The check and everything after it
/// are as for any round.
/// </summary>
internal sealed partial class PlanRunner
{
    internal const string FileRewriteEvent = "file-rewrite";

    private const int MaxRewriteFiles = 8;
    private const long MaxRewriteFileBytes = 200_000;

    private int RoundsAllowed(bool rewrite) => rewrite ? _harness.RewriteRounds : _roundsPerRung;

    // Only the fresh rung, only a step with a check whose listed files are plain files, small enough for the answering machine's window.
    private bool Rewrites(PlanRecord plan, PlanStep step, int rung, string? node)
    {
        if (rung != RepairLadder.FreshRung || !_harness.EnableFileRewrite || step.Verify is null || IsTestsStep(step) ||
            string.IsNullOrWhiteSpace(plan.WorkingDirectory) || step.Files.Count is 0 or > MaxRewriteFiles)
        {
            return false;
        }

        int budget = MachineProfile.RewriteFileBudget(ContextWindowOf(node, step.Tier));
        if (budget == 0)
        {
            return false;
        }

        long characters = 0;
        foreach (string listed in step.Files)
        {
            if (listed.IndexOfAny(['*', '?']) >= 0 || RewritePath(plan.WorkingDirectory, listed) is not { } full || Directory.Exists(full))
            {
                return false;
            }

            if (File.Exists(full))
            {
                characters += new FileInfo(full).Length;
            }
        }

        return characters <= budget;
    }

    // The full path of a file the step lists, or null when it is not a path inside the project.
    private static string? RewritePath(string root, string listed)
    {
        string normal = FileRewrite.Normalize(listed);
        if (normal.Length == 0 || normal.EndsWith('/') || Path.IsPathRooted(normal) || normal.Split('/').Contains(".."))
        {
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(root, normal));
        return HasLinkedPathComponent(Path.GetFullPath(root), full) ? null : full;
    }

    private string RewriteMessage(PlanRecord plan, PlanStep step, string failure)
    {
        string root = plan.WorkingDirectory!;
        var files = new List<RewriteFile>();
        foreach (string listed in step.Files)
        {
            files.Add(new RewriteFile(FileRewrite.Normalize(listed), ReadRewriteFile(RewritePath(root, listed))));
        }

        // The failing files the step does not list (a test of an earlier step), so the model can see what they ask for.
        RewriteFile[] readOnly = FailureLocation.ProjectFrames(failure, root)
            .Select(frame => frame.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => IsTestPath(path) && !files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .Select(path => new RewriteFile(path, ReadRewriteFile(RewritePath(root, path))))
            .Where(file => file.Text is not null)
            .ToArray();
        return FileRewrite.Prompt(step, files, readOnly, failure, RepairMessages.TriedLines(StepEventsSinceRetry(plan.Id, step.Id)));
    }

    private static string? ReadRewriteFile(string? full)
    {
        try
        {
            return full is not null && File.Exists(full) && new FileInfo(full).Length <= MaxRewriteFileBytes ? File.ReadAllText(full) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Writes the whole files of a reply where the step's work happens (the worker's copy, or the project on the hub). A file the step
    // does not list is not written; what was and was not written is logged.
    private async Task<StepAgentReply> WriteRewriteAsync(PlanRecord plan, PlanStep step, int attempt, string tier, string? machine,
        string? modelMachine, int rung, int round, StepAgentReply reply, CancellationToken cancellationToken)
    {
        var written = new List<string>();
        var left = new List<string>();
        string[] listed = step.Files.Select(FileRewrite.Normalize).ToArray();
        foreach (RewriteFile file in FileRewrite.Parse(reply.Text))
        {
            string? path = listed.FirstOrDefault(candidate => candidate.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (path is null || RewritePath(plan.WorkingDirectory!, path) is not { } full)
            {
                left.Add($"{file.Path} (not a file of this step)");
                continue;
            }

            string target = WorkerWorkspaceContext.Current is null ? full : path;
            string result = await WorkspaceTools.WriteFileAsync(target, file.Text!, cancellationToken);
            if (result.StartsWith("Wrote", StringComparison.Ordinal))
            {
                written.Add(path);
                _store.AddEvent(plan.Id, step.Id, attempt, ChangingToolEvent, tier, machine, "write_file", modelNode: modelMachine,
                    workspaceNode: machine, rung: rung, round: round);
            }
            else
            {
                left.Add($"{path} ({Cut(result, 120)})");
            }
        }

        string summary = written.Count > 0 ? $"Rewrote {string.Join(", ", written)}." : "The reply held no whole file of this step to write.";
        if (left.Count > 0)
        {
            summary += $" Not written: {string.Join(", ", left)}.";
        }

        _store.AddEvent(plan.Id, step.Id, attempt, FileRewriteEvent, tier, machine, summary, modelNode: modelMachine,
            workspaceNode: machine, rung: rung, round: round);
        return reply with { Text = summary, ToolCalls = written.Count, EditToolCalled = written.Count > 0 };
    }
}
