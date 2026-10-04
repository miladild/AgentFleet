using System.Text;

namespace AgentFleet;

/// <summary>
/// Self-healing of what is not the work: a check that cannot run or finish (healed by an audit and a guard, see
/// CheckGuard) and a machine that lacks the project's own dependencies (repaired by the environment doctor). A check that
/// ran and failed on its merits never comes through here.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>The most times the fleet changes one step's check before it stops and asks.</summary>
    public const int MaxHealsPerStep = 2;

    // Audits that changed nothing (the check was found sound, or no proposal was usable): after two, a sign is ignored for the
    // rest of the step, so a slow test suite or a false alarm does not cost a call to the hub every round. A check that can
    // never finish is not ignored: it cannot pass whatever else is true.
    private const int MaxIdleAuditsPerStep = 2;

    // The proposal and, when the guard refuses it, one more with the reason.
    private const int MaxProposalsPerAudit = 2;

    internal static readonly TimeSpan AuditTimeLimit = TimeSpan.FromMinutes(10);

    private enum HealKind { Healed, Park, Continue }

    private sealed record HealOutcome(HealKind Kind, string? Reason = null);

    private sealed record CheckRun(StepCompletion Result, PlanStep Step, string? ParkReason, string? Note);

    /// <summary>
    /// Runs the step's approved check, and when it fails for a reason that is not the work, repairs that and runs it again
    /// in the same workspace, so the repair costs no round: the environment doctor installs what the project declares
    /// but the machine lacks, and a broken check is audited and replaced when the guard allows. Returns the last run, and
    /// a reason when the step has to stop for the user.
    /// </summary>
    private async Task<CheckRun> RunCheckWithRepairsAsync(
        PlanRecord plan, PlanStep step, int attempt, string tier, int rung, int round, ModelAttemptResult model, CancellationToken cancellationToken)
    {
        string? note = null;
        bool blockerNoted = false;
        StepCompletion result = new(false, string.Empty);
        for (int pass = 0; pass < 5; pass++)
        {
            result = await _tools.TryCompleteStepAsync(
                plan.Id, step.Id, Summarize(model.Summary), cancellationToken, FilesToCreate(plan, step), deferCommit: true);
            if (result.Done)
            {
                return new CheckRun(result, step, null, note);
            }

            string output = result.Output ?? result.Message;

            // A missing runtime, a policy refusal or an unavailable worker have their own handling after the check.
            if (EnvironmentBlockReason(output) is not null)
            {
                return new CheckRun(result, step, null, note);
            }

            plan = _store.Get(plan.Id) ?? plan;
            step = plan.Steps.FirstOrDefault(candidate => candidate.Id == step.Id) ?? step;

            (bool repaired, string? doctorNote) = await TryRepairEnvironmentAsync(plan, step, attempt, tier, rung, round, model, output, cancellationToken);
            if (doctorNote is not null)
            {
                note = doctorNote;
            }

            if (repaired)
            {
                continue;
            }

            // What the model reported counts only as far as the check's own output says the same.
            bool matches = model.Blocker is { } blocker && CheckDefects.EvidenceMatches(blocker.Evidence, output);
            if (model.Blocker is { } reported && !blockerNoted)
            {
                blockerNoted = true;
                _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.BlockerReported, tier, node: model.ModelNode,
                    detail: $"The model reported {reported.Kind}: {Summarize(reported.Evidence)}. " +
                            (matches ? "The check's own output says the same, so it counts as a hint." : "The check's own output does not say that, so it was not acted on."),
                    modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
            }

            CheckDefect? defect = string.IsNullOrWhiteSpace(step.Verify) ? null : FindDefect(plan, step, output, model.Blocker, matches);
            if (defect is null)
            {
                return new CheckRun(result, step, null, note);
            }

            HealOutcome outcome = await HealCheckAsync(plan, step, attempt, tier, rung, round, model.Workspace?.Platform, model.ModelNode ?? model.Machine,
                defect, output, model.Blocker, cancellationToken);
            switch (outcome.Kind)
            {
                case HealKind.Healed:
                    plan = _store.Get(plan.Id) ?? plan;
                    step = plan.Steps.First(candidate => candidate.Id == step.Id);
                    continue;
                case HealKind.Park:
                    return new CheckRun(result, step, outcome.Reason, note);
                default:
                    return new CheckRun(result, step, null, note);
            }
        }

        return new CheckRun(result, step, null, note);
    }

    private CheckDefect? FindDefect(PlanRecord plan, PlanStep step, string output, ModelBlocker? blocker, bool blockerMatches)
    {
        if (CheckDefects.FromOutput(step.Verify!, output, plan.WorkingDirectory, plan.Steps) is { } fromOutput)
        {
            return fromOutput;
        }

        IEnumerable<string> earlier = StepEventsSinceRetry(plan.Id, step.Id)
            .Where(runEvent => runEvent.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed)
            .Select(runEvent => runEvent.Detail);
        if (CheckDefects.TimedOutTwice(output, earlier) is { } timedOut)
        {
            return timedOut;
        }

        return blocker is { Kind: "check_cannot_pass" } && blockerMatches
            ? new CheckDefect(CheckDefectKind.ReportedByModel, blocker.Evidence)
            : null;
    }

    /// <summary>
    /// The environment doctor's turn: a failure that is a project dependency the machine lacks gets the project's own
    /// install, once per cause and step, and the check runs again without costing a round. Returns whether to run the
    /// check again, and a note for the model and the log when the fleet could not repair it.
    /// </summary>
    private async Task<(bool Rerun, string? Note)> TryRepairEnvironmentAsync(
        PlanRecord plan, PlanStep step, int attempt, string tier, int rung, int round, ModelAttemptResult model, string output, CancellationToken cancellationToken)
    {
        string? root = plan.WorkingDirectory;
        EnvironmentRepair? repair = EnvironmentDoctor.Diagnose(output, root, root is null ? null : EnvironmentDoctor.VirtualEnvironmentPython(root));
        if (repair is null)
        {
            return (false, null);
        }

        if (StepEventsSinceRetry(plan.Id, step.Id).Any(runEvent => runEvent.Kind == RunEventKind.EnvironmentRepaired &&
                string.Equals(runEvent.FailureSignature, repair.Cause, StringComparison.Ordinal)))
        {
            return (false, null);
        }

        if (repair.Command is null)
        {
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.EnvironmentRepaired, tier, node: model.Machine,
                detail: $"The fleet does not repair this by itself. {repair.Advice}", workspaceNode: model.Machine,
                failureClass: "NotRepaired", failureSignature: repair.Cause, rung: rung, round: round);
            return (false, $"Fleet note: {repair.Advice}");
        }

        (bool succeeded, string result) = await _tools.RunRepairCommandAsync(plan, repair.Command, cancellationToken);
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.EnvironmentRepaired, tier, node: model.Machine,
            detail: $"{repair.Advice} `{repair.Command}` {(succeeded ? "succeeded" : "failed")}: {Summarize(result)}",
            workspaceNode: model.Machine, failureClass: succeeded ? "Repaired" : "RepairFailed", failureSignature: repair.Cause,
            rung: rung, round: round);
        return succeeded
            ? (true, null)
            : (false, $"Fleet note: `{repair.Command}` was run to install what the project declares and failed: {Summarize(result)}");
    }

    /// <summary>
    /// Audits a check that looks broken and replaces it when the guard allows: the auditor (the hub's model when the plan
    /// allows hub rescue, otherwise the model of the worker that did the work) reads the project and proposes a
    /// replacement; CheckGuard decides whether to apply it. Every outcome is a run event, and a check whose original text
    /// is kept in the step's OriginalVerify.
    /// </summary>
    private async Task<HealOutcome> HealCheckAsync(
        PlanRecord plan, PlanStep step, int? attempt, string tier, int? rung, int? round, string? workerPlatform, string? workMachine,
        CheckDefect defect, string? output, ModelBlocker? hint, CancellationToken cancellationToken)
    {
        string original = step.Verify!;
        PlanRunEvent[] events = StepEventsSinceRetry(plan.Id, step.Id);
        bool hard = defect.Kind is not (CheckDefectKind.TimedOut or CheckDefectKind.ReportedByModel);
        int heals = events.Count(runEvent => runEvent.Kind == RunEventKind.CheckHealed);

        void Note(string detail, string failureClass) =>
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.CheckAudit, tier, detail: detail, failureClass: failureClass, rung: rung, round: round);

        if (heals >= MaxHealsPerStep)
        {
            string limit = $"The check of step {step.Id} was already changed {heals} times by the fleet, so it is not changed again.";
            Note(limit, "Limit");
            return hard ? Park(plan, step, defect, limit) : new HealOutcome(HealKind.Continue);
        }

        int audits = events.Count(runEvent => runEvent.Kind == RunEventKind.CheckAudit && runEvent.FailureClass == "Started");
        if (defect.Kind != CheckDefectKind.NeverFinishes && audits - heals >= MaxIdleAuditsPerStep)
        {
            return new HealOutcome(HealKind.Continue);
        }

        Note($"Auditing the check: {defect.Name}. {defect.Evidence}", "Started");

        string? hub = _fleetOptions?.Nodes.SingleOrDefault(node => node.Fallback)?.Name;
        bool useHub = hub is not null && PlanRecoveryScope.AllowsHubRescue(plan, step);
        string auditTier = useHub ? FleetTiers.Heavy : step.Tier;
        string? auditMachine = useHub ? hub : workMachine ?? step.Machine;

        string message = CheckAudit.BuildPrompt(plan, step, original, defect, output, CheckDefects.ShellDescription(workerPlatform, original), hint);
        IStepSession session = _agent.OpenSession(PlanRunnerToolPolicy.CheckAuditRole);
        string? refused = null;
        for (int proposal = 1; proposal <= MaxProposalsPerAudit; proposal++)
        {
            StepAgentReply reply;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(AuditTimeLimit);
                reply = await session.SendAsync(message, auditTier, auditMachine, limit.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                string unavailable = exception is OperationCanceledException
                    ? $"The audit of the check did not answer within {Duration(AuditTimeLimit)}."
                    : $"The audit of the check could not run: {exception.Message}";
                _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the check audit failed.", plan.Id, step.Id);
                Note(unavailable, "Unavailable");
                return hard ? Park(plan, step, defect, unavailable) : new HealOutcome(HealKind.Continue);
            }

            if (CheckAudit.ProposalFrom(reply) is not { } proposed)
            {
                if (refused is not null)
                {
                    return hard ? Park(plan, step, defect, refused) : new HealOutcome(HealKind.Continue);
                }

                string none = $"The audit found no defect in the check: {Summarize(reply.Text)}";
                Note(none, "NoDefect");

                // A check that can never finish cannot pass, whatever the auditor thinks of it.
                return defect.Kind == CheckDefectKind.NeverFinishes
                    ? Park(plan, step, defect, "The audit proposed no replacement.")
                    : new HealOutcome(HealKind.Continue);
            }

            CheckVerdict verdict = CheckGuard.Validate(_store.Get(plan.Id) ?? plan, step, original, proposed.Check);
            if (!verdict.Accepted)
            {
                refused = $"The proposal `{proposed.Check}` was refused: {verdict.Reason}";
                Note(refused, "Refused");
                message = CheckAudit.RejectionMessage(proposed.Check, verdict.Reason);
                continue;
            }

            if (!PlanHealChecks.IsAuto(plan.HealChecks))
            {
                string ask = $"The fleet proposes changing the check from `{original}` to `{proposed.Check}` ({proposed.Why}). This plan is set to ask first, so the step waits.";
                Note(ask, "Asked");
                return Park(plan, step, defect, ask + " Approve the plan again with \"Broken checks: fix automatically\" to let the fleet apply it, change the check yourself, or skip the step.", asked: true);
            }

            _store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, step.Id, planStep => planStep with
            {
                Verify = proposed.Check,
                OriginalVerify = planStep.OriginalVerify ?? original
            }));
            _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.CheckHealed, tier,
                detail: $"The check was changed because {defect.Name}: {proposed.Why} ({verdict.Reason})",
                rung: rung, round: round, checkBefore: original, checkAfter: proposed.Check);
            _logger.LogInformation("Plan {PlanId} step {StepId}: check changed from `{Before}` to `{After}`.", plan.Id, step.Id, original, proposed.Check);
            return new HealOutcome(HealKind.Healed);
        }

        return hard ? Park(plan, step, defect, refused ?? "The audit proposed no usable replacement.") : new HealOutcome(HealKind.Continue);
    }

    private static HealOutcome Park(PlanRecord plan, PlanStep step, CheckDefect defect, string detail, bool asked = false)
    {
        // A step set to ask first is waiting for an answer, not stuck.
        string reason = asked
            ? $"Step {step.Id} ({step.Title}): {defect.Name}. Check: `{step.Verify}`. {defect.Evidence} {detail}"
            : $"Step {step.Id} ({step.Title}): {defect.Name}, and the fleet could not change it by itself. Check: `{step.Verify}`. {defect.Evidence} {detail} " +
              "Change the step's check to one that works, or skip the step, then approve the plan again.";
        return new HealOutcome(HealKind.Park, reason.Replace("  ", " "));
    }

    /// <summary>
    /// Before a step starts: a check that can never finish is healed, or the step is parked with the reason, instead of
    /// spending every attempt on a step that cannot pass. Replaces the older rule that always stopped such a plan.
    /// </summary>
    private async Task PreflightChecksAsync(string planId, IReadOnlyList<int> stepIds, CancellationToken cancellationToken)
    {
        foreach (int stepId in stepIds)
        {
            PlanRecord plan = _store.Get(planId)!;
            PlanStep? step = plan.Steps.FirstOrDefault(candidate => candidate.Id == stepId);
            if (step is null || step.Status != StepStatus.Pending || string.IsNullOrWhiteSpace(step.Verify) ||
                CheckDefects.FromCommand(step.Verify, plan.WorkingDirectory) is not { } defect)
            {
                continue;
            }

            HealOutcome outcome = await HealCheckAsync(plan, step, null, step.Tier, null, null, null, step.Machine, defect, null, null, cancellationToken);
            if (outcome.Kind != HealKind.Healed)
            {
                ParkForCheck(plan, step, outcome.Reason ?? Park(plan, step, defect, "No replacement was proposed.").Reason!);
            }
        }
    }

    private void ParkForCheck(PlanRecord plan, PlanStep step, string reason) => ParkStep(plan, step, reason, "check");
}
