using System.Security.Cryptography;
using System.Text.Json;

namespace AgentFleet;

internal sealed partial class PlanRunner
{
    private bool IsTestsStep(PlanStep step) => _testFirstMode == TestFirstMode.On && step.Kind == "tests";

    private StepCompletion DecideTestsStep(PlanRecord plan, PlanStep step, int attempt, string tier, int rung, int round,
        ModelAttemptResult model, StepCompletion check, IReadOnlyList<string>? changedFiles)
    {
        TestFirstDecision decision = TestFirst.Evaluate(check.Output, changedFiles);
        // A placeholder file is refused before the requirement check, so a refused step is never marked as covering a requirement.
        if (decision.Accepted && changedFiles is not null)
        {
            IReadOnlyList<string> placeholders = TestFilesOf(plan, step, changedFiles)
                .SelectMany(file => PlaceholderTests.Find(file.Path, file.Text)).ToArray();
            if (placeholders.Count > 0)
            {
                decision = new TestFirstDecision("placeholder", decision.Executed, decision.Failing, PlaceholderFeedback(placeholders));
            }
        }

        IReadOnlyList<PlanRequirement>? covered = null;
        if (decision.Accepted && plan.Requirements is { Count: > 0 } requirements && IsLastTestsStep(plan, step))
        {
            covered = RequirementCoverage.Apply(requirements, TestFilesOf(plan, step, changedFiles));
            IReadOnlyList<PlanRequirement> uncovered = covered.Where(requirement => requirement.CoveredBy.Count == 0).ToArray();
            if (uncovered.Count > 0)
            {
                decision = new TestFirstDecision("uncovered", decision.Executed, decision.Failing, RequirementCoverage.UncoveredFeedback(uncovered));
            }
        }

        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.TestFirst, tier, node: model.ModelNode,
            detail: JsonSerializer.Serialize(new { outcome = decision.Outcome, executed = decision.Executed, failing = decision.Failing,
                changedFiles = changedFiles?.Count }),
            modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
        if (decision.Accepted && covered is not null)
        {
            _store.Update(plan.Id, current => current with { Requirements = covered });
        }

        return new StepCompletion(decision.Accepted, decision.Feedback,
            decision.Accepted ? check.Output : decision.Feedback + "\n\n" + check.Output, check.Restored);
    }

    private static string PlaceholderFeedback(IReadOnlyList<string> placeholders) =>
        "These tests fail by construction and check nothing: " + string.Join("\n", placeholders.Take(5)) + ". " +
        "Write each test so that it calls the code the step will add and checks its real result (the inputs it passes and the value it expects); " +
        "it must fail now only because that behaviour is missing, and pass once it exists.";

    // Only the last tests step is held to the whole list: the tests of every tests step count toward it, and an earlier
    // tests step may cover a part of it.
    private static bool IsLastTestsStep(PlanRecord plan, PlanStep step) =>
        plan.Steps.Where(candidate => candidate.Kind == "tests").Select(candidate => candidate.Id).DefaultIfEmpty(0).Max() == step.Id;

