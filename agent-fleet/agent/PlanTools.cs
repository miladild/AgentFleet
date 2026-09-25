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
internal sealed record StepCompletion(bool Done, string Message, string? Output = null);

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
    private readonly ConcurrentDictionary<string, int> _diagramAttempts = new(StringComparer.Ordinal);

    /// <param name="programExists">Whether a check's program is installed; defaults to looking on PATH (a seam for tests).</param>
    public PlanTools(
        FleetPlanStore store,
        IDiagramValidator validator,
        Func<string, string?, CancellationToken, Task<string>> runCommand,
        FleetContextJournal? journal = null,
        Func<string, bool>? programExists = null)
    {
        _programExists = programExists;
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
        IReadOnlyList<string> problems = PlanReview.Problems(workingDirectory, stepInputs, _programExists);
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
                stepInputs);
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

    /// <summary>The same review propose_plan does, without saving anything: for a plan written outside the fleet.</summary>
    public IReadOnlyList<string> Review(string? workingDirectory, JsonElement? steps)
    {
        List<PlanStepInput> stepInputs = ToSteps(steps);
        return stepInputs.Count == 0
            ? ["The plan has no steps: send steps as an array of objects with title, detail, files, verify and tier."]
            : PlanReview.Problems(workingDirectory, stepInputs, _programExists);
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
        IReadOnlyCollection<string>? requiredFiles = null)
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

        PlanStep? earlier = plan.Steps.FirstOrDefault(candidate =>
            candidate.Id < stepId &&
            candidate.Status != StepStatus.Done &&
            (step.ParallelGroup is null || candidate.ParallelGroup != step.ParallelGroup));
        if (earlier is not null)
        {
            return new StepCompletion(false, $"Error: step {earlier.Id} ({earlier.Title}) is not done yet. Steps go in order. {marker}");
        }

        if (step.Status == StepStatus.Done)
        {
            return new StepCompletion(true, $"Step {stepId} is already done. {marker}");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        plan = _store.Update(plan.Id, current => WithStep(current, stepId, s => s with
        {
            Status = StepStatus.Running,
            Attempts = s.Attempts + 1,
            StartedUtc = s.StartedUtc ?? now
        }) with { Status = PlanStatus.Running })!;
        step = plan.Steps.First(candidate => candidate.Id == stepId);

        bool passed = true;
        string? output = null;
        if (step.Verify is not null)
        {
            string result = await _runCommand(step.Verify, plan.WorkingDirectory, cancellationToken);
            passed = result.StartsWith("Exit code: 0", StringComparison.Ordinal);
            output = result.Length > MaxVerifyOutputCharacters
                ? "..." + result[^MaxVerifyOutputCharacters..]
                : result;
        }

        if (passed && requiredFiles is { Count: > 0 })
        {
            // A named folder ("test/") counts once it exists.
            string[] missing = requiredFiles.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToArray();
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

        PlanRecord updated = _store.Update(plan.Id, current =>
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

        var reply = new StringBuilder();
        reply.AppendLine(step.Verify is null
            ? $"Step {stepId} marked done (it had no verify command, so nothing was checked). {marker}"
            : $"Step {stepId} verified and marked done. {marker}");
        PlanStep? next = updated.Steps.FirstOrDefault(candidate => candidate.Status != StepStatus.Done);
        reply.Append(next is null
            ? "All steps are done. Give the user a short summary of what changed."
            : $"Next: step {next.Id}, {next.Title}.");
        return new StepCompletion(true, reply.ToString(), output);
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
            Field(step, "parallelGroup", "parallel_group"));
    }

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

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+")]
    private static partial Regex LeadingBullet();

    [GeneratedRegex(@"[.!?:](?=\s)|\n")]
    private static partial Regex TitleBoundary();
}
