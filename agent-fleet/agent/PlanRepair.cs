using System.Text;

namespace AgentFleet;

/// <summary>Where a step stands on the repair ladder, read back from the run log so that a restart keeps it.</summary>
/// <param name="Rung">1 the requested tier, 2 the hub model in the same conversation, 3 a fresh conversation with a brief.</param>
/// <param name="RoundsInRung">Rounds on the current rung whose check ran and failed.</param>
/// <param name="RoundsTotal">Rounds of the step since the user last approved a retry.</param>
internal sealed record RepairPosition(int Rung, int RoundsInRung, int RoundsTotal);

internal enum RepairMove
{
    /// <summary>Stay on this rung: the failure goes back into the same conversation.</summary>
    NextRound,

    /// <summary>Rung 2: the same conversation, now answered by the hub model.</summary>
    ContinueOnHub,

    /// <summary>Rung 3: a new conversation that starts from a brief of what was tried.</summary>
    FreshConversation,

    /// <summary>Every rung has had its rounds.</summary>
    GiveUp
}

internal sealed record RepairDecision(RepairMove Move, int ToRung, string Why);

/// <summary>
/// What the plan runner does when a step's approved check fails. The model is not restarted from zero: the failure goes
/// back into the conversation it already has (a round), and only when rounds stop paying off does the step climb a rung:
/// 1 the requested tier, 2 the hub model continuing the same conversation, 3 a fresh conversation on the hub that starts
/// from a brief. A plan that does not allow hub rescue has no hub rung: its third rung is a fresh conversation on a worker.
/// </summary>
internal static class RepairLadder
{
    public const int DefaultRoundsPerRung = 3;
    public const int RequestedTierRung = 1;
    public const int HubRung = 2;
    public const int FreshRung = 3;

    public static string Name(int rung) => rung switch
    {
        RequestedTierRung => "the requested tier",
        HubRung => "the hub model, same conversation",
        _ => "a fresh conversation with a brief"
    };

    /// <summary>The rung and round count from the step's events since the user last approved a retry, oldest first.</summary>
    public static RepairPosition PositionFrom(IEnumerable<PlanRunEvent> stepEvents)
    {
        int rung = RequestedTierRung;
        int inRung = 0;
        int total = 0;
        foreach (PlanRunEvent runEvent in stepEvents)
        {
            if (runEvent.Kind == RunEventKind.RungChanged && runEvent.Rung is { } to)
            {
                rung = to;
                inRung = 0;
            }
            else if (IsVerdictRound(runEvent))
            {
                inRung++;
                total++;
            }
        }

        return new RepairPosition(rung, inRung, total);
    }

    /// <summary>
    /// A file that a package manager or a build rewrites as a side effect, which is not a change to the code the failing check
    /// is about: lock files, build caches and logs. A round that changed only these made no progress on the step, unless the
    /// step names the file.
    /// </summary>
    public static bool IsIncidentalFile(string path)
    {
        string name = path.Replace('\\', '/').Split('/')[^1];
        return name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tsbuildinfo", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("npm-shrinkwrap.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bun.lockb", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("go.sum", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("poetry.lock", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A round whose check ran and failed (a model call that never got that far has no round number).</summary>
    public static bool IsVerdictRound(PlanRunEvent runEvent) =>
        runEvent.Kind == RunEventKind.RoundClassified && runEvent.Round is not null &&
        !string.Equals(runEvent.FailureClass, "Passed", StringComparison.Ordinal);

    /// <summary>
    /// What to do after a round whose check failed. A round that changed nothing (no edit, or the same failures with no
    /// file changed) cannot be improved by another round on the same model, so it climbs at once; otherwise a rung gets
    /// its round budget first.
    /// </summary>
    public static RepairDecision Decide(int rung, int roundsInRung, int roundsPerRung, bool noChange, string noChangeWhy, bool hubRungAvailable)
    {
        if (!noChange && roundsInRung < roundsPerRung)
        {
            return new RepairDecision(RepairMove.NextRound, rung, string.Empty);
        }

        string why = noChange
            ? noChangeWhy
            : $"{roundsInRung} round{(roundsInRung == 1 ? string.Empty : "s")} on this rung did not get the check passing";
        return rung switch
        {
            RequestedTierRung when hubRungAvailable => new RepairDecision(RepairMove.ContinueOnHub, HubRung, why),
            RequestedTierRung or HubRung => new RepairDecision(RepairMove.FreshConversation, FreshRung, why),
            _ => new RepairDecision(RepairMove.GiveUp, rung, why)
        };
    }
}

/// <summary>How the project stood after a round, by what the approved check said.</summary>
/// <param name="Compiles">False when the check printed compile, syntax or build errors.</param>
/// <param name="Failures">How many failing names (or, when it does not build, distinct error codes) the check printed.</param>
internal readonly record struct RoundScore(bool Compiles, int Failures)
{
    /// <summary>
    /// Worse than the best round so far: code that stopped building where it used to build, or more failing names. Fewer
    /// failures that come with code that no longer builds do not count as better: no test ran.
    /// </summary>
    public bool IsWorseThan(RoundScore best) =>
        (best.Compiles && !Compiles) || (Compiles == best.Compiles && Failures > best.Failures);

    /// <summary>The score of a failed check, or null when its output names no failures (a hash of it cannot be counted).</summary>
    public static RoundScore? Of(FailureFingerprint signature, string? checkOutput) =>
        signature.Items.Count == 0
            ? null
            : new RoundScore(!PlanFailure.BuildBroken(checkOutput) && !signature.Items.Any(PlanFailure.IsBuildErrorCode), signature.Items.Count);

    public string Describe() => Compiles
        ? $"{Failures} failing"
        : $"code that does not build ({Failures} error code{(Failures == 1 ? string.Empty : "s")})";
}

/// <summary>What the runner undid after a round that left the step worse than its best round.</summary>
/// <param name="Round">The round that was undone.</param>
/// <param name="NewFailures">The failures that round brought that the best round did not have.</param>
/// <param name="Restored">The files put back as they were after the best round.</param>
internal sealed record RollbackNote(
    int Round,
    RoundScore Worse,
    int BestRound,
    RoundScore Best,
    IReadOnlyList<string> NewFailures,
    IReadOnlyList<string> Restored);

/// <summary>The words the runner says to a model between rounds, and the brief a fresh conversation starts from.</summary>
internal static class RepairMessages
{
    private const int MaxListed = 8;
    private const int MaxSaidCharacters = 160;

