using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// The tools the model uses to work with plans. The rules that matter are enforced here
/// and in PlanGate rather than asked for in a prompt: a step only counts as done when its
/// own verify command passes (the command comes from the approved plan, not from the
/// model), steps go in order except within an approved parallel group, and nothing can be completed before the user approves.
/// </summary>
/// <param name="Restored">Files the fleet put back from git because the step deleted them and no step names them.</param>
internal sealed record StepCompletion(bool Done, string Message, string? Output = null, string? Restored = null);

internal sealed partial class PlanTools
{
    private const int MaxVerifyOutputCharacters = 2500;
    private const int MaxDiagramAttempts = 2;
    private const int MaxDiagramCharacters = 6000;

    private readonly FleetPlanStore _store;
    private readonly IDiagramValidator _validator;
    private readonly Func<string, string?, CancellationToken, Task<string>> _runCommand;
    private readonly FleetContextJournal? _journal;
    private readonly Func<string, bool>? _programExists;
    private readonly bool _workerWorkspacesEnabled;
    private readonly ConcurrentDictionary<string, int> _diagramAttempts = new(StringComparer.Ordinal);

    /// <param name="programExists">Whether a check's program is installed; defaults to looking on PATH (a seam for tests).</param>
    public PlanTools(
        FleetPlanStore store,
        IDiagramValidator validator,
        Func<string, string?, CancellationToken, Task<string>> runCommand,
        FleetContextJournal? journal = null,
        Func<string, bool>? programExists = null,
        bool workerWorkspacesEnabled = false)
    {
        _programExists = programExists;
        _workerWorkspacesEnabled = workerWorkspacesEnabled;
        _store = store;
        _validator = validator;
        _runCommand = runCommand;
        _journal = journal;
    }

    // The list-shaped arguments are taken as raw JSON on purpose. Measured with the fleet's own
    // model: it sent assumptions and risks as plain strings and steps as an array of strings,
    // which a strictly typed schema rejects before this code runs - with the framework hiding
    // the reason, so the model retried the same mistake. Accepting the shapes models really
    // produce turns that into a plan instead of an error.
    public async Task<string> ProposePlanAsync(
        string title,
        string goal,
        string? workingDirectory,
        JsonElement? assumptions,
        JsonElement? openQuestions,
        JsonElement? risks,
        string? diagram,
        JsonElement? steps,
        CancellationToken cancellationToken)
    {
        List<PlanStepInput> stepInputs = ToSteps(steps);
        if (stepInputs.Count == 0)
        {
            return "Error: the plan has no steps. Call propose_plan again with a steps array: each step an object with " +
                   "title, detail, files, verify and tier.";
        }

        // Mistakes that would only show in the night, as a blocked plan: caught now, while the planner can fix them.
        Func<string, bool>? programExists = _workerWorkspacesEnabled ? _ => true : _programExists;
        var problems = PlanReview.Problems(workingDirectory, stepInputs, programExists).ToList();
        if (!_workerWorkspacesEnabled)
        {
            problems.AddRange(await ProjectToolchainReview.ProblemsAsync(workingDirectory, stepInputs, cancellationToken));
        }
        if (problems.Count > 0)
        {
            return "The plan was NOT saved, because it would fail when it runs:\n" +
                   string.Join('\n', problems.Select(problem => $"- {problem}")) +
                   "\n\nFix these and call propose_plan again with the whole plan.";
        }

        string? diagramCode = null;
        string? diagramNote = null;
        if (!string.IsNullOrWhiteSpace(diagram))
        {
            if (diagram.Length > MaxDiagramCharacters)
            {
                return $"Error: the diagram is longer than {MaxDiagramCharacters} characters. Make it smaller and call propose_plan again.";
            }

            DiagramCheck check = await _validator.CheckAsync(diagram, cancellationToken);
            string attemptKey = title.Trim().ToLowerInvariant();
            if (check.Valid)
            {
                diagramCode = check.Code;
                if (!check.Validated)
                {
                    diagramNote = "The diagram could not be checked for syntax errors.";
                }
            }
            else
            {
                int attempts = _diagramAttempts.AddOrUpdate(attemptKey, 1, (_, count) => count + 1);
                if (attempts < MaxDiagramAttempts)
                {
                    return "The plan was NOT saved because its Mermaid diagram has a syntax error:\n" +
                           $"{check.Error}\n\n" +
                           "Fix the diagram and call propose_plan again with the same steps. Put every node label in double " +
                           "quotes, for example A[\"Label (details)\"], and avoid special characters in ids.";
                }

                // A second failure: keep the plan, drop the diagram, and say so. A plan is worth
                // more than its picture.
                diagramNote = $"The diagram was left out because it kept failing to parse ({check.Error}).";
            }
        }

        PlanRecord plan;
        try
        {
            plan = _store.Create(
                title,
                goal,
                workingDirectory,
                ToStringList(assumptions),
                ToStringList(openQuestions),
                ToStringList(risks),
                diagramCode,
                diagramNote,
                stepInputs,
                // Escalation to the hub follows the plan's scope, not the fleet mode (see RepairLadder): a plan runs
                // unattended, so a step its worker cannot fix is offered to the hub unless the user keeps the plan on
                // its workers at approval, where the choice is shown.
                PlanRecoveryScope.AllowHubRescue);
        }
        catch (ArgumentException exception)
        {
            return $"Error: {exception.Message}";
        }

        plan = LinkToContext(plan);
        string replaced = ReplaceOlderProposals(plan);

        return $"Plan saved. {FleetPlanStore.Marker(plan.Id)}{replaced}\n" +
               "The plan is waiting for the user's approval and nothing has been changed. Stop here. In two or three sentences " +
               "tell the user the plan is ready to review (in the Plans panel, or reply APPROVE), and mention any open question. " +
               "Do not start the work.\n\n" +
               FleetPlanStore.ToMarkdown(plan);
    }

