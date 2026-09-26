using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

internal static class FleetPlanContext
{
    public static string PlanTaskId(string planId) => $"plan-{planId}";

    public static string StepTaskId(string planId, int stepId) => $"plan-{planId}-step-{stepId}";

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".next", "dist", "build", "target", "__pycache__", ".venv", "venv", ".idea", ".vs", ".vscode", "coverage"
    };

    /// <summary>
    /// Every file in the project folder (bounded, skipping build output and dependency folders), so what a step
    /// created can be told apart from what was already there.
    /// </summary>
    public static HashSet<string> SnapshotFiles(string? workingDirectory, int limit = 5000)
    {
        var files = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return files;
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(workingDirectory));
        while (pending.Count > 0 && files.Count < limit)
        {
            string directory = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (files.Count < limit)
                    {
                        files.Add(file);
                    }
                }

                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(child)) &&
                        (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read simply is not part of the snapshot.
            }
        }

        return files;
    }

    /// <summary>
    /// The step's declared files as full paths inside the project folder (whether or not they exist yet).
    /// Anything outside the folder, or with an invalid path, is left out.
    /// </summary>
    public static IEnumerable<string> DeclaredPaths(PlanRecord plan, PlanStep step)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            yield break;
        }

        string root;
        try
        {
            root = Path.GetFullPath(plan.WorkingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            yield break;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string requested in step.Files.Take(20))
        {
            string? path = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(requested) && requested.IndexOfAny(['\0', '\r', '\n']) < 0)
                {
                    path = Path.GetFullPath(Path.IsPathFullyQualified(requested) ? requested : Path.Combine(root, requested));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                path = null;
            }

            if (path is not null && path.StartsWith(prefix, comparison) && !PlanRunner.HasLinkedPathComponent(root, path))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// A declared path such as "test/*.test.ts" names every file it matches. Measured: a step that ran the tests named
    /// its files as that pattern, and the fleet looked for a file called "*.test.ts", so a check that passed all 22 tests
    /// failed three times and blocked the plan.
    /// </summary>
    public static bool IsPattern(string path) => path.IndexOfAny(['*', '?']) >= 0;

    /// <summary>Whether a declared path is there: a file, a folder, or for a pattern, at least one file it matches.</summary>
    public static bool DeclaredExists(string declared) =>
        IsPattern(declared) ? PatternFiles(declared).Any() : File.Exists(declared) || Directory.Exists(declared);

    /// <summary>
    /// Whether a declared path names this file: the file itself, a folder it is in, or a pattern it matches ("*" and "?"
    /// stay inside one folder, "**" crosses folders).
    /// </summary>
    public static bool Names(string declared, string path)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string trimmed = Path.TrimEndingDirectorySeparator(declared);
        if (IsPattern(trimmed))
        {
            return PatternRegex(trimmed).IsMatch(path.Replace('\\', '/'));
        }

        return string.Equals(trimmed, Path.TrimEndingDirectorySeparator(path), comparison) ||
               path.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The files a pattern matches, looked for under the folder before its first wildcard.</summary>
    public static IEnumerable<string> PatternFiles(string pattern)
    {
        int wildcard = pattern.IndexOfAny(['*', '?']);
        int cut = pattern.LastIndexOfAny(['\\', '/'], Math.Max(0, wildcard));
        if (cut <= 0)
        {
            return [];
        }

        Regex regex = PatternRegex(pattern);
        return SnapshotFiles(pattern[..cut]).Where(file => regex.IsMatch(file.Replace('\\', '/'))).Order(StringComparer.OrdinalIgnoreCase);
    }

    private static Regex PatternRegex(string pattern)
    {
        string normalized = pattern.Replace('\\', '/');
        var regex = new System.Text.StringBuilder("^");
        for (int index = 0; index < normalized.Length; index++)
        {
            char character = normalized[index];
            if (character == '*' && index + 1 < normalized.Length && normalized[index + 1] == '*')
            {
                // "**/" is any number of folders, none included.
                bool slash = index + 2 < normalized.Length && normalized[index + 2] == '/';
                regex.Append(slash ? "(?:.*/)?" : ".*");
                index += slash ? 2 : 1;
            }
            else
            {
                regex.Append(character switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(character.ToString())
                });
            }
        }

        return new Regex(regex.Append('$').ToString(), (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// What the plan runner writes into the durable record. Each plan runs inside one context (the chat it
/// was proposed in, or a fresh one), each step is a task in it, and at every step boundary the runner
/// leaves a structured handoff for whichever agent does the next piece: the goal, what is done and how it
/// was verified, the files produced (with hashes, so a later change is noticed), the decisions that are
/// still in force, and exactly what to do next. Nothing here can fail a plan: the record is written
/// best-effort and problems are logged.
/// </summary>
internal sealed class PlanContextRecorder
{
    private readonly FleetContextStore _store;
    private readonly FleetPlanStore _plans;
    private readonly ILogger _logger;

    public PlanContextRecorder(FleetContextStore store, FleetPlanStore plans, ILogger logger)
    {
        _store = store;
        _plans = plans;
        _logger = logger;
    }

    /// <summary>The plan's context id, created and remembered on the plan if it has none yet.</summary>
    public string? ResolveContext(PlanRecord plan)
    {
        try
        {
            string contextId = FleetRequestContext.IsValidContextId(plan.ContextId) ? plan.ContextId! : Guid.NewGuid().ToString("D");
            if (contextId != plan.ContextId)
            {
                _plans.Update(plan.Id, current => current with { ContextId = contextId });
            }

            // A chat that already has a title keeps it; only a context made for the plan is named after it.
            _store.EnsureContext(contextId, _store.GetContext(contextId) is null ? $"Plan: {plan.Title}" : null);
            return contextId;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not open a durable context for plan {PlanId}.", plan.Id);
            return null;
        }
    }

    public void RunStarted(string contextId, PlanRecord plan, bool continuing) =>
        Try(plan, "run start", () =>
        {
            string planTask = FleetPlanContext.PlanTaskId(plan.Id);
            _store.UpsertTask(contextId, planTask, "plan", PlanStatus.Running, plan.Title);
            foreach (PlanStep step in plan.Steps)
            {
                _store.UpsertTask(contextId, FleetPlanContext.StepTaskId(plan.Id, step.Id), "step",
                    step.Status == StepStatus.Done ? StepStatus.Done : StepStatus.Pending, $"{step.Id}. {step.Title}", planTask);
            }

            Transition(contextId, plan, null, PlanStatus.Running, continuing
                ? "Run resumed (after a stop or a restart)."
                : $"Run started: {plan.Steps.Count} step(s) in {plan.WorkingDirectory ?? "the default folder"}.");
            _store.SaveCheckpoint(contextId, planTask, continuing ? "plan-resumed" : "plan-started", State(plan));
        });

    /// <summary>Marks the step as running and notices any recorded file that changed or vanished since.</summary>
    public IReadOnlyList<ArtifactVerification> AttemptStarted(string contextId, PlanRecord plan, PlanStep step, int attempt, string tier)
    {
        try
        {
            _store.UpsertTask(contextId, FleetPlanContext.StepTaskId(plan.Id, step.Id), "step", StepStatus.Running,
                $"{step.Id}. {step.Title}", FleetPlanContext.PlanTaskId(plan.Id));
            Transition(contextId, plan, step, StepStatus.Running, $"Attempt {attempt} on the {tier} tier.");
            return _store.RecordArtifactDivergences(contextId, FleetPlanContext.StepTaskId(plan.Id, step.Id));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record the start of plan {PlanId} step {StepId}.", plan.Id, step.Id);
            return [];
        }
    }

    public long? CheckResult(string contextId, PlanRecord plan, PlanStep step, int attempt, bool passed, string? output, string? node)
    {
        try
        {
            return _store.AppendEvent(
                contextId,
                FleetContextEventKind.Verification,
                new
                {
                    command = step.Verify,
                    passed,
                    attempt,
                    output = ContextText.Clip(output, 1200)
                },
                FleetPlanContext.StepTaskId(plan.Id, step.Id), actor: "runner", agent: "plan-runner", node: node);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record the check of plan {PlanId} step {StepId}.", plan.Id, step.Id);
            return null;
        }
    }

    /// <summary>The step passed: record its files, leave the handoff for the next step, checkpoint.</summary>
    public void StepDone(string contextId, PlanRecord plan, PlanStep step, long? verificationEventId, string? node, bool skipped = false)
    {
        try
        {
            PlanRecord fresh = _plans.Get(plan.Id) ?? plan;
            string stepTask = FleetPlanContext.StepTaskId(plan.Id, step.Id);
            var artifactIds = new List<string>();
            var sourceEvents = new List<long>();
            if (verificationEventId is { } verification)
            {
                sourceEvents.Add(verification);
            }

            foreach (string path in ProjectFiles(fresh, step))
            {
                FleetArtifact artifact = _store.RecordArtifact(contextId, stepTask, path, MediaTypeFor(path));
                artifactIds.Add(artifact.Id);
                if (artifact.ProducingEventId is { } produced)
                {
                    sourceEvents.Add(produced);
                }
            }

            _store.UpsertTask(contextId, stepTask, "step", StepStatus.Done, $"{step.Id}. {step.Title}", FleetPlanContext.PlanTaskId(plan.Id));
            Transition(contextId, fresh, step, StepStatus.Done, step.Note ?? "Step passed its check.");

            PlanStep? next = fresh.Steps.FirstOrDefault(candidate => candidate.Status != StepStatus.Done && candidate.Id != step.Id);
            IReadOnlyList<FleetContextEvent> pinned = _store.EventsOfKinds(contextId, [FleetContextEventKind.Decision], 30, pinnedOnly: true);
            sourceEvents.AddRange(pinned.Select(e => e.Id));

            var envelope = new HandoffEnvelope(
                Version: 1,
                ContextId: contextId,
                TaskId: next is null ? FleetPlanContext.PlanTaskId(plan.Id) : FleetPlanContext.StepTaskId(plan.Id, next.Id),
                FromAgent: skipped ? $"step {step.Id}, skipped by the user" : $"step {step.Id}{(node is null ? string.Empty : $" on {node}")}",
                ToAgent: next?.Title,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                Goal: fresh.Goal,
                CurrentTask: next is null ? "The plan is complete." : $"{next.Id}. {next.Title}",
                Constraints: fresh.Assumptions.Concat(pinned.Where(e => ContextText.String(e.Payload, "category") == "constraint")
                    .Select(e => ContextText.Clip(ContextText.String(e.Payload, "text"), 300))).ToList(),
                Decisions: pinned.Where(e => ContextText.String(e.Payload, "category") != "constraint")
                    .Select(e => ContextText.Clip(ContextText.String(e.Payload, "text"), 300)).ToList(),
                CompletedWork: fresh.Steps.Where(s => s.Status == StepStatus.Done)
                    .Select(s => $"{s.Id}. {s.Title}{(string.IsNullOrWhiteSpace(s.Note) ? string.Empty : $": {ContextText.Clip(s.Note, 200)}")}").ToList(),
                ArtifactIds: artifactIds,
                VerificationEvidence: [skipped
                    ? $"Step {step.Id} was skipped by the user and its check was NOT run: do not assume its work is there, check the files it names."
                    : step.Verify is null
                        ? $"Step {step.Id} had no automatic check."
                        : $"Step {step.Id}: `{step.Verify}` passed."],
                OpenQuestions: fresh.OpenQuestions,
                Risks: fresh.Risks,
                NextAction: next is null
                    ? "Nothing left to do; summarise the result for the user."
                    : $"Do step {next.Id}, {next.Title}. {ContextText.Clip(next.Detail, 400)}",
                CompletionCondition: next is null
                    ? "All steps have passed their checks."
                    : next.Verify is null ? "No automatic check; finish when the step's work is done." : $"`{next.Verify}` succeeds.",
                SourceEventIds: sourceEvents.Distinct().ToList());
            _store.SaveHandoff(envelope);
            _store.SaveCheckpoint(contextId, stepTask, "step-done", State(fresh));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record the completion of plan {PlanId} step {StepId}.", plan.Id, step.Id);
        }
    }

    public void StepBlocked(string contextId, PlanRecord plan, PlanStep step, string reason) =>
        Try(plan, "block", () =>
        {
            _store.UpsertTask(contextId, FleetPlanContext.StepTaskId(plan.Id, step.Id), "step", StepStatus.Failed,
                $"{step.Id}. {step.Title}", FleetPlanContext.PlanTaskId(plan.Id));
            _store.UpsertTask(contextId, FleetPlanContext.PlanTaskId(plan.Id), "plan", PlanStatus.Blocked, plan.Title);
            Transition(contextId, plan, step, PlanStatus.Blocked, reason);
            _store.AppendEvent(contextId, FleetContextEventKind.Error, new { message = reason },
                FleetPlanContext.StepTaskId(plan.Id, step.Id), actor: "runner", agent: "plan-runner");
            _store.SaveCheckpoint(contextId, FleetPlanContext.PlanTaskId(plan.Id), "plan-blocked", State(_plans.Get(plan.Id) ?? plan));
        });

    public void PlanFinished(string contextId, PlanRecord plan, string status, string note) =>
        Try(plan, "finish", () =>
        {
            _store.UpsertTask(contextId, FleetPlanContext.PlanTaskId(plan.Id), "plan", status, plan.Title);
            Transition(contextId, plan, null, status, note);
            _store.SaveCheckpoint(contextId, FleetPlanContext.PlanTaskId(plan.Id), $"plan-{status}", State(_plans.Get(plan.Id) ?? plan));
        });

    private void Transition(string contextId, PlanRecord plan, PlanStep? step, string to, string note) =>
        _store.AppendEvent(
            contextId,
            FleetContextEventKind.PlanTransition,
            new { planId = plan.Id, stepId = step?.Id, to, note = ContextText.Clip(note, 400) },
            step is null ? FleetPlanContext.PlanTaskId(plan.Id) : FleetPlanContext.StepTaskId(plan.Id, step.Id),
            actor: "runner", agent: "plan-runner");

    private static object State(PlanRecord plan) => new
    {
        planId = plan.Id,
        status = plan.Status,
        steps = plan.Steps.Select(s => new { s.Id, s.Status, s.Attempts }).ToArray()
    };

    // The step's declared files that exist inside the project folder, not through a link; a pattern stands for the files it matches.
    private static IEnumerable<string> ProjectFiles(PlanRecord plan, PlanStep step) =>
        FleetPlanContext.DeclaredPaths(plan, step)
            .SelectMany(path => FleetPlanContext.IsPattern(path) ? FleetPlanContext.PatternFiles(path).Take(20) : File.Exists(path) ? [path] : [])
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static string MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".md" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".js" or ".mjs" or ".cjs" => "text/javascript",
        ".ts" or ".tsx" => "text/typescript",
        ".cs" => "text/x-csharp",
        _ => "text/plain"
    };

    private void Try(PlanRecord plan, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record {What} for plan {PlanId} in its context.", what, plan.Id);
        }
    }
}