    // The test files of every tests step, as the hub checkout holds them: a worker's edits are synced there before the check's
    // result is decided. The step being decided also counts the test files it changed, because a model may write its named
    // tests into a file the plan did not declare. A file that cannot be read is left out, so its requirements show as uncovered.
    private static IReadOnlyList<TestFileText> TestFilesOf(PlanRecord plan, PlanStep decided, IReadOnlyList<string>? changedFiles)
    {
        var files = new List<TestFileText>();
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return files;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.WorkingDirectory));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (PlanStep step in plan.Steps.Where(candidate => candidate.Kind == "tests"))
        {
            IEnumerable<string> paths = step.Files.Where(IsTestPath);
            if (step.Id == decided.Id && changedFiles is not null)
            {
                paths = paths.Concat(changedFiles.Where(IsTestPath));
            }

            StringComparer pathComparer = comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            foreach (string relative in paths.Select(path => path.Replace('\\', '/')).Distinct(pathComparer))
            {
                string path = Path.GetFullPath(Path.Combine(root, relative));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                {
                    continue;
                }

                try
                {
                    files.Add(new TestFileText(relative, File.ReadAllText(path)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }
        }

        return files;
    }

    // Every frozen set whose implementation step is this step: the first ordinary step after its tests step that names a non-test
    // code file. Several tests steps can name the same implementation step, and each of their sets must then be proven.
    internal IReadOnlyList<PlanFrozenTestSet> ImplementationSetsFor(PlanRecord plan, PlanStep step)
    {
        if (_testFirstMode != TestFirstMode.On || plan.FrozenTests is not { Count: > 0 }) return [];
        return plan.FrozenTests.Where(set => ImplementationStepFor(plan.Steps, set)?.Id == step.Id).ToArray();
    }

    // A step whose own check runs the frozen tests proves them, so it is preferred; otherwise the first code step is held to them.
    internal static PlanStep? ImplementationStepFor(IReadOnlyList<PlanStep> steps, PlanFrozenTestSet set)
    {
        List<PlanStep> candidates = steps.Where(candidate => candidate.Id > set.StepId && candidate.Kind != "tests" &&
            candidate.Files.Any(path => !IsTestPath(path) && TestFirst.IsCodeFile(path))).ToList();
        string command = set.Command.Trim();
        return candidates.FirstOrDefault(candidate => string.Equals(candidate.Verify?.Trim(), command, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault();
    }

    // The test files a tests step was accepted with are fixed from now on. Their hashes come from the hub copy, which the
    // step's sync has just brought up to date.
    private void FreezeAcceptedTests(PlanRecord plan, PlanStep step, IReadOnlyList<string> files, List<string> frozenNow)
    {
        var entries = new List<PlanFrozenTestFile>();
        foreach (string file in files)
        {
            string relative;
            try
            {
                relative = WorkerWorkspacePath.RelativeToProject(file, plan.WorkingDirectory!);
            }
            catch (WorkerPathRefusedException)
            {
                continue;
            }

            string local = Path.Combine(plan.WorkingDirectory!, relative.Replace('/', Path.DirectorySeparatorChar));
            // The accepted bytes are kept before the set is recorded, so a set never names content that is not stored.
            if (File.Exists(local)) entries.Add(new PlanFrozenTestFile(relative, _store.SaveFrozenContent(plan.Id, File.ReadAllBytes(local))));
        }

        if (entries.Count == 0 || string.IsNullOrWhiteSpace(step.Verify)) return;
        _store.AddFrozenTests(plan.Id, new PlanFrozenTestSet(step.Id, step.Verify, entries));
        frozenNow.AddRange(entries.Select(entry => entry.Path));
    }

    // Before a check, and before the implementation step's frozen run, the frozen files must be the accepted ones: a command the
    // model ran can change or delete one, and no file tool sees that. The files put back are added to restoredNow.
    private async Task RestoreFrozenTestsAsync(PlanRecord plan, IWorkerWorkspaceSession workspace, List<string> restoredNow,
        CancellationToken cancellationToken)
    {
        if (_testFirstMode != TestFirstMode.On || plan.FrozenTests is not { Count: > 0 }) return;
        restoredNow.AddRange(await workspace.RestoreFrozenTestsAsync(cancellationToken));
    }

    // A step that ran on the hub has no worker copy to restore: its frozen files are checked in the project folder itself. A file
    // that differs is rewritten from the stored accepted bytes, and a file with no verified copy fails the round.
    private void RestoreHubFrozenTests(PlanRecord plan, List<string> restoredNow)
    {
        foreach ((PlanFrozenTestFile frozen, string full) in HubFrozenFiles(plan))
        {
            if (FrozenFileMatches(full, frozen.Sha256)) continue;
            byte[]? accepted = _store.ReadFrozenContent(plan.Id, frozen.Sha256);
            if (accepted is null)
            {
                throw new FrozenTestsUnrestorableException($"The fixed test file {frozen.Path} was changed and could not be put back: " +
                    "no verified accepted copy is kept for it. Change the code, not the test.");
            }

            // A link at the test's path would take the write to a file outside the project, so the round fails instead.
            if (HasLinkedPathComponent(Path.GetFullPath(plan.WorkingDirectory!), full))
                throw new FrozenTestsUnrestorableException($"The fixed test file {frozen.Path} was changed and could not be put back, " +
                    "because the project holds a link at its path. Change the code, not the test.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, accepted);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Something the file system will not replace (a folder, a locked or read-only file) stands where the test belongs.
                throw new FrozenTestsUnrestorableException($"The fixed test file {frozen.Path} was changed and could not be put back, " +
                    "because the project holds something else at its path. Change the code, not the test.");
            }

            restoredNow.Add(frozen.Path);
        }
    }

    // The hub copy of each frozen file is put back from the accepted bytes before a worker is staged, so the worker copies the
    // accepted test. A file with no verified copy is left as it is: the worker restore still refuses a copy that does not match.
    private void HealFrozenHubCopies(PlanRecord plan)
    {
        foreach ((PlanFrozenTestFile frozen, string full) in HubFrozenFiles(plan))
        {
            if (FrozenFileMatches(full, frozen.Sha256)) continue;
            byte[]? accepted = _store.ReadFrozenContent(plan.Id, frozen.Sha256);
            if (accepted is null || HasLinkedPathComponent(Path.GetFullPath(plan.WorkingDirectory!), full)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, accepted);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not put the accepted copy of frozen test {Path} back in the plan folder.", frozen.Path);
            }
        }
    }

    // The frozen files of the plan as it is in the store now, each with its full path in the project folder.
    private IEnumerable<(PlanFrozenTestFile Frozen, string Full)> HubFrozenFiles(PlanRecord plan)
    {
        PlanRecord current = _store.Get(plan.Id) ?? plan;
        if (_testFirstMode != TestFirstMode.On || current.FrozenTests is not { Count: > 0 } || string.IsNullOrWhiteSpace(current.WorkingDirectory))
        {
            return [];
        }

        string root = Path.GetFullPath(current.WorkingDirectory);
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // A stored path that leads out of the project folder is never read or written: a frozen file is inside the project by construction.
        return current.FrozenTests.SelectMany(set => set.Files)
            .Select(frozen => (frozen, Path.GetFullPath(Path.Combine(root, frozen.Path.Replace('/', Path.DirectorySeparatorChar)))))
            .Where(item => item.Item2.StartsWith(prefix, comparison))
            .ToArray();
    }

    private static bool FrozenFileMatches(string full, string sha256) =>
        File.Exists(full) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) == sha256;

    // The hub's file tools refuse to change the frozen files of a plan step that runs on the hub, as the worker's tools do.
    private IDisposable? HubFrozenScopeFor(PlanRecord plan)
    {
        PlanRecord current = _store.Get(plan.Id) ?? plan;
        if (_testFirstMode != TestFirstMode.On || current.FrozenTests is not { Count: > 0 } || string.IsNullOrWhiteSpace(current.WorkingDirectory))
        {
            return null;
        }

        return HubFrozenTestScope.Push(current.WorkingDirectory, WorkerWorkspaceManager.FrozenFilesOf(current));
    }

    // The implementation step is accepted only when every frozen command runs, finds tests and passes in the step's workspace. Each
    // distinct command runs once, and the first one that fails or does not run decides the refusal. Returns null when the step may be accepted.
    private async Task<string?> FrozenTestsRefusalAsync(PlanRecord plan, IReadOnlyList<PlanFrozenTestSet> sets, CancellationToken cancellationToken)
    {
        foreach (string command in sets.Select(set => set.Command).Distinct(StringComparer.Ordinal))
        {
            string output = await _tools.RunFrozenTestsAsync(plan, command, cancellationToken);
            bool noTests = CheckVacuity.NoTestsRan(output);
            if (!noTests && output.StartsWith("Exit code: 0", StringComparison.Ordinal)) continue;

            string tail = output.Length <= 4000 ? output : output[^4000..];
            return noTests
                ? $"Step not accepted: the fixed tests did not run. Make `{command}` run them; change the code, not the tests.\n\n{tail}"
                : $"Step not accepted: the fixed tests still fail. Make `{command}` pass; change the code, not the tests.\n\n{tail}";
        }

        return null;
    }

    internal static string RestoredNote(IEnumerable<string> files) =>
        $"These fixed test files were changed and have been put back; change the code, not the tests: {string.Join(", ", files.Distinct(StringComparer.Ordinal))}.";

    // One run event per round that froze or restored something.
    private void RecordFrozenTests(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model, int rung, int round,
        IReadOnlyList<string> frozenNow, IReadOnlyList<string> restoredNow)
    {
        if (frozenNow.Count == 0 && restoredNow.Count == 0) return;
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.FrozenTests, tier, node: model.ModelNode,
            detail: JsonSerializer.Serialize(new { frozen = frozenNow.Count, restored = restoredNow.Count,
                files = frozenNow.Concat(restoredNow).Distinct(StringComparer.Ordinal).ToArray() }),
            modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
    }
}
