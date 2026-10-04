namespace AgentFleet;

/// <summary>
/// The second opinion: before a step whose check passed is accepted, a model that did not write its code reads the changed
/// files against the step's text (see StepReview). Where the reviewer finds the requirements unmet, the round counts as
/// failed and the findings go back to the step's model like a failing check; the repair ladder bounds it. A reviewer that
/// cannot be reached, gives no verdict, or does not exist never blocks a step.
/// </summary>
internal sealed partial class PlanRunner
{
    private static readonly TimeSpan ReviewTimeLimit = TimeSpan.FromMinutes(8);

    /// <returns>The reviewer's findings when it rejected the step; null when the step may be accepted.</returns>
    private async Task<string?> ReviewAcceptedStepAsync(
        PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model, IReadOnlyList<string> changedFiles,
        int? rung, int? round, CancellationToken cancellationToken)
    {
        PlanRecord latest = _store.Get(plan.Id) ?? plan;
        if (!PlanSecondOpinion.IsOn(latest, step) || step.Files.Count == 0)
        {
            return null;
        }

        void Note(string detail, string failureClass, string? node = null) =>
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.StepReview, tier, node: node, detail: detail,
                failureClass: failureClass, rung: rung, round: round);

        List<(string Path, string Content)> files = ReadFilesToReview(latest, step, changedFiles);
        if (files.Count == 0)
        {
            Note("Nothing the step changed could be read, so there was nothing to review.", "Skipped");
            return null;
        }

        (string reviewerTier, string? reviewer, string? unavailable) = ChooseReviewer(latest, step, model);
        if (reviewer is null)
        {
            Note($"No independent reviewer is available ({unavailable}); the step is accepted on its check.", "Skipped");
            return null;
        }

        string prompt = StepReview.BuildPrompt(latest, step, files);
        IStepSession session = _agent.OpenSession(PlanRunnerToolPolicy.StepReviewRole);
        StepAgentReply reply;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ReviewTimeLimit);
            reply = await session.SendAsync(prompt, reviewerTier, reviewer, limit.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            string detail = exception is OperationCanceledException
                ? $"The reviewer ({reviewer}) did not answer within {Duration(ReviewTimeLimit)}; the step is accepted on its check."
                : $"The reviewer ({reviewer}) could not be reached: {exception.Message} The step is accepted on its check.";
            _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the step review failed.", plan.Id, step.Id);
            Note(detail, "Unavailable", reviewer);
            return null;
        }

        ReviewVerdict verdict = StepReview.Parse(reply.Text);
        switch (verdict.Kind)
        {
            case ReviewKind.Pass:
                Note($"Reviewed by {reviewer}, which did not write the code: passed. {Summarize(verdict.Text)}".TrimEnd(), "Passed", reviewer);
                return null;
            case ReviewKind.Fail:
                Note($"Reviewed by {reviewer}, which did not write the code: the step's requirements are not met. {Summarize(verdict.Text)}", "Failed", reviewer);
                return verdict.Text.Length > 0 ? verdict.Text : "The reviewer did not say what is missing; read the step's text again and check each requirement against the code and the tests.";
            default:
                Note($"The reviewer ({reviewer}) gave no PASS or FAIL: {Summarize(reply.Text)} The step is accepted on its check.", "NoVerdict", reviewer);
                return null;
        }
    }

    // The files the step changed in any of its rounds (the ones it names first), as the hub holds them now. Where nothing is
    // known to have changed (git could not tell), the files the step names stand for them.
    private List<(string Path, string Content)> ReadFilesToReview(PlanRecord plan, PlanStep step, IReadOnlyList<string> changedFiles)
    {
        var paths = new List<string>();
        void Add(string relative)
        {
            string normalized = relative.Replace('\\', '/').TrimStart('/');
            if (normalized.Length > 0 && !paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(normalized);
            }
        }

        foreach (string file in MeaningfulFiles(plan, step, changedFiles)) Add(file);
        foreach (PlanRunEvent runEvent in (plan.Events ?? []).Where(e => e.StepId == step.Id && e.Kind == RunEventKind.RoundClassified))
        {
            foreach (string file in MeaningfulFiles(plan, step, runEvent.ChangedFiles ?? [])) Add(file);
        }

        if (paths.Count == 0)
        {
            foreach (string named in step.Files.Where(file => !file.Contains('*', StringComparison.Ordinal) && !file.EndsWith('/'))) Add(named);
        }

        string[] ordered = paths
            .OrderByDescending(path => step.Files.Any(named => string.Equals(named.Replace('\\', '/').TrimStart('/'), path, StringComparison.OrdinalIgnoreCase)))
            .Take(StepReview.MaxFiles)
            .ToArray();

        var files = new List<(string Path, string Content)>();
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return files;
        }

        string root = Path.GetFullPath(plan.WorkingDirectory);
        int total = 0;
        foreach (string relative in ordered)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || HasLinkedPathComponent(root, full))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(full);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (content.Contains('\0', StringComparison.Ordinal))
            {
                continue;
            }

            if (content.Length > StepReview.MaxCharactersPerFile)
            {
                content = content[..StepReview.MaxCharactersPerFile] + "\n[the rest of the file is cut; read it with read_file]";
            }

            if (total + content.Length > StepReview.MaxCharactersInTotal && files.Count > 0)
            {
                break;
            }

            total += content.Length;
            files.Add((relative, content));
        }

        return files;
    }

    // A model that did not write the code: the hub when the plan lets it be used and it did not do the work itself, else a
    // machine other than the author's. Without one the step is accepted on its check, and the log says why.
    private (string Tier, string? Machine, string? Unavailable) ChooseReviewer(PlanRecord plan, PlanStep step, ModelAttemptResult model)
    {
        FleetNodeDefinition[] nodes = _fleetOptions?.Nodes.Where(node => !node.Vision).ToArray() ?? [];
        string? hub = nodes.SingleOrDefault(node => node.Fallback)?.Name;
        string? author = model.ModelNode ?? model.Machine;
        bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        if (hub is not null && PlanRecoveryScope.AllowsHubRescue(plan, step) && !Same(author, hub))
        {
            return (FleetTiers.Heavy, hub, null);
        }

        FleetNodeDefinition? other = nodes
            .Where(node => !Same(node.Name, author) && !Same(node.Name, hub))
            .OrderBy(node => string.Equals(node.Tier, FleetTiers.Standard, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        return other is not null
            ? (other.Tier ?? FleetTiers.Standard, other.Name, null)
            : (FleetTiers.Standard, null, "no machine other than the one that wrote the code may be used");
    }
}