    /// <summary>The failing names a stored failure signature holds (none when it is only a hash of the output).</summary>
    public static IReadOnlyList<string> FailingNames(string? signature) =>
        string.IsNullOrWhiteSpace(signature) || signature.StartsWith("sha256:", StringComparison.Ordinal)
            ? []
            : signature.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The message that goes into the conversation the model already has, after the approved check failed. The first
    /// prompt is not sent again: the model has it, and everything it did since. What changed since the round before is
    /// said in a stable form (names fixed, names new, files changed) so the model can see whether it is getting closer.
    /// </summary>
    /// <param name="previousFailing">The failing names after the round before this one (empty when there was none, or no names).</param>
    /// <param name="currentFailing">The failing names now.</param>
    /// <param name="sameOutput">The check printed what it printed after the round before.</param>
    /// <param name="changedFiles">The files the last round changed; null when that could not be told.</param>
    /// <param name="handover">Said first when a more capable model has just taken over the conversation.</param>
    /// <param name="rollback">Said instead of the comparison with the round before when that round was undone.</param>
    public static string Build(
        PlanRecord plan,
        PlanStep step,
        string failure,
        int round,
        IReadOnlyList<string> previousFailing,
        IReadOnlyList<string> currentFailing,
        bool sameOutput,
        IReadOnlyList<string>? changedFiles,
        string? handover = null,
        RollbackNote? rollback = null)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(handover))
        {
            text.AppendLine(handover);
            text.AppendLine();
        }

        text.AppendLine(step.Verify is null
            ? $"Round {round}: the step is not finished yet. What went wrong:"
            : $"Round {round}: the approved check did not pass yet. The fleet runs it for you with `{step.Verify}`. What it printed:");
        text.AppendLine(string.IsNullOrWhiteSpace(failure) ? "(no output)" : failure);

        var changes = new List<string>();
        if (rollback is not null)
        {
            text.AppendLine();
            text.AppendLine(RollbackSentence(rollback));
        }
        else if (currentFailing.Count > 0)
        {
            if (previousFailing.Count > 0)
            {
                string[] fixedNow = previousFailing.Except(currentFailing, StringComparer.OrdinalIgnoreCase).ToArray();
                string[] added = currentFailing.Except(previousFailing, StringComparer.OrdinalIgnoreCase).ToArray();
                string[] still = currentFailing.Intersect(previousFailing, StringComparer.OrdinalIgnoreCase).ToArray();
                if (fixedNow.Length > 0) changes.Add($"fixed since the round before: {List(fixedNow)}");
                if (added.Length > 0) changes.Add($"new failures: {List(added)}");
                if (still.Length > 0) changes.Add($"still failing: {List(still)}");
            }
            else
            {
                changes.Add($"failing: {List(currentFailing)}");
            }
        }
        else if (sameOutput)
        {
            changes.Add("the check printed the same as after the round before");
        }

        if (changedFiles is not null && rollback is null)
        {
            changes.Add(changedFiles.Count == 0
                ? "you changed no file in the last round, so nothing could have changed the result"
                : $"files you changed in the last round: {List(changedFiles)}");
        }

        if (changes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Since your last round:");
            foreach (string change in changes)
            {
                text.AppendLine($"- {change}");
            }
        }

        foreach (string hint in PlanRunner.RetryHints(step, failure))
        {
            text.AppendLine();
            text.AppendLine(hint);
        }

