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

    /// <summary>The run-log kind of a whole-project check moved from a worker that cannot run it to the hub's copy.</summary>
    private const string FinalCheckMovedKind = "final-check-moved";

    /// <summary>The run-log kind of a whole-project check that could not run anywhere, so it did not hold the step.</summary>
    private const string FinalCheckUnavailableKind = "final-check-unavailable";

    /// <summary>How many failing test names the run log names.</summary>
    private const int FinalCheckNamesShown = 5;

    /// <summary>How much of a check's output the moved and unavailable events keep.</summary>
    private const int FinalCheckDetailCharacters = 400;

    // The shell's word for a program it cannot find: 127 from sh and bash, 9009 from cmd, and cmd's own sentence.
    [GeneratedRegex(@"\AExit code: (?:127|9009)\b|is not recognized as an internal or external command")]
    private static partial Regex MissingCommand();

    /// <summary>Whether a command's output says its program could not be found, as opposed to a check that ran and failed.</summary>
    internal static bool CommandMissing(string output) => MissingCommand().IsMatch(output);

    /// <summary>
    /// A failing whole-project check: the command, its trimmed output, the failing item names the output printed, and the files this
    /// step lists that the output names (relative paths).
    /// </summary>
    private sealed record FinalCheckFailure(string Command, string Output, IReadOnlyList<string> FailingItems, IReadOnlyList<string> OwnFiles);

    // A path-like token with a source or test extension, as a check prints it (for example "test/old.test.ts:12:3").
    [GeneratedRegex(@"(?<![\w./\\-])[\w./\\-]+\.(?:cjs|cs|cts|c|cpp|go|h|java|jsx|js|kt|mjs|mts|php|py|rb|rs|tsx|ts)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex SourcePathToken();

    /// <summary>
    /// Runs the plan's whole-project check after a step's own check passed and before its changes are synced back. Returns the
    /// failure, or null when the check passed or did not run. It does not run when the rule does not apply: the tests step of
    /// test-first mode, the last step itself, a plan of one step, a last step without a usable Verify, a last step whose Verify
    /// is this step's, and a plan without a project folder. A failure that names a file a later step lists is expected until that
    /// step is written, so it is not a failure here, unless it also names a file this step lists: that one is this step's own. A command that cannot run (a timeout, a worker that cannot be reached) is
    /// logged and treated as a pass: the check must never fail a step by itself. A command whose program is missing on the worker
    /// (measured: "tsc: not found" on one worker, so a step that broke an old test was taken and the plan blocked at its last step)
    /// runs again on the hub's copy, which holds the step's changes; missing there too, it is logged and treated as a pass.
    /// </summary>
    private async Task<FinalCheckFailure?> RunFinalCheckAsync(PlanRecord plan, PlanStep step, bool isFinalValidation, int attempt, string tier,
        ModelAttemptResult model, int rung, int round, CancellationToken cancellationToken)
    {
        if (!FinalCheckRuleApplies(plan, step, isFinalValidation))
        {
            return null;
        }

        string command = plan.Steps.MaxBy(candidate => candidate.Id)!.Verify!;
        try
        {
            (bool passed, string output) = await _tools.RunRepairCommandAsync(plan, command, cancellationToken);
            if (!passed && CommandMissing(output) && WorkerWorkspaceContext.Current is { } worker)
            {
                _store.AddEvent(plan.Id, step.Id, attempt, FinalCheckMovedKind, tier, node: model.ModelNode ?? model.Machine,
                    detail: $"`{command}` cannot run on {worker.Machine}, so it runs on the hub's copy of the project: {Cut(output, FinalCheckDetailCharacters)}",
                    modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                using (WorkerWorkspaceContext.Suspend())
                {
                    (passed, output) = await _tools.RunRepairCommandAsync(plan, command, cancellationToken);
                }
            }

            if (!passed && CommandMissing(output))
            {
                _store.AddEvent(plan.Id, step.Id, attempt, FinalCheckUnavailableKind, tier, node: model.ModelNode ?? model.Machine,
                    detail: $"`{command}` cannot run, so the whole project was not checked after this step: {Cut(output, FinalCheckDetailCharacters)}",
                    modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
                return null;
            }

            if (passed)
            {
                return null;
            }

            (IReadOnlyList<string> ownFiles, bool namesLaterFile) = NamedFiles(plan, step, output);
            if (ownFiles.Count == 0 && namesLaterFile)
            {
                return null;
            }

            return new FinalCheckFailure(command, output, PlanFailure.FailureSignature(output).Items, ownFiles);
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
    /// The source and test files a failure output names, sorted into the files this step lists (Own, relative paths) and whether it
    /// also names a file only a step after this one lists. The names are compared with the declared paths the same way the runner
    /// compares any name with a declared path. A file both this step and a later step list is this step's: the step wrote it, so a
    /// failure in it is the step's to fix now (measured: a step was accepted with a type error standing in its own file because
    /// a later step listed the file too, and the error hid the real fault of the next step).
    /// </summary>
    private static (IReadOnlyList<string> Own, bool NamesLater) NamedFiles(PlanRecord plan, PlanStep step, string output)
    {
        string[] own = FleetPlanContext.DeclaredPaths(plan, step).ToArray();
        string[] later = plan.Steps.Where(candidate => candidate.Id > step.Id)
            .SelectMany(candidate => FleetPlanContext.DeclaredPaths(plan, candidate))
            .ToArray();
        var ownNamed = new List<string>();
        bool namesLater = false;
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

            if (own.Any(declared => FleetPlanContext.Names(declared, full)))
            {
                string relative = Path.GetRelativePath(plan.WorkingDirectory!, full).Replace('\\', '/');
                if (!ownNamed.Contains(relative, StringComparer.OrdinalIgnoreCase))
                {
                    ownNamed.Add(relative);
                }
            }
            else if (later.Any(declared => FleetPlanContext.Names(declared, full)))
            {
                namesLater = true;
            }
        }

        return (ownNamed, namesLater);
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

    private static string FinalCheckRoundMessage(FinalCheckFailure failure)
    {
        string own = failure.OwnFiles.Count == 0
            ? string.Empty
            : $" The output names {string.Join(", ", failure.OwnFiles.Select(file => $"`{file}`"))}, which this step lists, so the fault is in this step's own work: fix it there.";
        return $"The check for this step passed, but the project's final check (`{failure.Command}`) fails now. It runs the whole project, " +
               $"so a change in this step or in an earlier one probably broke something that already worked.{own} Find what broke it and fix it " +
               "there, even in a file this step does not list: a call whose arguments or name changed in an earlier step is a common cause. " +
               "Do not change the tests that were passing before unless the step's text says to. Output:\n" + failure.Output;
    }
}
