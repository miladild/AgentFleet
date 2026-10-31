using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// A passing check does not finish a step that lists files and left one of them as it was. That is the usual shape of a step
/// that was only half done: the model edits one of the two files it was asked to change, and a syntax check passes on the
/// project as it is.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>The run-log kind of the one extra round a passing step is given for the listed files it left untouched.</summary>
    private const string UntouchedRoundKind = "untouched-files";

    /// <summary>
    /// The listed files that the whole step left as they were, when a passing check would otherwise finish it: a path with no
    /// wildcard that is not a folder, and that neither this run's snapshot of the step nor an earlier round recorded as changed.
    /// The run log is read too, because a round before a restart is not in the snapshot this run took.
    /// Empty when the rule does not apply: the tests step of test-first mode, the final check of a longer plan, a step that
    /// lists no such file, a step that changed no file at all, or a project git cannot read. The caller decides what an
    /// untouched file means: the extra round once per attempt, and after that the step is sent back (see UntouchedRoundGiven).
    /// </summary>
    private async Task<IReadOnlyList<string>> UntouchedListedFilesAsync(
        PlanRecord plan, PlanStep step, bool isFinalValidation, CancellationToken cancellationToken)
    {
        if (IsTestsStep(step) || (isFinalValidation && plan.Steps.Count > 1) || string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return [];
        }

        string root = plan.WorkingDirectory;
        string[] listed = FleetPlanContext.DeclaredPaths(plan, step).Where(path => !FleetPlanContext.IsPattern(path)).ToArray();
        if (listed.Length == 0 || !_failFirstSnapshots.TryGetValue($"{plan.Id}:{step.Id}", out PlanTools.WorkSnapshot? start))
        {
            return [];
        }

        PlanRunEvent[] events = (_store.Get(plan.Id)?.Events ?? []).ToArray();

        // The start snapshot is the one the fail-first check keeps: the state before the step's first model call in this run.
        WorkChange since = await ChangedFilesSinceAsync(plan, start, cancellationToken);
        if (since.After is not { } after)
        {
            return [];
        }

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var changed = new HashSet<string>(comparer);
        IEnumerable<string> recorded = events
            .Where(runEvent => runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.RoundClassified)
            .SelectMany(runEvent => runEvent.ChangedFiles ?? []);
        foreach (string file in since.Files.Concat(recorded))
        {
            changed.Add(Path.GetFullPath(Path.Combine(root, file)));
        }

        // A step that changed no file has no partial work to judge: that is the no-change rule's case. A git view that shows no
        // change at all cannot tell a listed file that was left alone from one git does not see.
        if (changed.Count == 0)
        {
            return [];
        }

        // A file too big for the snapshot cannot be told apart from an unchanged one, and a frozen test is put back after each
        // round, so the step is never asked to change it: neither is called untouched.
        IEnumerable<string> frozen = (plan.FrozenTests ?? []).SelectMany(set => set.Files)
            .Select(file => Path.GetFullPath(Path.Combine(root, file.Path)));
        var exempt = new HashSet<string>(start.Uncaptured.Concat(after.Uncaptured).Concat(frozen), comparer);
        return UntouchedListed(listed, changed, exempt)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToArray();
    }

    /// <summary>
    /// The listed paths (full paths) that no change touched, that are not folders and that are not exempt. A listed file that
    /// does not exist and was not created counts as untouched too; the existing rule for files a step was meant to create
    /// usually fails the check first.
    /// </summary>
    internal static IReadOnlyList<string> UntouchedListed(IEnumerable<string> listed, ISet<string> changed, ISet<string> exempt) =>
        listed.Where(path => !changed.Contains(path) && !exempt.Contains(path) && !Directory.Exists(path)).ToArray();

    /// <summary>What the worker is told when a passing check is not taken as the step done yet.</summary>
    private static string UntouchedFilesMessage(IReadOnlyList<string> files) =>
        $"The check passed, but these files listed for this step were not changed: {string.Join(", ", files)}. " +
        "Make the changes the step asks for in them. If a file needs no change, call the report_blocker tool with that file's name and the reason as its evidence; saying so in your reply does not count.";

    /// <summary>What the worker is told when the one extra round did not change a listed file either.</summary>
    private static string StillUntouchedMessage(IReadOnlyList<string> files) =>
        $"The check passed, but these files listed for this step are still unchanged after one more round: {string.Join(", ", files)}. " +
        "Change them. If a file needs no change, call the report_blocker tool now with that file's name and the reason as its evidence; saying so in your reply does not count.";

    /// <summary>Whether the step's one extra round for untouched listed files already ran in this attempt.</summary>
    private bool UntouchedRoundGiven(PlanRecord plan, PlanStep step, int attempt) =>
        (_store.Get(plan.Id)?.Events ?? []).Any(runEvent =>
            runEvent.Kind == UntouchedRoundKind && runEvent.StepId == step.Id && runEvent.Attempt == attempt);

    /// <summary>
    /// The files that a report_blocker of the same round does not name. Its evidence is the file's name, as the tool asks, so a
    /// file the evidence names (by its path as listed or by its file name) is accepted as left alone on purpose.
    /// </summary>
    internal static IReadOnlyList<string> NotNamedByBlocker(IReadOnlyList<string> files, ModelBlocker? blocker)
    {
        string evidence = blocker?.Evidence.Replace('\\', '/') ?? string.Empty;
        return files.Where(file => !NamedIn(evidence, file)).ToArray();
    }

    private static bool NamedIn(string evidence, string relative)
    {
        if (evidence.Length == 0)
        {
            return false;
        }

        foreach (string name in new[] { relative, Path.GetFileName(relative) })
        {
            if (Regex.IsMatch(evidence, @"(?<![\w.-])" + Regex.Escape(name) + @"(?![\w-])", RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