        text.AppendLine();
        text.AppendLine("Look at what is on disk (read_file, list_directory), fix the cause this output shows, and change a file: a round that changes nothing cannot make the check pass. " +
                        (step.Verify is null
                            ? "When you are finished, reply with one short sentence saying what you did."
                            : "Run the check yourself with run_command before you finish, and as soon as it passes, stop: reply with one short sentence saying what you did."));
        text.AppendLine();
        text.AppendLine(PlanRunner.ActFirst);
        text.AppendLine(FleetPlanStore.Marker(plan.Id));
        return text.ToString();
    }

    /// <summary>
    /// What a fresh conversation is told about the rounds before it, from the step's events (oldest first, since the user
    /// last approved a retry): which machine worked, what it changed, what the check then said, and in its own words what it
    /// thought it had done. Empty when no round has run.
    /// </summary>
    public static string Brief(IReadOnlyList<PlanRunEvent> stepEvents)
    {
        PlanRunEvent[] all = stepEvents.Where(RepairLadder.IsVerdictRound).ToArray();
        PlanRunEvent[] rounds = all.TakeLast(9).ToArray();
        if (rounds.Length == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.AppendLine("A conversation before this one worked on this step and did not get its check passing. What it tried, oldest first:");
        var touched = new List<string>();
        int number = all.Length - rounds.Length;
        foreach (PlanRunEvent round in rounds)
        {
            number++;
            string machine = round.ModelNode ?? round.Node ?? "a machine";
            string files = round.ChangedFiles is { Count: > 0 }
                ? $"changed {List(round.ChangedFiles)}"
                : round.FilesChanged is > 0 ? $"changed {round.FilesChanged} file(s)" : "changed no file";
            IReadOnlyList<string> names = FailingNames(round.FailureSignature);
            string result = names.Count > 0 ? $"the check then failed on {List(names)}" : "the check then failed";
            string undone = stepEvents.Any(runEvent => runEvent.Kind == RunEventKind.RoundRolledBack &&
                string.Equals(runEvent.FailureClass, "RolledBack", StringComparison.Ordinal) &&
                runEvent.Attempt == round.Attempt && runEvent.Rung == round.Rung && runEvent.Round == round.Round)
                ? " It left the step worse than the best round before it, so it was undone."
                : string.Empty;
            text.AppendLine($"- Round {number} on {machine}: {files}; {result}.{Said(stepEvents, round)}{undone}");
            if (round.ChangedFiles is not null)
            {
                touched.AddRange(round.ChangedFiles.Where(file => !touched.Contains(file, StringComparer.OrdinalIgnoreCase)));
            }
        }

        if (touched.Count > 0)
        {
            text.AppendLine($"Files changed so far: {List(touched)}.");
        }

        text.AppendLine("Find the root cause first, then fix it. Do not repeat an approach that already failed: read the failing test or check and the code it exercises before you edit anything.");
        return text.ToString();
    }

    private static string RollbackSentence(RollbackNote rollback)
    {
        string broke = rollback.NewFailures.Count > 0 ? $" It broke: {List(rollback.NewFailures)}." : string.Empty;
        string files = rollback.Restored.Count > 0 ? $" ({List(rollback.Restored)})" : string.Empty;
        return $"Round {rollback.Round} made the check worse, so the fleet undid it: it left {rollback.Worse.Describe()} where round " +
               $"{rollback.BestRound} had {rollback.Best.Describe()}.{broke} The files are back as they were after round {rollback.BestRound}{files}, " +
               "so they are not as you left them: read them again before you edit. Do not repeat that change. The output above is the check's " +
               "result for this state: fix what is still failing in another way.";
    }

    // The model's own summary of the round, from the attempt-ended event of the same round.
    private static string Said(IReadOnlyList<PlanRunEvent> events, PlanRunEvent round)
    {
        PlanRunEvent? ended = events.LastOrDefault(runEvent => runEvent.Kind == RunEventKind.AttemptEnded &&
            runEvent.Attempt == round.Attempt && runEvent.Round == round.Round && runEvent.Rung == round.Rung);
        if (ended is null)
        {
            return string.Empty;
        }

        string detail = ended.Detail;
        int colon = detail.IndexOf(": ", StringComparison.Ordinal);
        string summary = (colon >= 0 && detail.StartsWith("Model finished after", StringComparison.Ordinal) ? detail[(colon + 2)..] : detail)
            .Replace('\n', ' ').Trim();
        if (summary.Length == 0)
        {
            return string.Empty;
        }

        return summary.Length > MaxSaidCharacters
            ? $" It said: \"{summary[..MaxSaidCharacters].TrimEnd()}...\""
            : $" It said: \"{summary}\"";
    }

    private static string List(IReadOnlyList<string> items) =>
        items.Count <= MaxListed
            ? string.Join(", ", items)
            : $"{string.Join(", ", items.Take(MaxListed))} and {items.Count - MaxListed} more";
}