    /// <summary>
    /// propose_plan for a plan file the user pointed to: the steps come from the file exactly as written (see
    /// PlanFile), then get the same review, store and approval as any other plan.
    /// </summary>
    public async Task<string> ProposePlanFromFileAsync(string planFile, CancellationToken cancellationToken)
    {
        PlanFileContent content;
        try
        {
            content = PlanFile.Read(planFile);
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            return $"The plan file could not be read: {exception.Message} Use propose_plan with the steps instead, " +
                   "or ask the user for the right file.";
        }

        string result = await ProposePlanAsync(
            content.Title,
            content.Goal,
            content.WorkingDirectory,
            JsonSerializer.SerializeToElement(content.Assumptions),
            openQuestions: null,
            JsonSerializer.SerializeToElement(content.Risks),
            diagram: null,
            JsonSerializer.SerializeToElement(content.Steps.Select(step => new
            {
                title = step.Title,
                detail = step.Detail,
                files = step.Files ?? [],
                verify = step.Verify,
                tier = step.Tier,
                parallelGroup = step.ParallelGroup,
                dependsOn = step.DependsOn
            })),
            cancellationToken);

        return result.StartsWith("Plan saved", StringComparison.Ordinal)
            ? result
            : result.Replace("call propose_plan again with the whole plan", "tell the user what to change in the plan file");
    }

    /// <summary>The same review propose_plan does, without saving anything: for a plan written outside the fleet.</summary>
    public IReadOnlyList<string> Review(string? workingDirectory, JsonElement? steps)
    {
        List<PlanStepInput> stepInputs = ToSteps(steps);
        return stepInputs.Count == 0
            ? ["The plan has no steps: send steps as an array of objects with title, detail, files, verify and tier."]
            : PlanReview.Problems(workingDirectory, stepInputs, _workerWorkspacesEnabled ? _ => true : _programExists);
    }

    // One chat, one plan waiting for approval: a revised plan replaces the earlier proposal instead of leaving two
    // sets of Approve buttons (measured: a planner called propose_plan three times in one turn).
    private string ReplaceOlderProposals(PlanRecord plan)
    {
        if (plan.ContextId is null)
        {
            return string.Empty;
        }

        var replaced = new List<string>();
        foreach (PlanSummary summary in _store.List().Where(summary => summary.Status == PlanStatus.AwaitingApproval && summary.Id != plan.Id))
        {
            if (_store.Get(summary.Id) is { } older && older.ContextId == plan.ContextId && _store.Reject(older.Id) is not null)
            {
                replaced.Add(older.Id);
            }
        }

        return replaced.Count == 0
            ? string.Empty
            : $"\nIt replaces the earlier proposal in this conversation ({string.Join(", ", replaced.Select(FleetPlanStore.Marker))}), which is withdrawn.";
    }

    // A plan proposed in a chat belongs to that chat's durable context, so what the user decided while
    // planning is still there when the plan runs, whichever machine runs each step.
    private PlanRecord LinkToContext(PlanRecord plan)
    {
        FleetRequestIdentity? identity = _journal?.Current;
        if (_journal is null || identity is null)
        {
            return plan;
        }

        try
        {
            PlanRecord linked = _store.Update(plan.Id, current => current with { ContextId = identity.ContextId }) ?? plan;
            _journal.Store.UpsertTask(identity.ContextId, FleetPlanContext.PlanTaskId(plan.Id), "plan", PlanStatus.AwaitingApproval, plan.Title);
            _journal.Store.AppendEvent(
                identity.ContextId,
                FleetContextEventKind.PlanTransition,
                new { planId = plan.Id, to = PlanStatus.AwaitingApproval, note = $"Plan proposed: {plan.Title} ({plan.Steps.Count} steps)" },
                FleetPlanContext.PlanTaskId(plan.Id), actor: "assistant", agent: "fleet");
            return linked;
        }
        catch (Exception)
        {
            return plan;
        }
    }

    public string GetPlan(string? planId)
    {
        PlanRecord? plan = string.IsNullOrWhiteSpace(planId) ? _store.FindActive() : _store.Get(planId.Trim());
        return plan is null
            ? "There is no plan to show."
            : $"{FleetPlanStore.Marker(plan.Id)}\n{FleetPlanStore.ToMarkdown(plan)}";
    }

    public async Task<string> ValidateDiagramAsync(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "Error: provide Mermaid source to validate.";
        }

        if (code.Length > MaxDiagramCharacters)
        {
            return $"Error: the diagram is longer than {MaxDiagramCharacters} characters.";
        }

