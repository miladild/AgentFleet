using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace AgentFleet;

/// <summary>
/// Plans as files: one JSON document per plan in a "plans" folder next to the running
/// exe (or FLEET_PLANS_DIR), same convention as sessions/ and logs/ so a redeploy never
/// overwrites them. Ids are 32 hex characters and validated on every access, which closes
/// off path traversal the same way the session store does.
/// </summary>
internal sealed partial class FleetPlanStore
{
    public const int MaxSteps = 20;
    public static readonly TimeSpan DefaultRunDuration = TimeSpan.FromHours(8);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] Tiers = [FleetTiers.Heavy, FleetTiers.Standard, FleetTiers.Light];

    private readonly string _directory;
    private string WorkerBaselinesDirectory => Path.Combine(_directory, "worker-baselines");
    private readonly object _lock = new();

    public FleetPlanStore(IConfiguration configuration)
    {
        _directory = configuration["FLEET_PLANS_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "plans");
        Directory.CreateDirectory(_directory);
    }

    /// <summary>True when a plan that still matters runs inside this context, so the context must not be deleted.</summary>
    public bool UsesContext(string contextId)
    {
        lock (_lock)
        {
            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(path => TryRead(path))
                .OfType<PlanRecord>()
                .Any(plan => plan.ContextId == contextId && plan.Status != PlanStatus.Rejected);
        }
    }

    /// <summary>The contexts of plans that are not finished (waiting, approved, running or blocked): history cleanup keeps these.</summary>
    public IReadOnlySet<string> UnfinishedContextIds()
    {
        lock (_lock)
        {
            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(path => TryRead(path))
                .OfType<PlanRecord>()
                .Where(plan => plan.ContextId is not null &&
                    plan.Status is PlanStatus.AwaitingApproval or PlanStatus.Approved or PlanStatus.Running or PlanStatus.Blocked)
                .Select(plan => plan.ContextId!)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    public static bool IsValidId(string? id) => id is not null && PlanIdPattern().IsMatch(id);

    // The marker every plan-related tool result carries, so a later request can find which
    // plan a conversation belongs to just by reading its own history.
    public static string Marker(string id) => $"[plan:{id}]";

    public static string? FindMarkerId(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        Match match = MarkerPattern().Match(text);
        return match.Success ? match.Groups["id"].Value : null;
    }

    public PlanRecord Create(
        string title,
        string goal,
        string? workingDirectory,
        IEnumerable<string>? assumptions,
        IEnumerable<string>? openQuestions,
        IEnumerable<string>? risks,
        string? diagram,
        string? diagramNote,
        IReadOnlyList<PlanStepInput> steps,
        string? recoveryScope = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A plan needs a title.");
        }

        if (steps.Count == 0)
        {
            throw new ArgumentException("A plan needs at least one step.");
        }

        if (steps.Count > MaxSteps)
        {
            throw new ArgumentException($"A plan can have at most {MaxSteps} steps; split the work into smaller plans.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var plan = new PlanRecord(
            Guid.NewGuid().ToString("N"),
            title.Trim(),
            goal?.Trim() ?? string.Empty,
            PlanStatus.AwaitingApproval,
            string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim(),
            Clean(assumptions),
            Clean(openQuestions),
            Clean(risks),
            string.IsNullOrWhiteSpace(diagram) ? null : diagram.Trim(),
            diagramNote,
            steps.Select((step, index) => ToStep(step, index + 1)).ToList(),
            now,
            now,
            null,
            RecoveryScope: NormalizeRecoveryScope(recoveryScope));

        plan = PlanGraph.Normalize(plan);
        Save(plan);
        return plan;
    }

    public PlanRecord? Get(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        lock (_lock)
        {
            string path = PathFor(id);

            // A plan saved before steps could depend on each other has none written down: it reads as the chain it was.
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PlanRecord>(File.ReadAllText(path), SerializerOptions) is { } stored ? PlanGraph.Normalize(stored) : null
                : null;
        }
    }

    /// <summary>Cross-process plan lease; the OS releases it automatically if the backend exits.</summary>
    public FileStream? TryAcquireRunLease(string id)
    {
        if (!IsValidId(id)) return null;
        try
        {
            return new FileStream(PathFor(id) + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void SaveWorkerBaseline(string planId, int stepId, int attempt, string machine, IReadOnlyDictionary<string, string> hashes)
    {
        string path = WorkerBaselinePath(planId, stepId, attempt);
        string temporary = path + ".tmp";
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new WorkerWorkspaceBaseline(machine,
                new Dictionary<string, string>(hashes, StringComparer.OrdinalIgnoreCase)), SerializerOptions), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
    }

    internal WorkerWorkspaceBaseline? GetWorkerBaseline(string planId, int stepId, int attempt)
    {
        string path = WorkerBaselinePath(planId, stepId, attempt);
        lock (_lock)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<WorkerWorkspaceBaseline>(File.ReadAllText(path), SerializerOptions);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    internal void DeleteWorkerBaseline(string planId, int stepId, int attempt)
    {
        string path = WorkerBaselinePath(planId, stepId, attempt);
        lock (_lock)
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    public IReadOnlyList<PlanSummary> List()
    {
        lock (_lock)
        {
            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(path => TryRead(path))
                .OfType<PlanRecord>()
                .OrderByDescending(plan => plan.UpdatedUtc)
                .Select(plan => new PlanSummary(
                    plan.Id,
                    plan.Title,
                    plan.Status,
                    plan.Steps.Count(step => step.Status == StepStatus.Done),
                    plan.Steps.Count,
                    plan.UpdatedUtc,
                    plan.ContextId,
                    plan.Steps.Count(step => step.Status == StepStatus.Parked)))
                .ToList();
        }
    }

    // Most recent plan still waiting on, or in the middle of, work - what "get_plan" with
    // no id should show.
    public PlanRecord? FindActive()
    {
        lock (_lock)
        {
            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(path => TryRead(path))
                .OfType<PlanRecord>()
                .Where(plan => plan.Status is PlanStatus.AwaitingApproval or PlanStatus.Approved or PlanStatus.Running or PlanStatus.Blocked)
                .OrderByDescending(plan => plan.UpdatedUtc)
                .FirstOrDefault();
        }
    }

    // Applies a change under the store's lock and saves it, so two callers cannot
    // overwrite each other's step updates.
    public PlanRecord? Update(string id, Func<PlanRecord, PlanRecord> change)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        lock (_lock)
        {
            PlanRecord? current = Get(id);
            if (current is null)
            {
                return null;
            }

            PlanRecord updated = change(current) with { UpdatedUtc = DateTimeOffset.UtcNow };
            Save(updated);
            return updated;
        }
    }

    /// <summary>Changes the next route only while the step is waiting or has failed.</summary>
    public PlanRecord? SelectMachine(string id, int stepId, string? machine, out string? error)
    {
        error = null;
        if (!IsValidId(id))
        {
            return null;
        }

        lock (_lock)
        {
            PlanRecord? current = Get(id);
            if (current is null)
            {
                return null;
            }

            PlanStep? step = current.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
            if (step is null)
            {
                error = "That step no longer exists.";
                return current;
            }

            bool editable =
                (current.Status == PlanStatus.AwaitingApproval && step.Status == StepStatus.Pending) ||
                (current.Status == PlanStatus.Blocked && StepStatus.IsStopped(step.Status) && !HasRunningParallelPeer(current, step)) ||
                (current.Status == PlanStatus.Running && step.Status == StepStatus.Parked) ||
                ((current.Status is PlanStatus.Approved or PlanStatus.Running or PlanStatus.Blocked) &&
                    step.Status == StepStatus.Pending && IsReady(current, step) && !HasRunningParallelPeer(current, step));
            if (!editable)
            {
                error = step.Status == StepStatus.Running
                    ? "This step is running and cannot be moved."
                    : "This step is waiting for an earlier step, or the plan is no longer editable.";
                return current;
            }

            if (string.Equals(step.Machine, machine, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            IReadOnlyList<PlanStep> steps = current.Steps
                .Select(candidate => candidate.Id == stepId ? candidate with { Machine = machine } : candidate)
                .ToList();
            var routeEvent = new PlanRunEvent(
                now,
                stepId,
                null,
                RunEventKind.MachineSelected,
                step.Tier,
                null,
                machine is null
                    ? $"Automatic routing restored; requested tier remains {step.Tier}."
                    : $"Machine selected by the user; requested tier remains {step.Tier}.",
                ModelNode: null,
                WorkspaceNode: machine);
            List<PlanRunEvent> events = [.. current.Events ?? [], routeEvent];
            if (events.Count > MaxEvents)
            {
                events = [events[0], .. events.Skip(events.Count - (MaxEvents - 1))];
            }

            PlanRecord updated = current with { Steps = steps, Events = events, UpdatedUtc = now };
            Save(updated);
            return updated;
        }
    }

    private static bool IsReady(PlanRecord plan, PlanStep step) => PlanGraph.IsReady(plan, step);

    private static bool HasRunningParallelPeer(PlanRecord plan, PlanStep step) =>
        step.ParallelGroup is not null && plan.Steps.Any(candidate =>
            candidate.Id != step.Id && candidate.ParallelGroup == step.ParallelGroup && candidate.Status == StepStatus.Running);

    public const int MaxEvents = 400;
    public const int MaxEventDetailCharacters = 1200;
    public const int MaxFilesChangedCharacters = 4000;
    public const int MaxChangedFilesPerEvent = 12;

    // Appends to the run log. Bounded, so a plan that retries for days cannot grow its file without
    // limit: the oldest lines go first, but the first line (when the run started) is kept.
    public PlanRecord? AddEvent(
        string id,
        int? stepId,
        int? attempt,
        string kind,
        string? tier = null,
        string? node = null,
        string detail = "",
        string? modelNode = null,
        string? workspaceNode = null,
        string? failureClass = null,
        string? failureSignature = null,
        int? failureSignatureSize = null,
        int? filesChanged = null,
        int? toolCalls = null,
        bool? editToolCalled = null,
        int? rung = null,
        int? round = null,
        IReadOnlyList<string>? changedFiles = null,
        int? durationSeconds = null,
        string? checkBefore = null,
        string? checkAfter = null)
    {
        string text = detail ?? string.Empty;
        int limit = kind == RunEventKind.FilesChanged ? MaxFilesChangedCharacters : MaxEventDetailCharacters;
        if (text.Length > limit)
        {
            text = text[..limit].TrimEnd() + "...";
        }

        var entry = new PlanRunEvent(DateTimeOffset.UtcNow, stepId, attempt, kind, tier, node, text, modelNode, workspaceNode,
            failureClass, failureSignature, failureSignatureSize, filesChanged, toolCalls, editToolCalled, rung, round,
            changedFiles is { Count: > 0 } ? changedFiles.Take(MaxChangedFilesPerEvent).ToList() : null, durationSeconds,
            Shorten(checkBefore), Shorten(checkAfter));
        return Update(id, plan =>
        {
            List<PlanRunEvent> events = [.. plan.Events ?? [], entry];
            if (events.Count > MaxEvents)
            {
                events = [events[0], .. events.Skip(events.Count - (MaxEvents - 1))];
            }

            return plan with { Events = events };
        });
    }

    /// <summary>Raised with the plan id when a plan moves to approved, which is what starts it running.</summary>
    public event Action<string>? Approved;

    // Only a plan that is waiting (or blocked and needs another go-ahead) can be approved.
    /// <param name="retryStopped">
    /// Whether approving a blocked plan also gives every parked step a fresh start (the default: "approve and resume" means
    /// try again). False for a call that only goes on past one step the user skipped.
    /// </param>
    public PlanRecord? Approve(string id, bool exportToProject = false, string? recoveryScope = null, string? healChecks = null, bool retryStopped = true)
    {
        bool changed = false;
        bool retryingBlockedPlan = false;
        PlanRecord? approved = Update(id, plan =>
        {
            if (plan.Status is not (PlanStatus.AwaitingApproval or PlanStatus.Blocked))
            {
                return plan;
            }

            changed = true;
            DateTimeOffset approvedAt = DateTimeOffset.UtcNow;
            retryingBlockedPlan = plan.Status == PlanStatus.Blocked;
            IReadOnlyList<PlanStep> steps = retryingBlockedPlan
                ? plan.Steps.Select(step => step.Status switch
                {
                    StepStatus.Parked when retryStopped => step with { Status = StepStatus.Pending, Attempts = 0 },
                    StepStatus.Running or StepStatus.Failed => step with { Attempts = 0 },
                    _ => step
                }).ToList()
                : plan.Steps;
            PlanRecord approved = plan with
            {
                Status = PlanStatus.Approved,
                ApprovedUtc = approvedAt,
                RunDeadlineUtc = approvedAt + DefaultRunDuration,
                RecoveryScope = NormalizeRecoveryScope(recoveryScope) ?? NormalizeRecoveryScope(plan.RecoveryScope) ?? PlanRecoveryScope.WorkerOnly,
                HealChecks = NormalizeHealChecks(healChecks) ?? NormalizeHealChecks(plan.HealChecks) ?? PlanHealChecks.Auto,
                Steps = steps
            };
            if (exportToProject)
            {
                string path = ExportToProject(approved);
                approved = approved with { ExportedPlanPath = path };
            }

            return approved;
        });

        // Outside the store's lock: a listener may call straight back into the store.
        if (changed && approved is not null)
        {
            if (retryingBlockedPlan)
            {
                // The explicit re-approval starts a fresh deadline. Durable prior waits remain in the report.
                AddEvent(id, null, null, RunEventKind.RetryApproved,
                    detail: "The user approved another run; a fresh run deadline started.");
            }
            Approved?.Invoke(approved.Id);
        }

        return approved;
    }

    /// <summary>
    /// The user's call on a stopped or blocked plan: the step counts as done without its check (its work was done by
    /// hand, or its check is what is wrong), so approving the plan again carries on with the next step. Null when there
    /// is no such plan or step; the plan unchanged when it is not stopped or blocked, or the step is already done.
    /// </summary>
    public PlanRecord? SkipStep(string id, int stepId)
    {
        bool skipped = false;
        PlanRecord? result = Update(id, plan =>
        {
            PlanStep? step = plan.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
            bool stoppedInRunningPlan = plan.Status == PlanStatus.Running && step is { Status: StepStatus.Parked };
            if ((plan.Status != PlanStatus.Blocked && !stoppedInRunningPlan) || step is null || step.Status == StepStatus.Done)
            {
                return plan;
            }

            skipped = true;
            PlanRecord changed = FleetPlanStoreSteps.With(plan, stepId, s => s with
            {
                Status = StepStatus.Done,
                Note = "Skipped by the user: its check was not run.",
                CompletedUtc = DateTimeOffset.UtcNow
            });
            return changed.Steps.All(s => s.Status == StepStatus.Done) ? changed with { Status = PlanStatus.Done } : changed;
        });

        if (skipped)
        {
            result = AddEvent(id, stepId, null, RunEventKind.StepSkipped, detail: "Skipped by the user: its check was not run.") ?? result;
        }

        return result is null || result.Steps.All(step => step.Id != stepId) ? null : result;
    }

    /// <summary>
    /// The user's call on one stopped step: it gets a fresh start (its attempts and its repair ladder begin again) while the
    /// rest of the plan is left alone. In a plan that is still running the runner picks it up by itself; for a blocked plan
    /// the caller approves the plan again. Null when there is no such plan or step; the plan unchanged when the step is not stopped.
    /// </summary>
    public PlanRecord? RetryStep(string id, int stepId)
    {
        bool retried = false;
        PlanRecord? result = Update(id, plan =>
        {
            PlanStep? step = plan.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
            // In a plan that is still running only a parked step is stopped for good: a failed one may be between two rounds.
            bool retryable = plan.Status == PlanStatus.Blocked
                ? step is not null && StepStatus.IsStopped(step.Status)
                : plan.Status == PlanStatus.Running && step is { Status: StepStatus.Parked };
            if (step is null || !retryable)
            {
                return plan;
            }

            retried = true;
            return FleetPlanStoreSteps.With(plan, stepId, s => s with { Status = StepStatus.Pending, Attempts = 0 });
        });

        if (retried)
        {
            result = AddEvent(id, stepId, null, RunEventKind.StepRetried, detail: "The user asked for this step to be tried again with a fresh repair ladder.") ?? result;
        }

        return result is null || result.Steps.All(step => step.Id != stepId) ? null : result;
    }

    public PlanRecord? Reject(string id) =>
        Update(id, plan => plan.Status is PlanStatus.Done
            ? plan
            : plan with { Status = PlanStatus.Rejected });

    public static string ToMarkdown(PlanRecord plan)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {plan.Title}");
        text.AppendLine();
        text.AppendLine($"Status: {plan.Status.Replace('-', ' ')}. {Marker(plan.Id)}");
        if (plan.WorkingDirectory is not null)
        {
            text.AppendLine($"Working directory: `{plan.WorkingDirectory}`");
        }

        text.AppendLine($"Recovery scope: {(PlanRecoveryScope.AllowsHubRescue(plan.RecoveryScope) ? "hub model rescue allowed after worker failure" : "worker only")}");
        if (plan.RunDeadlineUtc is { } deadline)
        {
            text.AppendLine($"Run deadline: {Local(deadline)}.");
        }

        if (plan.ExportedPlanPath is not null)
        {
            text.AppendLine($"Exported copy: `{plan.ExportedPlanPath}`");
        }

        text.AppendLine();
        text.AppendLine("## Goal");
        text.AppendLine(string.IsNullOrWhiteSpace(plan.Goal) ? "(none given)" : plan.Goal);

        AppendList(text, "Assumptions", plan.Assumptions);
        AppendList(text, "Open questions", plan.OpenQuestions);
        AppendList(text, "Risks", plan.Risks);

        if (plan.Diagram is not null)
        {
            text.AppendLine();
            text.AppendLine("## Diagram");
            text.AppendLine("```mermaid");
            text.AppendLine(plan.Diagram);
            text.AppendLine("```");
        }

        if (plan.DiagramNote is not null)
        {
            text.AppendLine();
            text.AppendLine($"_{plan.DiagramNote}_");
        }

        text.AppendLine();
        text.AppendLine("## Steps");
        foreach (PlanStep step in plan.Steps)
        {
            string box = step.Status switch
            {
                StepStatus.Done => "[x]",
                StepStatus.Failed or StepStatus.Parked => "[!]",
                StepStatus.Running => "[~]",
                _ => "[ ]"
            };
            string machine = step.Machine is null ? string.Empty : $", machine: {step.Machine}";
            text.AppendLine($"- {box} **{step.Id}. {step.Title}** ({step.Tier}{machine})");
            if (!string.IsNullOrWhiteSpace(step.Detail))
            {
                text.AppendLine($"  - {step.Detail.ReplaceLineEndings(" ")}");
            }

            if (step.Files.Count > 0)
            {
                text.AppendLine($"  - Files: {string.Join(", ", step.Files.Select(file => $"`{file}`"))}");
            }

            if (step.ParallelGroup is not null)
            {
                text.AppendLine($"  - Parallel group: `{step.ParallelGroup}` (runs alongside consecutive steps with the same label when safe)");
            }

            // Only what differs from the chain is worth a line.
            List<PlanStep> unspecified = plan.Steps.Select(candidate => candidate with { DependsOn = null }).ToList();
            if (step.DependsOn is { Count: > 0 } declared && !declared.SequenceEqual(PlanGraph.DependenciesOf(unspecified, step with { DependsOn = null })))
            {
                text.AppendLine($"  - Depends on: {string.Join(", ", declared.Select(id => $"step {id}"))}");
            }

            if (step.Verify is not null)
            {
                text.AppendLine($"  - {(step.Id == plan.Steps[^1].Id ? "Runner final validation" : "Verify")}: `{step.Verify}`");
            }

            if (step.OriginalVerify is not null)
            {
                text.AppendLine($"  - Check changed by the fleet: it was `{step.OriginalVerify}`");
            }

            text.AppendLine($"  - Backend restart: {(step.RetrySafe ? "this local step may resume automatically" : "pause for review before retrying")}");

            if (!string.IsNullOrWhiteSpace(step.Note))
            {
                text.AppendLine($"  - Note: {step.Note.ReplaceLineEndings(" ")}");
            }
        }

        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// The morning-after view of a run: how it ended, what each step took, and the timeline. Times are
    /// shown in this machine's local time, which is the time the owner read the clock in.
    /// </summary>
    public static string ToReportMarkdown(PlanRecord plan)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Run report: {plan.Title}");
        text.AppendLine();
        text.AppendLine($"Status: **{plan.Status.Replace('-', ' ')}**. {Marker(plan.Id)}");
        text.AppendLine($"Recovery scope: {(PlanRecoveryScope.AllowsHubRescue(plan.RecoveryScope) ? "hub model rescue allowed after worker failure" : "worker only")}");
        text.AppendLine($"Broken checks: {(PlanHealChecks.IsAuto(plan.HealChecks) ? "fixed automatically" : "the fleet asks first")}");
        if (plan.WorkingDirectory is not null)
        {
            text.AppendLine($"Working directory: `{plan.WorkingDirectory}`");
        }

        IReadOnlyList<PlanRunEvent> events = plan.Events ?? [];
        DateTimeOffset? started = plan.ApprovedUtc ?? events.FirstOrDefault()?.AtUtc;
        if (started is not null)
        {
            DateTimeOffset end = plan.Status is PlanStatus.Done or PlanStatus.Blocked or PlanStatus.Rejected
                ? plan.UpdatedUtc
                : DateTimeOffset.UtcNow;
            text.AppendLine($"Started {Local(started.Value)}, {(plan.Status is PlanStatus.Running or PlanStatus.Approved ? "running for" : "took")} {Duration(end - started.Value)}.");
        }
        if (plan.RunDeadlineUtc is { } deadline)
        {
            text.AppendLine($"Run deadline {Local(deadline)}.");
        }

        int done = plan.Steps.Count(step => step.Status == StepStatus.Done);
        PlanStep[] stopped = plan.Steps.Where(step => StepStatus.IsStopped(step.Status)).ToArray();
        PlanStep[] waiting = plan.Steps.Where(step => step.Status == StepStatus.Pending && PlanGraph.Unmet(plan, step).Any(dependency => StepStatus.IsStopped(dependency.Status))).ToArray();
        text.AppendLine($"{done} of {plan.Steps.Count} steps done" +
                        (stopped.Length > 0 ? $", {stopped.Length} parked or stopped" : string.Empty) +
                        (waiting.Length > 0 ? $", {waiting.Length} waiting for them" : string.Empty) +
                        $", {plan.Steps.Sum(step => step.Attempts)} attempt(s) in all.");

        PlanStep? blocked = stopped.FirstOrDefault();
        if (plan.Status == PlanStatus.Blocked && blocked is not null)
        {
            text.AppendLine();
            text.AppendLine($"## Where it stopped");
            foreach (PlanStep stoppedStep in stopped)
            {
                PlanRunEvent? parkedEvent = events.LastOrDefault(e => e.StepId == stoppedStep.Id && e.Kind == RunEventKind.StepParked);
                text.AppendLine($"Step {stoppedStep.Id}, {stoppedStep.Title}: {stoppedStep.Note}" +
                                (parkedEvent?.FailureSignature is { Length: > 0 } cause ? $" (cause: {cause})" : string.Empty));
            }

            PlanRunEvent? lastCheck = events.LastOrDefault(e => e.StepId == blocked.Id &&
                (e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed or RunEventKind.RunDeadlineExceeded));
            if (lastCheck is not null)
            {
                text.AppendLine();
                text.AppendLine($"Last failing check output (step {blocked.Id}):");
                text.AppendLine("```");
                text.AppendLine(lastCheck.Detail);
                text.AppendLine("```");
            }

            text.AppendLine();
            text.AppendLine("## What to do next");
            foreach (PlanStep stoppedStep in stopped)
            {
                text.AppendLine(stoppedStep.Status == StepStatus.Parked
                    ? $"- Step {stoppedStep.Id} ({stoppedStep.Title}) is parked: fix the cause and retry it (a fresh repair ladder), or skip it to count it as done without its check."
                    : $"- Step {stoppedStep.Id} ({stoppedStep.Title}) stopped the run and needs a look at the project first; then approve the plan again, or skip it.");
            }

            if (waiting.Length > 0)
            {
                text.AppendLine(waiting.Length == 1
                    ? $"- Step {waiting[0].Id} was not run: it starts by itself once what it waits for is done or skipped."
                    : $"- Steps {string.Join(", ", waiting.Select(step => step.Id))} were not run: they start by themselves once what they wait for is done or skipped.");
            }
        }

        PlanRunEvent? filesChanged = events.LastOrDefault(e => e.Kind == RunEventKind.FilesChanged);
        if (filesChanged is not null)
        {
            text.AppendLine();
            text.AppendLine("## Files changed (git status)");
            text.AppendLine("```");
            text.AppendLine(filesChanged.Detail);
            text.AppendLine("```");
        }

        PlanRunEvent[] healed = events.Where(e => e.Kind == RunEventKind.CheckHealed).ToArray();
        if (healed.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## Checks changed by Fleet");
            foreach (PlanRunEvent e in healed)
            {
                string title = plan.Steps.FirstOrDefault(step => step.Id == e.StepId)?.Title ?? string.Empty;
                text.AppendLine($"- Step {e.StepId} ({title}): `{e.CheckBefore}` became `{e.CheckAfter}`. {e.Detail.ReplaceLineEndings(" ")}");
            }
        }

        PlanRunEvent[] repaired = events.Where(e => e.Kind == RunEventKind.EnvironmentRepaired).ToArray();
        if (repaired.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## Environment repairs");
            foreach (PlanRunEvent e in repaired)
            {
                text.AppendLine($"- Step {e.StepId}: {e.Detail.ReplaceLineEndings(" ")}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Steps");
        foreach (PlanStep step in plan.Steps)
        {
            string box = step.Status switch { StepStatus.Done => "[x]", StepStatus.Failed => "[!]", StepStatus.Parked => "[p]", StepStatus.Running => "[~]", _ => "[ ]" };
            string took = step.StartedUtc is not null && step.CompletedUtc is not null
                ? $", {Duration(step.CompletedUtc.Value - step.StartedUtc.Value)}"
                : string.Empty;
            text.AppendLine($"- {box} {step.Id}. {step.Title} ({step.Tier}, {step.Attempts} attempt(s){took})");
            if (!string.IsNullOrWhiteSpace(step.Note))
            {
                text.AppendLine($"  - {step.Note.ReplaceLineEndings(" ")}");
            }

            string[] modelNodes = events.Where(e => e.StepId == step.Id &&
                    (e.Kind is RunEventKind.AttemptStarted or RunEventKind.AttemptEnded or RunEventKind.ModelFailed) &&
                    !string.IsNullOrWhiteSpace(e.ModelNode ?? e.Node))
                .Select(e => e.ModelNode ?? e.Node!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modelNodes.Length > 0)
            {
                text.AppendLine($"  - Model: {string.Join(", ", modelNodes)}");
            }

            string[] workspaces = events.Where(e => e.StepId == step.Id &&
                    (e.Kind is RunEventKind.WorkspaceStaged or RunEventKind.WorkspaceSynced) &&
                    !string.IsNullOrWhiteSpace(e.WorkspaceNode ?? e.Node))
                .Select(e => e.WorkspaceNode ?? e.Node!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (workspaces.Length > 0)
            {
                text.AppendLine($"  - Workspace and checks: {string.Join(", ", workspaces)}");
            }

            PlanRunEvent[] stepEvents = events.Where(e => e.StepId == step.Id).ToArray();
            int healedChecks = stepEvents.Count(e => e.Kind == RunEventKind.CheckHealed);
            int repairs = stepEvents.Count(e => e.Kind == RunEventKind.EnvironmentRepaired && e.FailureClass == "Repaired");
            if (healedChecks > 0 || repairs > 0)
            {
                text.AppendLine($"  - Healing: {(healedChecks > 0 ? $"check changed {healedChecks} time{(healedChecks == 1 ? string.Empty : "s")}" : string.Empty)}" +
                                $"{(healedChecks > 0 && repairs > 0 ? ", " : string.Empty)}" +
                                $"{(repairs > 0 ? $"environment repaired {repairs} time{(repairs == 1 ? string.Empty : "s")}" : string.Empty)}");
            }

            if (step.Status == StepStatus.Pending && PlanGraph.Unmet(plan, step).Where(dependency => StepStatus.IsStopped(dependency.Status)).ToArray() is { Length: > 0 } holdingBack)
            {
                text.AppendLine($"  - Waiting on {string.Join(", ", holdingBack.Select(dependency => $"#{dependency.Id}"))} ({"parked or stopped"}); not run.");
            }

            int failedRounds = stepEvents.Count(RepairLadder.IsVerdictRound);
            int highestRung = stepEvents.Where(e => e.Rung is not null).Select(e => e.Rung!.Value).DefaultIfEmpty(RepairLadder.RequestedTierRung).Max();
            if (failedRounds > 0 || highestRung > RepairLadder.RequestedTierRung)
            {
                int undone = stepEvents.Count(e => e.Kind == RunEventKind.RoundRolledBack && e.FailureClass == "RolledBack");
                text.AppendLine($"  - Repair ladder: {failedRounds} failed round{(failedRounds == 1 ? string.Empty : "s")}, reached rung {highestRung} ({RepairLadder.Name(highestRung)})" +
                                (undone > 0 ? $"; {undone} round{(undone == 1 ? " was" : "s were")} undone because the check got worse" : string.Empty));
            }
        }

        text.AppendLine();
        text.AppendLine("## Timeline");
        if (events.Count == 0)
        {
            text.AppendLine("Nothing recorded yet.");
        }

        foreach (PlanRunEvent e in events)
        {
            string ladder = e.Rung is null ? string.Empty : $", rung {e.Rung}{(e.Round is null ? string.Empty : $" round {e.Round}")}";
            string where = e.StepId is null ? string.Empty : $" step {e.StepId}{(e.Attempt is null ? string.Empty : $" attempt {e.Attempt}")}{ladder}";
            string via = string.Join(", ", new[] { e.Tier, e.ModelNode ?? e.Node, e.WorkspaceNode is null ? null : $"workspace {e.WorkspaceNode}" }.Where(part => !string.IsNullOrEmpty(part)));
            text.AppendLine($"- {Local(e.AtUtc)}{where}: {e.Kind.Replace('-', ' ')}{(via.Length > 0 ? $" ({via})" : string.Empty)}");
            if (e.Kind == RunEventKind.FilesChanged)
            {
                text.AppendLine("  > listed above");
            }
            else if (!string.IsNullOrWhiteSpace(e.Detail))
            {
                text.AppendLine($"  > {e.Detail.ReplaceLineEndings(" ")}");
            }

            if (e.CheckBefore is not null || e.CheckAfter is not null)
            {
                text.AppendLine($"  > check: `{e.CheckBefore}` -> `{e.CheckAfter}`");
            }
        }

        return text.ToString().TrimEnd() + "\n";
    }

    private static string Local(DateTimeOffset moment) => moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
        : $"{Math.Max(0, (int)span.TotalSeconds)} s";

    private static void AppendList(StringBuilder text, string heading, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine($"## {heading}");
        foreach (string item in items)
        {
            text.AppendLine($"- {item}");
        }
    }

    private static PlanStep ToStep(PlanStepInput input, int id)
    {
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw new ArgumentException($"Step {id} needs a title.");
        }

        string tier = string.IsNullOrWhiteSpace(input.Tier) ? FleetTiers.Standard : input.Tier.Trim().ToLowerInvariant();
        if (!Tiers.Contains(tier))
        {
            tier = FleetTiers.Standard;
        }

        return new PlanStep(
            id,
            input.Title.Trim(),
            input.Detail?.Trim() ?? string.Empty,
            Clean(input.Files),
            string.IsNullOrWhiteSpace(input.Verify) ? null : input.Verify.Trim(),
            tier,
            StepStatus.Pending,
            null,
            0,
            null,
            null,
            CleanParallelGroup(input.ParallelGroup),
            RetrySafe: input.RetrySafe,
            DependsOn: input.DependsOn is { Length: > 0 } declared ? declared.Where(id => id > 0).Distinct().Order().ToList() : null);
    }

    internal static string? NormalizeRecoveryScope(string? scope) =>
        scope?.Trim().ToLowerInvariant() switch
        {
            PlanRecoveryScope.WorkerOnly => PlanRecoveryScope.WorkerOnly,
            PlanRecoveryScope.AllowHubRescue => PlanRecoveryScope.AllowHubRescue,
            _ => null
        };

    internal static string? NormalizeHealChecks(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            PlanHealChecks.Auto => PlanHealChecks.Auto,
            PlanHealChecks.Ask => PlanHealChecks.Ask,
            _ => null
        };

    // A check is one command; a model's rewrite of it that runs to pages is not a check.
    private static string? Shorten(string? text) =>
        text is null ? null : text.Length <= 600 ? text : text[..600] + "...";

    private static string? CleanParallelGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        string value = group.Trim().ToLowerInvariant();
        bool validCharacters = value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
        return value.Length <= 24 && char.IsAsciiLetterOrDigit(value[0]) && validCharacters ? value : null;
    }

    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList();

    private PlanRecord? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<PlanRecord>(File.ReadAllText(path), SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private void Save(PlanRecord plan)
    {
        string path = PathFor(plan.Id);
        string temp = path + ".tmp";
        byte[] json = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, SerializerOptions));

        // The plan file is what a restart resumes from, so it must never be half written: written to a temporary file,
        // flushed to the disk (a power cut then cannot leave an empty file behind the rename), then swapped in. A
        // virus scanner holding the file for a moment is waited out rather than stopping a plan in the night.
        for (int tries = 1; ; tries++)
        {
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(json);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && tries < 5)
            {
                Thread.Sleep(200 * tries);
            }
        }

        // A readable copy next to the data, so a run left going overnight can be read with any text
        // viewer. Best effort: the JSON above is the record, this is for people.
        try
        {
            File.WriteAllText(Path.Combine(_directory, plan.Id + ".md"), ToMarkdown(plan));
            if (plan.Events is { Count: > 0 })
            {
                File.WriteAllText(Path.Combine(_directory, plan.Id + ".report.md"), ToReportMarkdown(plan));
            }
        }
        catch (IOException)
        {
        }
    }

    private static string ExportToProject(PlanRecord plan)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            throw new PlanExportException("This plan has no project folder, so it cannot be exported there.");
        }

        try
        {
            string root = Path.GetFullPath(plan.WorkingDirectory);
            if (!Directory.Exists(root))
            {
                throw new PlanExportException($"The project folder does not exist: {root}");
            }

            string metadataDirectory = Path.Combine(root, ".agent-fleet");
            EnsureExportDirectory(metadataDirectory);
            string plansDirectory = Path.Combine(metadataDirectory, "plans");
            EnsureExportDirectory(plansDirectory);

            string target = Path.GetFullPath(Path.Combine(plansDirectory, plan.Id + ".md"));
            string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            StringComparison pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!target.StartsWith(rootPrefix, pathComparison))
            {
                throw new PlanExportException("The export path would leave the project folder.");
            }

            if (IsLinkOrReparsePoint(target))
            {
                throw new PlanExportException("The existing plan export path is a link; refusing to overwrite it.");
            }

            if (Directory.Exists(target))
            {
                throw new PlanExportException($"A directory already exists at the plan export path: {target}");
            }

            if (File.Exists(target))
            {
                if (!File.ReadAllText(target).Contains(Marker(plan.Id), StringComparison.Ordinal))
                {
                    throw new PlanExportException($"A file already exists at the plan export path: {target}");
                }
            }

            string temporary = Path.Combine(plansDirectory, $".{plan.Id}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, ToMarkdown(plan with { ExportedPlanPath = target }), new UTF8Encoding(false));
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return target;
        }
        catch (PlanExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PlanExportException($"Could not export the plan into its project folder: {exception.Message}");
        }
    }

    private static void EnsureExportDirectory(string path)
    {
        if (IsLinkOrReparsePoint(path))
        {
            throw new PlanExportException($"The export folder is a link; refusing to write through it: {path}");
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        if (IsLinkOrReparsePoint(path))
        {
            throw new PlanExportException($"The export folder is a link; refusing to write through it: {path}");
        }
    }

    private static bool IsLinkOrReparsePoint(string path)
    {
        try
        {
            if (new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null)
            {
                return true;
            }

            return (Directory.Exists(path) || File.Exists(path)) &&
                   (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // If link status cannot be checked, fail closed for this optional write.
            return true;
        }
    }

    private string WorkerBaselinePath(string planId, int stepId, int attempt)
    {
        if (!IsValidId(planId) || stepId < 1 || attempt < 1) throw new ArgumentException("Invalid worker baseline identity.");
        return Path.Combine(WorkerBaselinesDirectory, planId, $"{stepId}-{attempt}.baseline");
    }

    private string PathFor(string id) => Path.Combine(_directory, id + ".json");

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex PlanIdPattern();

    [GeneratedRegex(@"\[plan:(?<id>[0-9a-f]{32})\]")]
    private static partial Regex MarkerPattern();
}

internal sealed class PlanExportException(string message) : Exception(message);
