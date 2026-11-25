namespace AgentFleet;

/// <summary>A code-wrong ruling read back from the run log: the note the worker is given, and the failure line it was made for.</summary>
internal sealed record CarriedRuling(string Note, string? FailureLine);

/// <summary>
/// A ruling that the code is wrong is given once per step, and the worker is told it in every later round. The runner's own retry
/// of a step starts a new repair ladder, but not a new opinion: the ruling is kept for it. A retry or approval by the user is a
/// fresh start and drops it. So does a check that fails with another error than the one the ruling was made for: the note then
/// describes something that is no longer there (measured, 13 rounds after the failure had changed). The ruling is read back from
/// the run log, where its failure line and note are stored after markers.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>Written in front of the note the worker was given, in the detail of a code-wrong ruling, so a retried attempt can be given it again.</summary>
    internal const string RulingNoteMarker = "\nNote to the worker: ";

    /// <summary>
    /// Written in front of the note marker: the failure the ruling was made for (see RulingFailure). It sits before the note so that
    /// a long note cut by the event's size limit does not take it away. A ruling written without it is carried whatever the check prints.
    /// </summary>
    internal const string RulingFailureMarker = "\nFailure when ruled: ";

    /// <summary>The failure class of the run-log event that ends a ruling because the check now fails differently.</summary>
    internal const string RulingDroppedClass = "Dropped";

    /// <summary>
    /// The note of the step's latest code-wrong ruling since the step last had a fresh start, or null. Null as well when the ruling
    /// has been dropped from the bounded run log (the ruler is then asked again), when it was dropped because the check failed
    /// differently, and when the step's newest failed check since the ruling fails with another error line than the ruling was made for.
    /// </summary>
    internal string? CarriedRulingNote(string planId, int stepId) => CarriedRulingOf(planId, stepId)?.Note;

    /// <summary>The same ruling with the failure line it was made for, or null under the same conditions as <see cref="CarriedRulingNote"/>.</summary>
    internal CarriedRuling? CarriedRulingOf(string planId, int stepId)
    {
        PlanRunEvent[] events = (_store.Get(planId)?.Events ?? []).ToArray();
        // The newest failed check of this step that came after the ruling: scanning back from the end meets it before the ruling.
        string? newestFailure = null;
        for (int index = events.Length - 1; index >= 0; index--)
        {
            PlanRunEvent runEvent = events[index];
            if (runEvent.Kind == RunEventKind.RetryApproved && (runEvent.StepId is null || runEvent.StepId == stepId))
            {
                return null;
            }

            if (runEvent.StepId != stepId)
            {
                continue;
            }

            if (runEvent.Kind == RunEventKind.StepRetried && !string.Equals(runEvent.FailureClass, FleetPlanStore.AutoRetryClass, StringComparison.Ordinal))
            {
                return null;
            }

            if (newestFailure is null && runEvent.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed)
            {
                newestFailure = runEvent.Detail;
            }

            if (runEvent.Kind == RunEventKind.TestRuling && string.Equals(runEvent.FailureClass, RulingDroppedClass, StringComparison.Ordinal))
            {
                return null;
            }

            if (runEvent.Kind == RunEventKind.TestRuling && string.Equals(runEvent.FailureClass, CodeWrongClass, StringComparison.Ordinal))
            {
                CarriedRuling? ruling = ReadRuling(runEvent.Detail);
                return ruling is not null && RulingFailure.StillApplies(ruling.FailureLine, newestFailure) ? ruling : null;
            }
        }

        return null;
    }

    private static CarriedRuling? ReadRuling(string detail)
    {
        int at = detail.IndexOf(RulingNoteMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        string note = detail[(at + RulingNoteMarker.Length)..].Trim();
        string head = detail[..at];
        int failureAt = head.LastIndexOf(RulingFailureMarker, StringComparison.Ordinal);
        string? failureLine = failureAt < 0 ? null : head[(failureAt + RulingFailureMarker.Length)..].Trim();
        return new CarriedRuling(note, string.IsNullOrEmpty(failureLine) ? null : failureLine);
    }

    /// <summary>The marker and failure line a new code-wrong ruling stores, or nothing when the check prints no readable error line.</summary>
    private static string RulingFailureText(string? checkOutput) =>
        RulingFailure.Line(checkOutput) is { } line ? RulingFailureMarker + line : string.Empty;

    private void RecordRulingDropped(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model, int rung, int round,
        string? madeFor, string? now) =>
        RecordRuling(plan, step, attempt, tier, model, rung, round, RulingDroppedClass,
            $"The check now fails with another error ({now ?? "no readable error line"}; the ruling was made for {madeFor}), " +
            "so the earlier ruling that the code was wrong is no longer given to the worker.");
}
