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

    /// <summary>
    /// The step is stuck (its repair ladder is spent, its check is broken beyond repair, the machine cannot run it) and the
    /// run goes on without it: only the steps that depend on it wait. Failed is the harder stop that needs a person to look
    /// at the project first (the run deadline, a backend restart in the middle of a step that is not safe to repeat).
    /// </summary>
    public const string Parked = "parked";

    /// <summary>Stuck, one way or the other: waiting for the user to retry the step or skip it.</summary>
    public static bool IsStopped(string status) => status is Failed or Parked;
}

internal static class PlanRecoveryScope
{
    public const string WorkerOnly = "worker-only";
    public const string AllowHubRescue = "allow-hub-rescue";

    public static bool AllowsHubRescue(string? scope) =>
        string.Equals(scope, AllowHubRescue, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What the runner does when a step's check is broken (it never finishes, does not parse in the worker's shell, names a
/// program that is not there) rather than failing on its merits: fix it by itself, or park the step with the proposal.
/// </summary>
internal static class PlanHealChecks
{
    public const string Auto = "auto";
    public const string Ask = "ask";

    // A plan saved before the setting existed heals by itself, like a new one: nobody is there to ask at night.
    public static bool IsAuto(string? mode) => !string.Equals(mode, Ask, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// How many times the runner gives a parked step a fresh repair ladder by itself before it is left for the user. A plan left
/// to run unattended is not stopped by a step that a second pass, from the best state the first one left, can finish.
/// </summary>
internal static class PlanAutoRetry
{
    public const int Default = 2;
    public const int Max = 5;

    public static int For(PlanRecord plan) => Math.Clamp(plan.AutoRetries ?? Default, 0, Max);

    // The causes a pass from the best state may fix: the ladder was spent, nothing changed, the step ran out of time. An
    // environment, a broken check or an unknown failure is not made better by trying again.
    public static bool Retryable(string cause) => cause is "check-kept-failing" or "no-change" or "working-time";
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
    string? ParallelGroup = null,
    string? Machine = null,
    bool RetrySafe = false,
    string? OriginalVerify = null,
    IReadOnlyList<int>? DependsOn = null);

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
    public const string StepSkipped = "step-skipped";
    public const string MachineSelected = "machine-selected";
    public const string WorkspaceFailed = "workspace-failed";
    public const string WorkspaceStaged = "workspace-staged";
    public const string WorkspaceSynced = "workspace-synced";
    public const string RecoveryRouted = "recovery-routed";
    public const string FinalValidationStarted = "final-validation-started";
    public const string FinalValidationPassed = "final-validation-passed";
    public const string FinalValidationFailed = "final-validation-failed";
    public const string RunLeaseBusy = "run-lease-busy";
    public const string RunDeadlineExceeded = "run-deadline-exceeded";
    public const string RetryApproved = "retry-approved";
    public const string RoundClassified = "round-classified";
    public const string InferenceProbePassed = "inference-probe-passed";
    public const string WorkspaceReconciled = "workspace-reconciled";
    public const string RungChanged = "rung-changed";
    public const string RoundRolledBack = "round-rolled-back";
    public const string StepTimeLimit = "step-time-limit";
    public const string CheckHealed = "check-healed";
    public const string CheckAudit = "check-audit";
    public const string EnvironmentRepaired = "environment-repaired";
    public const string BlockerReported = "blocker-reported";
    public const string StepParked = "step-parked";
    public const string StepRetried = "step-retried";
    public const string PlanNeedsAttention = "plan-needs-attention";
}

/// <summary>
/// One line of the run log: what the plan runner did and when. The log is what answers "what
/// happened while I was asleep" without reading the backend's log file.
/// </summary>
/// <param name="Node">The machine that answered, when known.</param>
/// <param name="Detail">Short text: what the model said it did, or the failing check's output.</param>
/// <param name="Rung">The repair ladder's rung the step was on (1 requested tier, 2 hub model, 3 fresh conversation).</param>
/// <param name="Round">
/// The round within that rung: the model works, then the runner runs the approved check. On a round-classified event it
/// is set only when the check ran, so a restart can count the rounds that really happened.
/// </param>
/// <param name="ChangedFiles">The project files the round changed (the first few), where git could tell.</param>
/// <param name="DurationSeconds">On a round-classified event: the working time of the round, its model call and its check.</param>
/// <param name="CheckBefore">On a check-healed event: the check as it was.</param>
/// <param name="CheckAfter">On a check-healed event: the check the step has from now on.</param>
internal sealed record PlanRunEvent(
    DateTimeOffset AtUtc,
    int? StepId,
    int? Attempt,
    string Kind,
    string? Tier,
    string? Node,
    string Detail,
    string? ModelNode = null,
    string? WorkspaceNode = null,
    string? FailureClass = null,
    string? FailureSignature = null,
    int? FailureSignatureSize = null,
    int? FilesChanged = null,
    int? ToolCalls = null,
    bool? EditToolCalled = null,
    int? Rung = null,
    int? Round = null,
    IReadOnlyList<string>? ChangedFiles = null,
    int? DurationSeconds = null,
    string? CheckBefore = null,
    string? CheckAfter = null);

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
    string? ContextId = null,
    string? RecoveryScope = null,
    DateTimeOffset? RunDeadlineUtc = null,
    string? HealChecks = null,
    int? AutoRetries = null);

internal sealed record PlanSummary(
    string Id,
    string Title,
    string Status,
    int StepsDone,
    int StepsTotal,
    DateTimeOffset UpdatedUtc,
    string? ContextId = null,
    int StepsParked = 0);

/// <summary>What the model supplies for one step when it proposes a plan.</summary>
internal sealed record PlanStepInput(
    [property: Description("Short imperative title, for example: Add the rate limiter class")] string Title,
    [property: Description("What to do and why, specific enough that a smaller model could carry it out without the rest of the conversation")] string Detail,
    [property: Description("Files this step reads or changes")] string[]? Files = null,
    [property: Description("A shell command that succeeds only if this step actually worked, for example: dotnet build, or npm test. Leave empty only if nothing can be checked automatically")] string? Verify = null,
    [property: Description("How capable a model the step needs: heavy for hard or risky work, standard for ordinary work, light for trivial edits")] string? Tier = null,
    [property: Description("Optional shared short label for two or more consecutive, independent steps that can run at the same time. Leave empty unless their files are disjoint and neither depends on the other")] string? ParallelGroup = null,
    [property: Description("Set true only when this step's project-local edits and commands are safe to repeat if the backend restarts mid-step. Otherwise false; interrupted steps then wait for user review")] bool RetrySafe = false,
    [property: Description("Numbers of the earlier steps this one needs finished first. Leave empty for the usual chain (each step needs the one before it); the last step always needs all of them. A step that cannot be finished holds back only the steps that need it")] int[]? DependsOn = null);

internal sealed record StepMachineRequest(int StepId, string? Machine);

internal sealed record PlanApprovalRequest(
    bool ExportToProject = false,
    string? RecoveryScope = null,
    string? HealChecks = null,
    int? AutoRetries = null);