        DiagramCheck check = await _validator.CheckAsync(code, cancellationToken);
        string fencedCode = $"```mermaid\n{check.Code}\n```";
        if (!check.Validated)
        {
            return "The Mermaid checker is unavailable, so this source is unchecked. The checker applied its safe formatting fixes; " +
                   "you can still use or edit the source below.\n\n" + fencedCode;
        }

        return check.Valid
            ? "The Mermaid source is valid after safe formatting fixes.\n\n" + fencedCode
            : $"The Mermaid source is invalid: {check.Error}\n\nSanitized source:\n\n{fencedCode}";
    }

    public async Task<string> CompleteStepAsync(string planId, int stepId, string? note, CancellationToken cancellationToken) =>
        (await TryCompleteStepAsync(planId, stepId, note, cancellationToken)).Message;

    // Runs the step's own verify command and only then marks it done. The command comes from the
    // approved plan, never from whoever is asking, so a step cannot be waved through.
    //
    // requiredFiles: paths the step named that did not exist when it started. A check such as `node --test`
    // passes with no tests at all, so a step that was meant to create a file cannot be waved through by a
    // model that never created it: those files must exist afterwards.
    public async Task<StepCompletion> TryCompleteStepAsync(
        string planId,
        int stepId,
        string? note,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? requiredFiles = null,
        bool deferCommit = false)
    {
        PlanRecord? plan = _store.Get(planId?.Trim() ?? string.Empty);
        if (plan is null)
        {
            return new StepCompletion(false, "Error: there is no plan with that id.");
        }

        string marker = FleetPlanStore.Marker(plan.Id);
        if (plan.Status is not (PlanStatus.Approved or PlanStatus.Running))
        {
            return new StepCompletion(
                false,
                $"Error: plan is {plan.Status.Replace('-', ' ')}, so no step can be completed. {marker}\n" +
                "It needs the user's approval first. Do not change anything until it is approved.");
        }

        PlanStep? step = plan.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
        if (step is null)
        {
            return new StepCompletion(false, $"Error: the plan has no step {stepId}. {marker}");
        }

        PlanStep? earlier = PlanGraph.Unmet(plan, step).FirstOrDefault();
        if (earlier is not null)
        {
            return new StepCompletion(false, $"Error: step {earlier.Id} ({earlier.Title}) is not done yet, and this step depends on it. {marker}");
        }

        if (step.Status == StepStatus.Done)
        {
            return new StepCompletion(true, $"Step {stepId} is already done. {marker}");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        plan = _store.Update(plan.Id, current => WithStep(current, stepId, s => s with
        {
            Status = StepStatus.Running,
            StartedUtc = s.StartedUtc ?? now
        }) with { Status = PlanStatus.Running })!;
        step = plan.Steps.First(candidate => candidate.Id == stepId);

        // Before the check (the model's own test runs may have deleted something the check needs) and after it.
        List<string> restored = [.. await RestoreUnnamedDeletionsAsync(plan, cancellationToken)];
        bool passed = true;
        string? output = null;
        if (step.Verify is not null)
        {
            if (PlanRunnerToolPolicy.CommandRefusal(step.Verify) is { } refusal)
            {
                string blocked = $"Blocked by unattended plan policy: the approved check cannot run safely. {refusal}";
                return new StepCompletion(false, blocked, blocked);
            }

            string result = await _runCommand(step.Verify, plan.WorkingDirectory, cancellationToken);
            passed = result.StartsWith("Exit code: 0", StringComparison.Ordinal);
            output = ShortenCheckOutput(FailureLocation.ProjectRelative(result, plan.WorkingDirectory));
            restored.AddRange(await RestoreUnnamedDeletionsAsync(plan, cancellationToken));
        }

        string? restoredNote = restored.Count == 0
            ? null
            : $"The fleet put back {string.Join(", ", restored.Distinct(StringComparer.OrdinalIgnoreCase))} from git: deleted while this " +
              "step ran, and no step of the plan names it. Tests and checks must not delete project files; a test that needs a " +
              "file of its own writes it under the system's temp folder.";
        if (restoredNote is not null && !passed)
        {
            output = $"{output}\n\n{restoredNote}";
        }

        if (passed && requiredFiles is { Count: > 0 })
        {
            // A named folder ("test/") counts once it exists, a pattern ("test/*.test.ts") once a file matches it.
            string[] missing = requiredFiles.Where(path => !FleetPlanContext.DeclaredExists(path)).ToArray();
            if (missing.Length > 0)
            {
                string shown = string.Join(", ", missing.Select(path => RelativeTo(plan.WorkingDirectory, path)));
                string message = $"The step's check passed, but the file(s) this step was meant to create still do not exist: {shown}.";
                _store.Update(plan.Id, current => WithStep(current, stepId, s => s with
                {
                    Status = StepStatus.Failed,
                    Note = $"Attempt {s.Attempts} did not create {shown}."
                }));
                return new StepCompletion(
                    false,
                    $"Step {stepId} is NOT done: {message} (attempt {step.Attempts}). {marker}\n\n" +
                    "Create them with write_file (in the project folder) and try again. A passing check is not enough when the work was never done.",
                    message);
            }
        }

        if (!passed)
        {
            _store.Update(plan.Id, current => WithStep(current, stepId, s => s with
            {
                Status = StepStatus.Failed,
                Note = $"Verification failed on attempt {s.Attempts}."
            }));
            return new StepCompletion(
                false,
                $"Step {stepId} is NOT done: its verify command `{step.Verify}` failed (attempt {step.Attempts}). {marker}\n{output}\n\n" +
                "Read the output, fix the cause, and try again. If you cannot fix it, say so instead of claiming it works.",
                output);
        }

        if (deferCommit)
        {
            return new StepCompletion(true, $"Step {stepId} passed its approved check; waiting for the worker sync before marking it done.", output, restoredNote);
        }

        PlanRecord updated = CommitStepDone(plan.Id, stepId, note);

        var reply = new StringBuilder();
        reply.AppendLine(step.Verify is null
            ? $"Step {stepId} marked done (it had no verify command, so nothing was checked). {marker}"
            : $"Step {stepId} verified and marked done. {marker}");
        PlanStep? next = updated.Steps.FirstOrDefault(candidate => candidate.Status != StepStatus.Done);
        reply.Append(next is null
            ? "All steps are done. Give the user a short summary of what changed."
            : $"Next: step {next.Id}, {next.Title}.");
        return new StepCompletion(true, reply.ToString(), output, restoredNote);
    }

    /// <summary>
    /// Runs one of the environment doctor's commands (EnvironmentDoctor) the way a check runs: in the plan's folder, on
    /// the machine the current worker scope selects, as the same limited account, and under the same policy. The
    /// command comes from the doctor's own list, never from a model.
    /// </summary>
    internal async Task<(bool Succeeded, string Output)> RunRepairCommandAsync(PlanRecord plan, string command, CancellationToken cancellationToken)
    {
        if (PlanRunnerToolPolicy.CommandRefusal(command) is { } refusal)
        {
            return (false, refusal);
        }

        string result = await _runCommand(command, plan.WorkingDirectory, cancellationToken);
        return (result.StartsWith("Exit code: 0", StringComparison.Ordinal), ShortenCheckOutput(FailureLocation.ProjectRelative(result, plan.WorkingDirectory)));
    }

    public PlanRecord CommitStepDone(string planId, int stepId, string? note)
    {
        return _store.Update(planId, current =>
        {
            PlanRecord withStep = WithStep(current, stepId, s => s with
            {
                Status = StepStatus.Done,
                Note = string.IsNullOrWhiteSpace(note) ? s.Note : note.Trim(),
                CompletedUtc = DateTimeOffset.UtcNow
            });
            return withStep.Steps.All(s => s.Status == StepStatus.Done)
                ? withStep with { Status = PlanStatus.Done }
                : withStep;
        })!;
    }

    /// <summary>
    /// Tracked files deleted while the plan runs that no step of it names, put back from git. Measured: a test a model
    /// wrote removed the project's config/watchlist.json in its cleanup, so every run of the check deleted it. Works in a
    /// git working tree only; elsewhere, and when git is missing, it does nothing. Returns the files put back.
    /// </summary>
    internal async Task<IReadOnlyList<string>> RestoreUnnamedDeletionsAsync(PlanRecord plan, CancellationToken cancellationToken)
    {
        // Worker paths have their own OS format (for example /home/agentfleet/project). The worker workspace sync
        // transfers deletions back by its staged-file manifest; comparing those paths with hub paths here would
        // incorrectly restore named worker edits from git.
        if (WorkerWorkspaceContext.Current is not null)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !Directory.Exists(plan.WorkingDirectory))
        {
            return [];
        }

        try
        {
            string? root = Stdout(await _runCommand("git rev-parse --show-toplevel", plan.WorkingDirectory, cancellationToken))?.Trim();
            string? status = Stdout(await _runCommand("git -c core.quotepath=off status --porcelain=v1 --untracked-files=no", plan.WorkingDirectory, cancellationToken));
            if (string.IsNullOrWhiteSpace(root) || status is null)
            {
                return [];
            }

            string[] named = plan.Steps.SelectMany(step => FleetPlanContext.DeclaredPaths(plan, step)).ToArray();

            var restored = new List<string>();
            foreach (string line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = line.TrimEnd('\r');
                if (entry.Length < 4 || (entry[0] != 'D' && entry[1] != 'D') || entry.Contains(" -> ", StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = entry[3..].Trim().Trim('"');
                string full = Path.GetFullPath(Path.Combine(root, relative));
                if (named.Any(declared => FleetPlanContext.Names(declared, full)))
                {
                    continue;
                }

                string restore = await _runCommand($"git checkout HEAD -- \"{relative}\"", root, cancellationToken);
                if (restore.StartsWith("Exit code: 0", StringComparison.Ordinal))
                {
                    restored.Add(RelativeTo(plan.WorkingDirectory, full));
                }
            }

            return restored;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// What the plan had changed or created in a git project at one moment: the files git sees as changed or new (not
    /// ignored) with their contents, and what else it takes to put the project back exactly so: which of them git did not
    /// track, which tracked files were deleted, and which changed files were too big to keep (those cannot be put back).
    /// Taken before an attempt, so the attempt cannot throw earlier work away, and after a round, as a checkpoint.
    /// </summary>
    internal sealed record WorkSnapshot(
        string Head,
        IReadOnlyDictionary<string, byte[]> Files,
        IReadOnlySet<string> Untracked,
        IReadOnlySet<string> Deleted,
        IReadOnlySet<string> Uncaptured);

    /// <summary>What putting the project back to a snapshot did.</summary>
    /// <param name="Restored">The files written back, removed or checked out again, as paths inside the project.</param>
    /// <param name="Left">Changed files it could not put back (too big to keep, or behind a link).</param>
    /// <param name="Refusal">Why nothing was done, when the project is not in a state it can reason about.</param>
    internal sealed record WorkRestore(IReadOnlyList<string> Restored, IReadOnlyList<string> Left, string? Refusal = null);

    private const long MaxSnapshotFileBytes = 2_000_000;
    private const long MaxSnapshotBytes = 50_000_000;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The files git sees as changed or new (not ignored) before an attempt, with their contents. Measured: a model on
    /// its last round ran `git checkout HEAD -- src/server/marketHours.ts` and threw away its own step's work; nothing
    /// is committed between steps, so `git checkout -- .`, `git stash` or `git clean` would throw away every earlier
    /// step's. Null outside a git working tree or when git is missing.
    /// </summary>
    internal async Task<WorkSnapshot?> SnapshotWorkAsync(PlanRecord plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !Directory.Exists(plan.WorkingDirectory))
        {
            return null;
        }

        try
        {
            if (await GitStateAsync(plan.WorkingDirectory, cancellationToken) is not { } state)
            {
                return null;
            }

            var files = new Dictionary<string, byte[]>(PathComparer);
            var untracked = new HashSet<string>(PathComparer);
            var deleted = new HashSet<string>(PathComparer);
            var uncaptured = new HashSet<string>(PathComparer);
            long total = 0;
            foreach (GitEntry entry in state.Entries)
            {
                if (entry.Deleted)
                {
                    deleted.Add(entry.FullPath);
                    continue;
                }

                if (entry.Untracked)
                {
                    untracked.Add(entry.FullPath);
                }

                var info = new FileInfo(entry.FullPath);
                if (!info.Exists)
                {
                    continue;
                }

                if (info.Length <= MaxSnapshotFileBytes && total + info.Length <= MaxSnapshotBytes)
                {
                    files[entry.FullPath] = await File.ReadAllBytesAsync(entry.FullPath, cancellationToken);
                    total += info.Length;
                }
                else
                {
                    uncaptured.Add(entry.FullPath);
                }
            }

            return new WorkSnapshot(state.Head, files, untracked, deleted, uncaptured);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// After an attempt: the files of the snapshot that are gone, or back to what git has committed, and that no
    /// unfinished step names, written back as they were. A file an unfinished step names (this step's own, or one a
    /// step running beside it or after it will write) is that step's to change. When the attempt committed (HEAD moved),
    /// only deleted files come back, so nothing committed is overwritten. Returns the files put back.
    /// </summary>
    internal async Task<IReadOnlyList<string>> RestoreDiscardedWorkAsync(PlanRecord plan, WorkSnapshot? snapshot, CancellationToken cancellationToken)
    {
        if (snapshot is null || snapshot.Files.Count == 0 || string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return [];
        }

        try
        {
            if (await GitStateAsync(plan.WorkingDirectory, cancellationToken) is not { } state)
            {
                return [];
            }

            string[] unfinished = plan.Steps
                .Where(step => step.Status != StepStatus.Done)
                .SelectMany(step => FleetPlanContext.DeclaredPaths(plan, step))
                .ToArray();
            var changedNow = new HashSet<string>(state.Changed, PathComparer);
            bool committed = !string.Equals(state.Head, snapshot.Head, StringComparison.Ordinal);

            var restored = new List<string>();
            foreach ((string path, byte[] contents) in snapshot.Files)
            {
                bool named = unfinished.Any(declared => FleetPlanContext.Names(declared, path));
                bool discarded = !File.Exists(path) || (!committed && !changedNow.Contains(path));
                if (named || !discarded)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, contents, cancellationToken);
                restored.Add(RelativeTo(plan.WorkingDirectory, path));
            }

            return restored;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Puts the project's changed and new files back to a snapshot taken earlier in the same run: every file the snapshot
    /// held gets its contents again, a file that is changed or new now but was not then goes back to what git has committed
    /// (or is removed when git does not track it), and a tracked file deleted since comes back. Used to undo a round that
    /// left the step worse than its best round, so it does not touch HEAD, the index of anything git was not already
    /// tracking, or a path behind a link. Refuses (and changes nothing) when git cannot say what the project looks like or
    /// HEAD moved since the snapshot.
    /// </summary>
    internal async Task<WorkRestore> RestoreToSnapshotAsync(PlanRecord plan, WorkSnapshot target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !Directory.Exists(plan.WorkingDirectory))
        {
            return new WorkRestore([], [], "the project folder is not there");
        }

        try
        {
            if (await GitStateAsync(plan.WorkingDirectory, cancellationToken) is not { } state)
            {
                return new WorkRestore([], [], "git could not say what the project looks like now");
            }

            if (!string.Equals(state.Head, target.Head, StringComparison.Ordinal))
            {
                return new WorkRestore([], [], "the repository was committed to since the checkpoint");
            }

            var restored = new List<string>();
            var left = new List<string>();
            string Shown(string path) => RelativeTo(plan.WorkingDirectory, path).Replace('\\', '/');

            // What the snapshot held, as it held it.
            foreach ((string path, byte[] contents) in target.Files)
            {
                if (PlanRunner.HasLinkedPathComponent(state.Root, path))
                {
                    left.Add(Shown(path));
                    continue;
                }

                if (File.Exists(path) && new FileInfo(path).Length == contents.Length &&
                    (await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(contents))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, contents, cancellationToken);
                restored.Add(Shown(path));
            }

            // What is changed, new or gone now and was not then.
            foreach (GitEntry entry in state.Entries)
            {
                if (target.Files.ContainsKey(entry.FullPath) || target.Deleted.Contains(entry.FullPath))
                {
                    continue;
                }

                if (target.Uncaptured.Contains(entry.FullPath) || PlanRunner.HasLinkedPathComponent(state.Root, entry.FullPath))
                {
                    left.Add(Shown(entry.FullPath));
                    continue;
                }

                if (entry.Untracked)
                {
                    // Created after the snapshot, so it did not exist then.
                    if (File.Exists(entry.FullPath))
                    {
                        File.Delete(entry.FullPath);
                        restored.Add(Shown(entry.FullPath));
                    }

                    continue;
                }

                // A tracked file, modified or deleted since: at the snapshot it was as committed.
                string result = await _runCommand($"git checkout HEAD -- \"{entry.Relative}\"", state.Root, cancellationToken);
                if (result.StartsWith("Exit code: 0", StringComparison.Ordinal))
                {
                    restored.Add(Shown(entry.FullPath));
                }
                else
                {
                    left.Add(Shown(entry.FullPath));
                }
            }

            // A tracked file the snapshot had deleted and that is back since.
            foreach (string path in target.Deleted)
            {
                if (File.Exists(path) && !PlanRunner.HasLinkedPathComponent(state.Root, path))
                {
                    File.Delete(path);
                    restored.Add(Shown(path));
                }
            }

            return new WorkRestore(restored.Order(StringComparer.OrdinalIgnoreCase).ToArray(), left.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new WorkRestore([], [], $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>One line of git's status: the file, whether git does not track it, and whether it is deleted.</summary>
    private sealed record GitEntry(string FullPath, string Relative, bool Untracked, bool Deleted);

    private sealed record GitState(string Root, string Head, IReadOnlyList<GitEntry> Entries)
    {
        /// <summary>The full paths of the files that are changed or new (a deleted file is not one: it is not there).</summary>
        public IEnumerable<string> Changed => Entries.Where(entry => !entry.Deleted).Select(entry => entry.FullPath);
    }

    // The repository root, HEAD and every changed, new or deleted file, or null outside a git working tree.
    private async Task<GitState?> GitStateAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string? root = Stdout(await _runCommand("git rev-parse --show-toplevel", workingDirectory, cancellationToken))?.Trim();
        string? head = Stdout(await _runCommand("git rev-parse HEAD", workingDirectory, cancellationToken))?.Trim();
        string? status = Stdout(await _runCommand("git -c core.quotepath=off status --porcelain=v1 --untracked-files=all", workingDirectory, cancellationToken));
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(head) || status is null)
        {
            return null;
        }

        var entries = new List<GitEntry>();
        foreach (string line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = line.TrimEnd('\r');
            if (entry.Length < 4)
            {
                continue;
            }

            // A rename lists "old -> new"; the file on disk is the new one.
            string relative = entry[3..];
            int arrow = relative.IndexOf(" -> ", StringComparison.Ordinal);
            relative = (arrow >= 0 ? relative[(arrow + 4)..] : relative).Trim().Trim('"');
            entries.Add(new GitEntry(
                Path.GetFullPath(Path.Combine(root, relative)),
                relative,
                Untracked: entry[0] == '?',
                Deleted: entry[0] == 'D' || entry[1] == 'D'));
        }

        return new GitState(root, head, entries);
    }

    // The stdout part of a command result, or null when the command did not succeed.
    private static string? Stdout(string result)
    {
        if (!result.StartsWith("Exit code: 0", StringComparison.Ordinal))
        {
            return null;
        }

        const string StdoutMarker = "--- stdout ---";
        int start = result.IndexOf(StdoutMarker, StringComparison.Ordinal);
        string body = start < 0 ? string.Empty : result[(start + StdoutMarker.Length)..];
        int stderrAt = body.IndexOf("--- stderr ---", StringComparison.Ordinal);
        return (stderrAt >= 0 ? body[..stderrAt] : body).Trim('\r', '\n');
    }

    /// <summary>
    /// The files git sees as changed in the plan's working directory, for the run report ("what did it do
    /// to my project overnight"). Null when the folder is not a git repository or git is not available,
    /// and empty text when nothing changed. Read-only.
    /// </summary>
    public async Task<string?> ChangedFilesAsync(string? workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        string result;
        try
        {
            result = await _runCommand("git status --short", workingDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        if (!result.StartsWith("Exit code: 0", StringComparison.Ordinal))
        {
            return null;
        }

        const string StdoutMarker = "--- stdout ---";
        int start = result.IndexOf(StdoutMarker, StringComparison.Ordinal);
        string body = start < 0 ? string.Empty : result[(start + StdoutMarker.Length)..];
        int stderrAt = body.IndexOf("--- stderr ---", StringComparison.Ordinal);
        if (stderrAt >= 0)
        {
            body = body[..stderrAt];
        }

        string[] lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        const int MaxLines = 60;
        return lines.Length <= MaxLines
            ? string.Join('\n', lines)
            : string.Join('\n', lines.Take(MaxLines)) + $"\n... and {lines.Length - MaxLines} more";
    }

    private static string RelativeTo(string? root, string path)
    {
        try
        {
            return root is not null && Directory.Exists(root) ? Path.GetRelativePath(root, path) : path;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return path;
        }
    }

    public string FailStep(string planId, int stepId, string reason)
    {
        PlanRecord? plan = _store.Get(planId?.Trim() ?? string.Empty);
        if (plan is null || plan.Steps.All(step => step.Id != stepId))
        {
            return "Error: there is no such plan or step.";
        }

        PlanRecord updated = _store.Update(plan.Id, current => WithStep(current, stepId, s => s with
        {
            Status = StepStatus.Failed,
            Note = string.IsNullOrWhiteSpace(reason) ? "Could not be completed." : reason.Trim()
        }) with { Status = PlanStatus.Blocked })!;
        return $"Plan is blocked at step {stepId}. {FleetPlanStore.Marker(updated.Id)}\n" +
               "Stop working. Tell the user what failed, what you tried, and what you would need to continue.";
    }

    private static PlanRecord WithStep(PlanRecord plan, int stepId, Func<PlanStep, PlanStep> change) =>
        plan with { Steps = plan.Steps.Select(step => step.Id == stepId ? change(step) : step).ToList() };

    // A list of text from whatever the model sent: an array, one string with items on separate
    // lines or split by semicolons, or an object. Placeholders like "None" are dropped so they
    // do not show up as an assumption or risk.
    internal static List<string> ToStringList(JsonElement? value)
    {
        var items = new List<string>();
        if (value is not { } element)
        {
            return items;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                // Some models send an array as a string of JSON text.
                if (TryParseJsonText(element.GetString(), out JsonElement parsed))
                {
                    return ToStringList(parsed);
                }

                items.AddRange(SplitItems(element.GetString()));
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        items.Add(text.Trim());
                    }
                }

                break;
            case JsonValueKind.Object:
                items.Add(element.ToString());
                break;
        }

        return items.Where(item => !IsPlaceholder(item)).ToList();
    }

    // Steps as objects (the documented shape), as plain strings, or as one numbered text block.
    internal static List<PlanStepInput> ToSteps(JsonElement? value)
    {
        var steps = new List<PlanStepInput>();
        if (value is not { } element)
        {
            return steps;
        }

        // Measured on the fleet's own model: it sent `steps` as a string holding the JSON array,
        // which split line by line turns into a "step" per bracket and quote. Parse the text; and
        // if it is clearly meant as JSON but does not parse, report no steps so the model is told
        // the expected shape instead of a plan being saved with garbage in it.
        if (element.ValueKind == JsonValueKind.String)
        {
            string? text = element.GetString();
            if (TryParseJsonText(text, out JsonElement parsed))
            {
                return ToSteps(parsed);
            }

            if (LooksLikeJson(text))
            {
                return steps;
            }
        }

        IEnumerable<JsonElement> entries = element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray().ToList(),
            JsonValueKind.String => SplitItems(element.GetString())
                .Select(line => JsonSerializer.SerializeToElement(line)).ToList(),
            JsonValueKind.Object => [element],
            _ => []
        };

        foreach (JsonElement entry in entries)
        {
            PlanStepInput? step = entry.ValueKind switch
            {
                JsonValueKind.Object => StepFromObject(entry),
                JsonValueKind.String => StepFromText(entry.GetString()),
                _ => null
            };

            if (step is not null)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private static bool LooksLikeJson(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.TrimStart() is ['[' or '{', ..];

    private static bool TryParseJsonText(string? text, out JsonElement parsed)
    {
        parsed = default;
        if (!LooksLikeJson(text))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text!);
            parsed = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PlanStepInput? StepFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The title is the first sentence or clause. A bare '.' is not a boundary: it is inside
        // file names like ratelimit.js.
        string trimmed = text.Trim();
        Match boundary = TitleBoundary().Match(trimmed);
        string title = boundary.Success && boundary.Index is > 0 and <= 90
            ? trimmed[..boundary.Index]
            : trimmed.Length <= 90 ? trimmed : trimmed[..90].TrimEnd() + "...";
        return new PlanStepInput(title.Trim(), trimmed);
    }

    private static PlanStepInput? StepFromObject(JsonElement step)
    {
        string? title = Field(step, "title", "name", "step", "summary");
        string? detail = Field(step, "detail", "details", "description", "what", "action");
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        title = string.IsNullOrWhiteSpace(title) ? StepFromText(detail)!.Title : title;
        return new PlanStepInput(
            title.Trim(),
            detail?.Trim() ?? string.Empty,
            SplitFiles(Raw(step, "files", "file", "paths")),
            Field(step, "verify", "verification", "check", "test", "command"),
            Field(step, "tier", "complexity"),
            Field(step, "parallelGroup", "parallel_group"),
            BooleanField(step, "retrySafe", "retry_safe"),
            StepNumbers(Raw(step, "dependsOn", "depends_on", "dependencies", "depends")));
    }

    // [1, 3], "1, 3" and ["step 1", "Step 3"] all name steps 1 and 3; nothing named means the chain.
    private static int[]? StepNumbers(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        IEnumerable<string> parts = element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray().Select(item => item.ToString()),
            JsonValueKind.String or JsonValueKind.Number => [element.ToString()],
            _ => []
        };
        int[] numbers = parts.SelectMany(PlanGraph.ParseStepNumbers).Distinct().ToArray();
        return numbers.Length > 0 ? numbers : null;
    }

    private static bool BooleanField(JsonElement obj, params string[] names) =>
        Raw(obj, names) is { } raw &&
        (raw.ValueKind == JsonValueKind.True || raw.ValueKind == JsonValueKind.String &&
            bool.TryParse(raw.GetString(), out bool value) && value);

    private static JsonElement? Raw(JsonElement obj, params string[] names)
    {
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? Field(JsonElement obj, params string[] names) =>
        Raw(obj, names) is { } value
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.ToString()
            : null;

    private static string[] SplitFiles(JsonElement? value) =>
        value is not { } element
            ? []
            : element.ValueKind == JsonValueKind.Array
                ? element.EnumerateArray().Select(item => item.ToString().Trim()).Where(item => item.Length > 0).ToArray()
                : element.ValueKind == JsonValueKind.String
                    ? (element.GetString() ?? string.Empty).Split([',', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    : [];

    // "1. do x\n2. do y", "- a\n- b" and "a; b" all become separate items.
    private static IEnumerable<string> SplitItems(string? text) =>
        (text ?? string.Empty)
            .Split(['\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => LeadingBullet().Replace(line, string.Empty).Trim())
            .Where(line => line.Length > 0);

    private static bool IsPlaceholder(string item)
    {
        string text = item.Trim().TrimEnd('.').ToLowerInvariant();
        return text is "" or "-" or "n/a" or "na" or "no" or "nothing" or "null" || text.StartsWith("none", StringComparison.Ordinal);
    }

    /// <summary>
    /// The project folder of the plan step a conversation carries out, from the plan marker in the step's prompt.
    /// Measured: a worker ran the step's check with run_command and no folder, so it ran in the backend's own folder,
    /// could not find the project's tsx, and spent eight minutes trying to install it globally.
    /// </summary>
    public string? StepWorkingDirectory(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        foreach (Microsoft.Extensions.AI.ChatMessage message in messages)
        {
            if (message.Role == Microsoft.Extensions.AI.ChatRole.User && FleetPlanStore.FindMarkerId(message.Text) is { } id)
            {
                return _store.Get(id)?.WorkingDirectory is { } folder && Directory.Exists(folder) ? folder : null;
            }
        }

        return null;
    }

    /// <summary>
    /// A long check output as the next attempt sees it. Only the end fits, but the end of a test run is the detail of
    /// its last failure: with four holiday tests failing, the retry was shown one of them and fixed only that. So the
    /// lines that name failures and totals, from the whole output, come before the end.
    /// </summary>
    internal static string ShortenCheckOutput(string result)
    {
        if (result.Length <= MaxVerifyOutputCharacters)
        {
            return result;
        }

        var summary = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in result.Split('\n'))
        {
            string text = line.Trim();
            if (text.Length is > 0 and <= 200 && !text.StartsWith("at ", StringComparison.Ordinal) &&
                FailureOrTotalLine().IsMatch(text) && seen.Add(text) &&
                summary.Length + text.Length < MaxCheckSummaryCharacters)
            {
                summary.Append(text).Append('\n');
            }
        }

        string end = "..." + result[^MaxVerifyOutputCharacters..];
        return summary.Length == 0 ? end : $"Lines naming failures and totals:\n{summary}\nThe end of the output:\n{end}";
    }

    private const int MaxCheckSummaryCharacters = 1500;

    // node:test and TAP (✖, "not ok", "ℹ fail 4"), jest and vitest ("Tests: 4 failed"), dotnet test ("Failed!"),
    // pytest ("FAILED"), tsc ("error TS2345").
    [GeneratedRegex(@"✖|✗|\bnot ok\b|\bfail(?:ed|ing|ures?|s)?\b|\berror\b|^(?:#|ℹ)\s*(?:tests|pass|fail)\b|\bTests?:", RegexOptions.IgnoreCase)]
    private static partial Regex FailureOrTotalLine();

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+")]
    private static partial Regex LeadingBullet();

    [GeneratedRegex(@"[.!?:](?=\s)|\n")]
    private static partial Regex TitleBoundary();
}
