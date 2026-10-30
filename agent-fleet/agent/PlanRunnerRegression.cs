using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// A step's own check runs only the tests of the files it touched, so a step can pass while it breaks an older test that a later
/// step would have caught at the end of the plan. When a step's check passes, the plan's whole-project check (the last step's
/// Verify) runs in the same workspace, before the step's changes are synced back. If it fails, the worker gets one more round in
/// the same conversation, once per attempt, so the break is fixed where it happened.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>The run-log kind of the one extra round a passing step is given when the whole-project check fails.</summary>
    private const string FinalCheckRoundKind = "final-check-round";

    /// <summary>How many failing test names the run log names.</summary>
    private const int FinalCheckNamesShown = 5;

    /// <summary>A failing whole-project check: the command, its trimmed output and the failing item names the output printed.</summary>
    private sealed record FinalCheckFailure(string Command, string Output, IReadOnlyList<string> FailingItems);

    // A path-like token with a source or test extension, as a check prints it (for example "test/old.test.ts:12:3").
    [GeneratedRegex(@"(?<![\w./\\-])[\w./\\-]+\.(?:cjs|cs|cts|c|cpp|go|h|java|jsx|js|kt|mjs|mts|php|py|rb|rs|tsx|ts)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex SourcePathToken();

    /// <summary>
    /// Runs the plan's whole-project check after a step's own check passed and before its changes are synced back. Returns the
    /// failure, or null when the check passed or did not run. It does not run when the rule does not apply: the tests step of
    /// test-first mode, the last step itself, a plan of one step, a last step without a usable Verify, a last step whose Verify
    /// is this step's, and a plan without a project folder. A failure that names a file a later step lists is expected until that
    /// step is written, so it is not a failure here. A command that cannot run (a timeout, a worker that cannot be reached) is
    /// logged and treated as a pass: the check must never fail a step by itself.
    /// </summary>
    private async Task<FinalCheckFailure?> RunFinalCheckAsync(PlanRecord plan, PlanStep step, bool isFinalValidation, CancellationToken cancellationToken)
    {
        if (!FinalCheckRuleApplies(plan, step, isFinalValidation))
        {
            return null;
        }

        string command = plan.Steps.MaxBy(candidate => candidate.Id)!.Verify!;
        try
        {
            (bool passed, string output) = await _tools.RunRepairCommandAsync(plan, command, cancellationToken);
            if (passed || NamesLaterStepFile(plan, step, output))
            {
                return null;
            }

            return new FinalCheckFailure(command, output, PlanFailure.FailureSignature(output).Items);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the whole-project check could not run, so it does not hold the step.", plan.Id, step.Id);
            return null;
        }
    }

    private bool FinalCheckRuleApplies(PlanRecord plan, PlanStep step, bool isFinalValidation)
    {
        if (IsTestsStep(step) || isFinalValidation || plan.Steps.Count < 2 || string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return false;
        }

        PlanStep last = plan.Steps.MaxBy(candidate => candidate.Id)!;
        return !IsTestsStep(last) && !string.IsNullOrWhiteSpace(last.Verify) && last.Verify != step.Verify &&
            PlanRunnerToolPolicy.CommandRefusal(last.Verify) is null;
    }

    /// <summary>
    /// Whether the failure output names a source or test file that a step after this one lists in its Files. The names are
    /// compared with the declared paths the same way the runner compares any name with a declared path.
    /// </summary>
    private static bool NamesLaterStepFile(PlanRecord plan, PlanStep step, string output)
    {
        string[] later = plan.Steps.Where(candidate => candidate.Id > step.Id)
            .SelectMany(candidate => FleetPlanContext.DeclaredPaths(plan, candidate))
            .ToArray();
        if (later.Length == 0)
        {
            return false;
        }

        foreach (Match token in SourcePathToken().Matches(output))
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(plan.WorkingDirectory!, token.Value));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (later.Any(declared => FleetPlanContext.Names(declared, full)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the extra round for a failing whole-project check already ran in this attempt.</summary>
    private bool FinalCheckRoundGiven(PlanRecord plan, PlanStep step, int attempt) =>
        (_store.Get(plan.Id)?.Events ?? []).Any(runEvent =>
            runEvent.Kind == FinalCheckRoundKind && runEvent.StepId == step.Id && runEvent.Attempt == attempt);

    /// <summary>
    /// The one extra round a passing step gets when the whole-project check failed. It applies only when the step would otherwise be
    /// accepted: a step another rule already sent back in this round is not given this message, and the rule applies on its next
    /// passing check. Returns the completion with the round's message, or the completion unchanged.
    /// </summary>
    private StepCompletion WithFinalCheckRound(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model,
        int rung, int round, StepCompletion result, FinalCheckFailure failure)
    {
        if (!result.Done || FinalCheckRoundGiven(plan, step, attempt))
        {
            return result;
        }

        string named = failure.FailingItems.Count == 0
            ? "no failing test name was printed"
            : $"first failing: {string.Join(", ", failure.FailingItems.Take(FinalCheckNamesShown))}";
        _store.AddEvent(plan.Id, step.Id, attempt, FinalCheckRoundKind, tier, node: model.ModelNode ?? model.Machine,
            detail: $"`{failure.Command}` fails on the whole project after this step's check passed ({named}). " +
                    "The worker gets one more round before the step is accepted.",
            modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
        string note = FinalCheckRoundMessage(failure);
        return new StepCompletion(false, note, note, result.Restored);
    }

    private static string FinalCheckRoundMessage(FinalCheckFailure failure) =>
        $"The check for this step passed, but the project's final check (`{failure.Command}`) fails now. It runs the whole project, " +
        "so a change in this step probably broke something that already worked. Fix what this step broke; do not change the tests " +
        "that were passing before unless the step's text says to. Output:\n" + failure.Output;
}
