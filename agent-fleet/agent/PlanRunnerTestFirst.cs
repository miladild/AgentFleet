using System.Text.Json;

namespace AgentFleet;

internal sealed partial class PlanRunner
{
    private bool IsTestsStep(PlanStep step) => _testFirstMode == TestFirstMode.On && step.Kind == "tests";

    private StepCompletion DecideTestsStep(PlanRecord plan, PlanStep step, int attempt, string tier, int rung, int round,
        ModelAttemptResult model, StepCompletion check, IReadOnlyList<string>? changedFiles)
    {
        TestFirstDecision decision = TestFirst.Evaluate(check.Output, changedFiles);
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
}
