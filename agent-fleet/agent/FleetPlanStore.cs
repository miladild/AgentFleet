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

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] Tiers = [FleetTiers.Heavy, FleetTiers.Standard, FleetTiers.Light];

    private readonly string _directory;
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
        IReadOnlyList<PlanStepInput> steps)
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
            null);

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
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PlanRecord>(File.ReadAllText(path), SerializerOptions)
                : null;
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
                    plan.UpdatedUtc))
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

    public const int MaxEvents = 400;
    public const int MaxEventDetailCharacters = 1200;
    public const int MaxFilesChangedCharacters = 4000;

    // Appends to the run log. Bounded, so a plan that retries for days cannot grow its file without
    // limit: the oldest lines go first, but the first line (when the run started) is kept.
    public PlanRecord? AddEvent(string id, int? stepId, int? attempt, string kind, string? tier = null, string? node = null, string detail = "")
    {
        string text = detail ?? string.Empty;
        int limit = kind == RunEventKind.FilesChanged ? MaxFilesChangedCharacters : MaxEventDetailCharacters;
        if (text.Length > limit)
        {
            text = text[..limit].TrimEnd() + "...";
        }

        var entry = new PlanRunEvent(DateTimeOffset.UtcNow, stepId, attempt, kind, tier, node, text);
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
    public PlanRecord? Approve(string id, bool exportToProject = false)
    {
        bool changed = false;
        PlanRecord? approved = Update(id, plan =>
        {
            if (plan.Status is not (PlanStatus.AwaitingApproval or PlanStatus.Blocked))
            {
                return plan;
            }

            changed = true;
            PlanRecord approved = plan with { Status = PlanStatus.Approved, ApprovedUtc = DateTimeOffset.UtcNow };
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
            Approved?.Invoke(approved.Id);
        }

        return approved;
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
                StepStatus.Failed => "[!]",
                StepStatus.Running => "[~]",
                _ => "[ ]"
            };
            text.AppendLine($"- {box} **{step.Id}. {step.Title}** ({step.Tier})");
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

            if (step.Verify is not null)
            {
                text.AppendLine($"  - Verify: `{step.Verify}`");
            }

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

        int done = plan.Steps.Count(step => step.Status == StepStatus.Done);
        text.AppendLine($"{done} of {plan.Steps.Count} steps done, {plan.Steps.Sum(step => step.Attempts)} attempt(s) in all.");

        PlanStep? blocked = plan.Steps.FirstOrDefault(step => step.Status == StepStatus.Failed);
        if (plan.Status == PlanStatus.Blocked && blocked is not null)
        {
            PlanRunEvent? lastCheck = events.LastOrDefault(e => e.StepId == blocked.Id && e.Kind == RunEventKind.CheckFailed);
            text.AppendLine();
            text.AppendLine($"## Where it stopped");
            text.AppendLine($"Step {blocked.Id}, {blocked.Title}: {blocked.Note}");
            if (lastCheck is not null)
            {
                text.AppendLine();
                text.AppendLine("Last failing check output:");
                text.AppendLine("```");
                text.AppendLine(lastCheck.Detail);
                text.AppendLine("```");
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

        text.AppendLine();
        text.AppendLine("## Steps");
        foreach (PlanStep step in plan.Steps)
        {
            string box = step.Status switch { StepStatus.Done => "[x]", StepStatus.Failed => "[!]", StepStatus.Running => "[~]", _ => "[ ]" };
            string took = step.StartedUtc is not null && step.CompletedUtc is not null
                ? $", {Duration(step.CompletedUtc.Value - step.StartedUtc.Value)}"
                : string.Empty;
            text.AppendLine($"- {box} {step.Id}. {step.Title} ({step.Tier}, {step.Attempts} attempt(s){took})");
            if (!string.IsNullOrWhiteSpace(step.Note))
            {
                text.AppendLine($"  - {step.Note.ReplaceLineEndings(" ")}");
            }

            IEnumerable<string> nodes = events.Where(e => e.StepId == step.Id && e.Node is not null).Select(e => e.Node!).Distinct();
            if (nodes.Any())
            {
                text.AppendLine($"  - Answered by: {string.Join(", ", nodes)}");
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
            string where = e.StepId is null ? string.Empty : $" step {e.StepId}{(e.Attempt is null ? string.Empty : $" attempt {e.Attempt}")}";
            string via = string.Join(", ", new[] { e.Tier, e.Node }.Where(part => !string.IsNullOrEmpty(part)));
            text.AppendLine($"- {Local(e.AtUtc)}{where}: {e.Kind.Replace('-', ' ')}{(via.Length > 0 ? $" ({via})" : string.Empty)}");
            if (e.Kind == RunEventKind.FilesChanged)
            {
                text.AppendLine("  > listed above");
            }
            else if (!string.IsNullOrWhiteSpace(e.Detail))
            {
                text.AppendLine($"  > {e.Detail.ReplaceLineEndings(" ")}");
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
            CleanParallelGroup(input.ParallelGroup));
    }

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

    private string PathFor(string id) => Path.Combine(_directory, id + ".json");

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex PlanIdPattern();

    [GeneratedRegex(@"\[plan:(?<id>[0-9a-f]{32})\]")]
    private static partial Regex MarkerPattern();
}

internal sealed class PlanExportException(string message) : Exception(message);
