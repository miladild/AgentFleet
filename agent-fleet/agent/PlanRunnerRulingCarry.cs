namespace AgentFleet;

/// <summary>
/// A ruling that the code is wrong is given once per step, and the worker is told it in every later round. The runner's own retry
/// of a step starts a new repair ladder, but not a new opinion: the ruling is kept for it. A retry or approval by the user is a
/// fresh start and drops it. The ruling is read back from the run log, where its note is stored after a marker.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>Written in front of the note the worker was given, in the detail of a code-wrong ruling, so a retried attempt can be given it again.</summary>
    internal const string RulingNoteMarker = "\nNote to the worker: ";

    /// <summary>
    /// The note of the step's latest code-wrong ruling since the step last had a fresh start, or null. Null as well when the ruling
    /// has been dropped from the bounded run log: the ruler is then asked again.
    /// </summary>
    internal string? CarriedRulingNote(string planId, int stepId)
    {
        PlanRunEvent[] events = (_store.Get(planId)?.Events ?? []).ToArray();
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

            if (runEvent.Kind == RunEventKind.TestRuling && string.Equals(runEvent.FailureClass, CodeWrongClass, StringComparison.Ordinal))
            {
                int at = runEvent.Detail.IndexOf(RulingNoteMarker, StringComparison.Ordinal);
                return at < 0 ? null : runEvent.Detail[(at + RulingNoteMarker.Length)..].Trim();
            }
        }

        return null;
    }
}
