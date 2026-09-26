using System.ComponentModel;

namespace AgentFleet;

internal static class PlanStatus
{
    public const string AwaitingApproval = "awaiting-approval";
    public const string Approved = "approved";
    public const string Running = "running";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public const string Rejected = "rejected";
}

internal static class StepStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

internal sealed record PlanStep(
    int Id,
    string Title,
    string Detail,
    IReadOnlyList<string> Files,
    string? Verify,
    string Tier,
    string Status,
    string? Note,
    int Attempts,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? ParallelGroup = null);

internal static class RunEventKind
{
    public const string RunStarted = "run-started";
    public const string Resumed = "resumed";
    public const string AttemptStarted = "attempt-started";
    public const string AttemptEnded = "attempt-ended";
    public const string CheckPassed = "check-passed";
    public const string CheckFailed = "check-failed";
    public const string ModelFailed = "model-failed";
    public const string TimedOut = "attempt-timed-out";
    public const string StepDone = "step-done";
    public const string PlanDone = "plan-done";
    public const string PlanBlocked = "plan-blocked";
    public const string Stopped = "stopped";
    public const string FilesChanged = "files-changed";
    public const string ParallelStarted = "parallel-started";
    public const string ParallelFallback = "parallel-fallback";
    public const string Waiting = "waiting";
    public const string WorkRestored = "work-restored";
}

/// <summary>
/// One line of the run log: what the plan runner did and when. The log is what answers "what
/// happened while I was asleep" without reading the backend's log file.
/// </summary>
/// <param name="Node">The machine that answered, when known.</param>
/// <param name="Detail">Short text: what the model said it did, or the failing check's output.</param>
internal sealed record PlanRunEvent(
    DateTimeOffset AtUtc,
    int? StepId,
    int? Attempt,
    string Kind,
    string? Tier,
    string? Node,
    string Detail);

/// <summary>
/// A plan is a durable artifact, not chat text: it is saved as a file, shown to the user
/// for approval, and then worked through step by step with each step's own verify command
/// deciding whether it counts as done.
/// </summary>
internal sealed record PlanRecord(
    string Id,
    string Title,
    string Goal,
    string Status,
    string? WorkingDirectory,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> Risks,
    string? Diagram,
    string? DiagramNote,
    IReadOnlyList<PlanStep> Steps,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? ApprovedUtc,
    IReadOnlyList<PlanRunEvent>? Events = null,
    string? ExportedPlanPath = null,
    string? ContextId = null);

internal sealed record PlanSummary(
    string Id,
    string Title,
    string Status,
    int StepsDone,
    int StepsTotal,
    DateTimeOffset UpdatedUtc);

/// <summary>What the model supplies for one step when it proposes a plan.</summary>
internal sealed record PlanStepInput(
    [property: Description("Short imperative title, for example: Add the rate limiter class")] string Title,
    [property: Description("What to do and why, specific enough that a smaller model could carry it out without the rest of the conversation")] string Detail,
    [property: Description("Files this step reads or changes")] string[]? Files = null,
    [property: Description("A shell command that succeeds only if this step actually worked, for example: dotnet build, or npm test. Leave empty only if nothing can be checked automatically")] string? Verify = null,
    [property: Description("How capable a model the step needs: heavy for hard or risky work, standard for ordinary work, light for trivial edits")] string? Tier = null,
    [property: Description("Optional shared short label for two or more consecutive, independent steps that can run at the same time. Leave empty unless their files are disjoint and neither depends on the other")] string? ParallelGroup = null);
