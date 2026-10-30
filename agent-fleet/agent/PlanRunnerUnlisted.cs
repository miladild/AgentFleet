namespace AgentFleet;

/// <summary>
/// A passing check does not accept a step that left files the plan does not name in the project folder, such as scratch scripts
/// and test data written next to the code. The worker gets one extra round in the same conversation to delete what it only used
/// for trying things. The step is accepted after that round even if files remain; the step-done note still names them.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>The run-log kind of the one extra round a passing step is given for the files it created that the plan does not name.</summary>
    private const string UnlistedRoundKind = "unlisted-files";

    /// <summary>How many file names the extra round lists before it only counts the rest.</summary>
    private const int UnlistedNamesShown = 10;

    /// <summary>
    /// The files this step created that the plan does not name and that a passing check would otherwise accept as they are. These
    /// are the files the step-done note reports (see StrayFiles), less the ones that are not leftovers: incidental files that a
    /// package manager or a build rewrites, and anything in the fleet's own folder or a dependency or version-control folder. A file
    /// that a failing check of this step named is still asked about: that note was a report, not a deletion. Paths are relative, with
    /// forward slashes. Empty when the rule does not apply: the tests step of test-first mode, the final check of a longer plan, and
    /// a plan without a project folder.
    /// </summary>
    private IReadOnlyList<string> UnlistedNewFiles(PlanRecord plan, PlanStep step, bool isFinalValidation)
    {
        if (IsTestsStep(step) || (isFinalValidation && plan.Steps.Count > 1) || string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return [];
        }

        return StrayFiles(plan, step, int.MaxValue)
            .Where(file => !RepairLadder.IsIncidentalFile(file) && !IsFleetOrDependencyPath(file))
            .Select(file => file.Replace('\\', '/'))
            .ToArray();
    }

    /// <summary>Whether the extra round for files the plan does not name already ran in this attempt.</summary>
    private bool UnlistedRoundGiven(PlanRecord plan, PlanStep step, int attempt) =>
        (_store.Get(plan.Id)?.Events ?? []).Any(runEvent =>
            runEvent.Kind == UnlistedRoundKind && runEvent.StepId == step.Id && runEvent.Attempt == attempt);

    /// <summary>The file names for a message or a run-log detail: the first few, then how many more there are.</summary>
    private static string UnlistedNames(IReadOnlyList<string> files)
    {
        string shown = string.Join(", ", files.Take(UnlistedNamesShown));
        int more = files.Count - UnlistedNamesShown;
        return more > 0 ? $"{shown}, and {more} more" : shown;
    }

    /// <summary>What the worker is told when a passing check is not taken as the step done yet, because of the files it left behind.</summary>
    private static string UnlistedFilesMessage(IReadOnlyList<string> files) =>
        $"The check passed, but these files were created and are not part of this step: {UnlistedNames(files)}. " +
        "Delete the ones you only used for trying things (delete_file). If a test writes files into the project, change the test to write under " +
        "the OS temp folder with a unique name and remove them, then delete the leftovers. Keep a file only if the step needs it.";

    /// <summary>
    /// The part of a round's start snapshot that putting back discarded work may restore: the files the step found in the project when
    /// it started. Restoring is for earlier work. A file this step created in an earlier round is not that, so a worker that deletes it
    /// as a leftover (the extra round asks for this) is not overruled. Without a start record the snapshot is used as it is.
    /// </summary>
    private PlanTools.WorkSnapshot? EarlierWorkOnly(PlanRecord plan, PlanStep step, PlanTools.WorkSnapshot? work)
    {
        if (work is null || !_filesAtStart.TryGetValue($"{plan.Id}:{step.Id}", out HashSet<string>? atStart))
        {
            return work;
        }

        return work with { Files = work.Files.Where(file => atStart.Contains(file.Key)).ToDictionary(file => file.Key, file => file.Value) };
    }

    /// <summary>A path inside the fleet's own folder or a dependency or version-control folder: the fleet or the tools wrote it, not the step.</summary>
    private static bool IsFleetOrDependencyPath(string relative) =>
        relative.Replace('\\', '/').Split('/').Any(part => part is "node_modules" or ".git" or ".agent-fleet");
}
