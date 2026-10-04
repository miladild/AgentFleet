using System.Text;

namespace AgentFleet;

/// <summary>
/// Parking: a step that cannot be finished (its repair ladder is spent, its check is broken beyond repair, the machine
/// cannot run it, it keeps failing in a way nobody understands) stops being worked on, and only the steps that depend on it
/// wait. The plan stays running while anything else can progress and is blocked only when nothing can. Waiting for a
/// machine to come back is not parking (that is the outage logic of the repair loop).
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>
    /// Parks the step: it keeps its reason, the plan goes on, and the user is told once. Unless the plan's automatic retries
    /// (see TryRetryAutomatically) give it another go first, the step is then the user's to retry or skip. Cause is a few
    /// fixed words for the notice, never output.
    /// </summary>
    private void ParkStep(PlanRecord plan, PlanStep step, string note, string cause, string? reason = null)
    {
        reason ??= note;
        if (TryRetryAutomatically(plan, step, cause, reason))
        {
            return;
        }

        PlanRecord? parked = _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id, s => s with
        {
            Status = StepStatus.Parked,
            Note = note
        }) with { Status = PlanStatus.Running });
        if (_recorder is not null && ContextFor(plan.Id) is { } contextId && parked is not null)
        {
            _recorder.StepBlocked(contextId, parked, step, reason);
        }

        _store.AddEvent(plan.Id, step.Id, null, RunEventKind.StepParked, step.Tier, detail: reason,
            workspaceNode: step.Machine, failureClass: "Parked", failureSignature: cause);
        _logger.LogWarning("Plan {PlanId} step {StepId} ({Title}) is parked: {Cause}.", plan.Id, step.Id, step.Title, cause);
        if (parked is not null)
        {
            Notify(parked, "step-parked", step, cause);
        }
    }

    /// <summary>
    /// A plan left to run unattended should not wait for a person over a step that a second pass can finish. Instead of
    /// parking, the step gets a fresh repair ladder (the user's own Retry, done by the runner), starting from the project as
    /// the best round left it and with what went wrong in front of the model. It is bounded: the plan's budget of automatic
    /// retries for this step (a retry by the user starts it afresh), the causes a second pass can mend, and the run deadline.
    /// Nobody is told: the step is not stopped.
    /// </summary>
    private bool TryRetryAutomatically(PlanRecord plan, PlanStep step, string cause, string reason)
    {
        if (!PlanAutoRetry.Retryable(cause))
        {
            return false;
        }

        PlanRecord latest = _store.Get(plan.Id) ?? plan;
        int budget = PlanAutoRetry.For(latest, step);
        int used = AutomaticRetriesUsed(latest, step.Id);
        if (used >= budget || (latest.RunDeadlineUtc is { } deadline && _utcNow() >= deadline))
        {
            return false;
        }

        string detail = $"{reason} Trying it again automatically (retry {used + 1} of {budget}) with a fresh repair ladder, from the project as the best round left it.";
        _store.RetryStepAutomatically(plan.Id, step.Id, detail);
        _logger.LogWarning("Plan {PlanId} step {StepId} ({Title}) is retried automatically ({Used} of {Budget}): {Cause}.",
            plan.Id, step.Id, step.Title, used + 1, budget, cause);
        return true;
    }

    // The automatic retries a step has had since the user last gave it a fresh start (their own retry, or approving the plan again).
    private static int AutomaticRetriesUsed(PlanRecord plan, int stepId)
    {
        int used = 0;
        foreach (PlanRunEvent runEvent in (plan.Events ?? []).Reverse())
        {
            if (runEvent.Kind == RunEventKind.RetryApproved && (runEvent.StepId is null || runEvent.StepId == stepId))
            {
                break;
            }

            if (runEvent.Kind == RunEventKind.StepRetried && runEvent.StepId == stepId)
            {
                if (!string.Equals(runEvent.FailureClass, FleetPlanStore.AutoRetryClass, StringComparison.Ordinal))
                {
                    break;
                }

                used++;
            }
        }

        return used;
    }

    /// <summary>
    /// Nothing more can run and some steps did not finish: the plan is blocked, with the first parked step's reason, the
    /// steps that were not run because they wait for it, and one notice. The steps that did finish stay done.
    /// </summary>
    private void EndWithStoppedSteps(PlanRecord plan)
    {
        PlanStep[] stopped = plan.Steps.Where(step => StepStatus.IsStopped(step.Status)).ToArray();
        PlanStep[] waiting = plan.Steps.Where(step => step.Status == StepStatus.Pending).ToArray();
        PlanStep? first = stopped.FirstOrDefault();
        int done = plan.Steps.Count(step => step.Status == StepStatus.Done);

        var detail = new StringBuilder();
        if (first is null)
        {
            detail.Append("Nothing more can run.");
        }
        else
        {
            PlanRunEvent? parkedEvent = (plan.Events ?? []).LastOrDefault(runEvent => runEvent.Kind == RunEventKind.StepParked && runEvent.StepId == first.Id);
            detail.Append(parkedEvent?.Detail ?? first.Note ?? $"Step {first.Id} ({first.Title}) did not pass its check.");
        }

        if (stopped.Length > 1)
        {
            detail.Append($"\n\nAlso stopped: {Numbered(stopped.Skip(1))}.");
        }

        if (waiting.Length > 0)
        {
            detail.Append($"\n\nNot run, because they wait for a step that stopped: {Numbered(waiting)}.");
        }

        // The summary first: the blocked line stays the last event of the run, where the live view and the reports look for it.
        _store.AddEvent(plan.Id, null, null, RunEventKind.PlanNeedsAttention,
            detail: $"{done} of {plan.Steps.Count} steps are done. " +
                    (stopped.Length > 0 ? $"Stopped, waiting for you to retry or skip: {Numbered(stopped)}. " : string.Empty) +
                    (waiting.Length > 0 ? $"Not run, waiting on them: {Numbered(waiting)}." : string.Empty));
        _store.AddEvent(plan.Id, first?.Id, null, RunEventKind.PlanBlocked, detail: detail.ToString());
        _logger.LogWarning("Plan {PlanId} ended with {Stopped} stopped and {Waiting} waiting step(s).", plan.Id, stopped.Length, waiting.Length);
        Notify(plan, "plan-needs-attention", first, "stopped-steps");
    }

    private static string Numbered(IEnumerable<PlanStep> steps) =>
        string.Join(", ", steps.Select(step => $"#{step.Id} ({step.Title})"));

    // A told-once moment of the run. The notifier never blocks and never throws into the run.
    private void Notify(PlanRecord plan, string kind, PlanStep? step = null, string? cause = null)
    {
        if (_notifier is null)
        {
            return;
        }

        try
        {
            PlanRecord latest = _store.Get(plan.Id) ?? plan;
            _notifier.Notify(new PlanNotice(kind, plan.Id, plan.Title,
                latest.Steps.Count(candidate => candidate.Status == StepStatus.Done),
                latest.Steps.Count(candidate => StepStatus.IsStopped(candidate.Status)),
                latest.Steps.Count, step?.Id, step?.Title, cause, _utcNow()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "A plan notice could not be handed over.");
        }
    }

    /// <summary>
    /// The events that count for a step since it was last retried: the user's approval of the plan again (every step starts
    /// afresh) or a retry of this step alone (see FleetPlanStore.RetryStep). A retry of another step leaves it alone.
    /// </summary>
    private static int RetryBoundary(PlanRunEvent[] events, int stepId) =>
        Array.FindLastIndex(events, runEvent =>
            (runEvent.Kind == RunEventKind.RetryApproved && (runEvent.StepId is null || runEvent.StepId == stepId)) ||
            (runEvent.Kind == RunEventKind.StepRetried && runEvent.StepId == stepId));
}
