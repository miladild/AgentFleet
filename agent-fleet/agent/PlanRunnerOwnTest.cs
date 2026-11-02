using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentFleet;

/// <summary>
/// A step's model may write a wrong test and then spend every round changing correct code to satisfy it: measured, two live runs
/// of three were stopped that way, and the model said in its own words what was wrong with its test without ever changing it.
/// When a step's check has failed for a number of rounds in a row, and every failing test is in a test file the step created and
/// lists, a model that did not write either side rules on it, as it does on a disputed fixed test. A correction is written to the
/// hub copy only when TestRuling.Guard accepts it, and every ruling is logged. A ruling that cannot be obtained changes nothing.
/// </summary>
internal sealed partial class PlanRunner
{
    private const int MaxRulerReplyCharacters = 2000;

    /// <summary>
    /// Whether every failing item of a check names a test in one of the step's own test files, with no compiler or build error.
    /// </summary>
    internal static bool OnlyOwnTestsFail(string? checkOutput, IReadOnlyList<TestFileText> ownTestFiles) =>
        ownTestFiles.Count > 0 &&
        TestDispute.Evaluate(testFirstOn: true, ordinaryStep: true, hasFrozenFiles: true, disputed: true, rulingsMade: 0, unresolvedAsks: 0,
            checkOutput, ownTestFiles).Due;

    // The test files the step lists that were not in the project when the step began, as the hub copy holds them now.
    private IReadOnlyList<TestFileText> ReadOwnTestFiles(PlanRecord plan, PlanStep step)
    {
        var files = new List<TestFileText>();
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || !_filesAtStart.TryGetValue($"{plan.Id}:{step.Id}", out HashSet<string>? before))
        {
            return files;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.WorkingDirectory));
        string[] declared = FleetPlanContext.DeclaredPaths(plan, step).ToArray();
        foreach (string full in FleetPlanContext.SnapshotFiles(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (before.Contains(full) || !IsTestPath(relative) || !declared.Any(path => FleetPlanContext.Names(path, full)))
            {
                continue;
            }

            try
            {
                files.Add(new TestFileText(relative, File.ReadAllText(full)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return files;
    }

    private async Task<TestRulingOutcome> RuleOnOwnTestAsync(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model,
        IReadOnlyList<string> changedFiles, string checkOutput, IReadOnlyList<TestFileText> ownTests, int roundsFailing, int rung, int round,
        CancellationToken cancellationToken)
    {
        PlanRecord latest = _store.Get(plan.Id) ?? plan;
        PlanRunEvent[] earlierRulings = StepEventsSinceRetry(plan.Id, step.Id).Where(runEvent => runEvent.Kind == RunEventKind.TestRuling).ToArray();
        if (earlierRulings.Any(runEvent => runEvent.FailureClass is TestWrongClass or CodeWrongClass) ||
            earlierRulings.Count(runEvent => runEvent.FailureClass is NoVerdictClass or RefusedClass or UndoneClass) >= TestDispute.MaxUnresolvedAsks)
        {
            return NoRuling;
        }

        (string reviewerTier, string? ruler, string? unavailable) = ChooseReviewer(latest, step, model);
        if (ruler is null)
        {
            RecordRuling(plan, step, attempt, tier, model, rung, round, "Unavailable",
                $"No independent model is available to rule on the step's own failing test ({unavailable}); nothing changes.");
            return NoRuling;
        }

        IReadOnlyList<string> items = TestDispute.FailingItems(checkOutput);
        IReadOnlyList<TestFileText> holding = TestDispute.Holding(ownTests, items);
        string[] testPaths = holding.Select(file => file.Path).ToArray();
        IReadOnlyList<TestFileText> changed = ReadFilesToReview(latest, step, changedFiles)
            .Where(file => !testPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
            .Select(file => new TestFileText(file.Path, file.Content)).ToArray();
        string said = string.IsNullOrWhiteSpace(model.Summary) ? "(nothing)" : Cut(model.Summary.Trim(), MaxRulerReplyCharacters);
        string prompt = TestRuling.BuildPrompt(latest, step, holding, checkOutput, said, changed, ownTest: true);
        IStepSession session = _agent.OpenSession(PlanRunnerToolPolicy.TestRulingRole);
        StepAgentReply reply;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(RulingTimeLimit);
            using IDisposable? folder = HubProjectFolderScope.For(latest);
            reply = await session.SendAsync(prompt, reviewerTier, ruler, limit.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            string detail = exception is OperationCanceledException
                ? $"The ruler ({ruler}) did not answer within {Duration(RulingTimeLimit)}; nothing changes."
                : $"The ruler ({ruler}) could not be reached: {exception.Message} Nothing changes.";
            _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the ruling on the step's own test failed.", plan.Id, step.Id);
            RecordRuling(plan, step, attempt, tier, model, rung, round, "Unavailable", detail, ruler);
            return NoRuling;
        }

        string why = $"The check failed {roundsFailing} rounds in a row on tests the step wrote ({string.Join(", ", testPaths)}).";
        TestRulingVerdict verdict = TestRuling.Parse(reply.Text);
        switch (verdict.Kind)
        {
            case TestRulingKind.CodeWrong:
                RecordRuling(plan, step, attempt, tier, model, rung, round, CodeWrongClass,
                    $"{why} The ruler ({ruler}) found the test right: {Summarize(verdict.Paragraph)}", ruler);
                return new TestRulingOutcome($"An independent model read the failing test this step wrote and found it right, so the code must change: {verdict.Paragraph}", false);
            case TestRulingKind.TestWrong:
                return await ApplyOwnTestCorrectionAsync(latest, step, attempt, tier, model, rung, round, verdict, holding, items, ruler, why,
                    cancellationToken);
            default:
                RecordRuling(plan, step, attempt, tier, model, rung, round, NoVerdictClass,
                    $"{why} The ruler ({ruler}) gave no TEST-WRONG or CODE-WRONG verdict: {Summarize(reply.Text)} Nothing changes.", ruler);
                return NoRuling;
        }
    }

    // Every file is checked before the first write; a write that fails puts back the files already written. The written correction
    // is then checked at once (see CheckCorrectionAsync) and put back when it does not help.
    private async Task<TestRulingOutcome> ApplyOwnTestCorrectionAsync(PlanRecord plan, PlanStep step, int attempt, string tier,
        ModelAttemptResult model, int rung, int round, TestRulingVerdict verdict, IReadOnlyList<TestFileText> ownTests, IReadOnlyList<string> items,
        string ruler, string why, CancellationToken cancellationToken)
    {
        if (TestRuling.Guard(ownTests, verdict.Files, items) is { } refusal)
        {
            RecordRuling(plan, step, attempt, tier, model, rung, round, RefusedClass,
                $"{why} The ruler ({ruler}) asked for a correction that is refused, so nothing changed: {refusal}.", ruler);
            return NoRuling;
        }

        string root = Path.GetFullPath(plan.WorkingDirectory!);
        var writes = new List<(string Full, byte[] Bytes, byte[] Original, LineChange Change)>();
        foreach (TestFileText file in verdict.Files)
        {
            string full = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            TestFileText snapshot = ownTests.First(candidate => string.Equals(candidate.Path, file.Path, StringComparison.Ordinal));
            byte[] original;
            try
            {
                original = File.ReadAllBytes(full);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                RecordRuling(plan, step, attempt, tier, model, rung, round, "Unavailable",
                    $"{why} The test {file.Path} could not be read again ({exception.Message}), so nothing changed.", ruler);
                return NoRuling;
            }

            string existing = Encoding.UTF8.GetString(original).TrimStart('﻿');
            if (existing != snapshot.Text.TrimStart('﻿'))
            {
                RecordRuling(plan, step, attempt, tier, model, rung, round, RefusedClass,
                    $"{why} The test {file.Path} changed while the ruler was working, so the correction was not written and nothing changed.", ruler);
                return NoRuling;
            }

            string lineEnd = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string text = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", lineEnd, StringComparison.Ordinal);
            byte[] encoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
            bool bom = original is [0xEF, 0xBB, 0xBF, ..];
            writes.Add((full, bom ? [0xEF, 0xBB, 0xBF, .. encoded] : encoded, original, LineDiff.Compute(existing, text)));
        }

        var written = new List<(string Full, byte[] Original)>();
        try
        {
            foreach (var write in writes)
            {
                File.WriteAllBytes(write.Full, write.Bytes);
                written.Add((write.Full, write.Original));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            foreach ((string full, byte[] original) in written)
            {
                File.WriteAllBytes(full, original);
            }

            RecordRuling(plan, step, attempt, tier, model, rung, round, "Unavailable",
                $"{why} The corrected test could not be written ({exception.Message}), so nothing changed.", ruler);
            return NoRuling;
        }

        (bool passesNow, string? undoneBecause) = await CheckCorrectionAsync(plan, step, items, cancellationToken);
        if (undoneBecause is not null)
        {
            foreach ((string full, byte[] original) in written)
            {
                File.WriteAllBytes(full, original);
            }

            RecordRuling(plan, step, attempt, tier, model, rung, round, UndoneClass,
                $"{why} The ruler ({ruler}) ruled the test wrong, but {undoneBecause}, so the correction was undone and the test is as the step wrote it.",
                ruler);
            return NoRuling;
        }

        var diff = new StringBuilder();
        foreach ((TestFileText file, var write) in verdict.Files.Zip(writes))
        {
            diff.Append(file.Path).Append('\n');
            foreach (string line in write.Change.Removed) diff.Append("- ").Append(line).Append('\n');
            foreach (string line in write.Change.Added) diff.Append("+ ").Append(line).Append('\n');
        }

        string names = string.Join(", ", verdict.Files.Select(file => file.Path));
        string detail = JsonSerializer.Serialize(new { verdict = TestWrongClass, ruler, ownTest = true, why, files = names, diff = Cut(diff.ToString().TrimEnd(), 3000) },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        RecordRuling(plan, step, attempt, tier, model, rung, round, TestWrongClass, detail, ruler);
        string then = passesNow
            ? "With the corrected test the step's check passes now: keep it as it is and change nothing else the check does not ask for."
            : "Keep the corrected test as it is and make the code pass it.";
        return new TestRulingOutcome(
            $"An independent model ruled that the failing test this step wrote was wrong and corrected it: {names}. {then}", true);
    }

    /// <summary>
    /// Runs the step's check on the hub copy right after a correction was written, before any model round. Measured: one correction
    /// left the same assertion failing and the step lost 20 minutes before a second ruling; another removed an import a passing test
    /// still used. A correction is kept when the check passes, or when it fails on fewer of the same tests and on no new one. Returns
    /// why it must be put back otherwise; a check that cannot run keeps the correction, as before this check existed.
    /// </summary>
    private async Task<(bool PassesNow, string? UndoBecause)> CheckCorrectionAsync(PlanRecord plan, PlanStep step, IReadOnlyList<string> before,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.Verify))
        {
            return (false, null);
        }

        (bool passed, string output) checkedRun;
        try
        {
            using (WorkerWorkspaceContext.Suspend())
            {
                checkedRun = await _tools.RunRepairCommandAsync(plan, step.Verify, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the check after a test correction could not run.", plan.Id, step.Id);
            return (false, null);
        }

        (bool passed, string output) = checkedRun;
        if (passed)
        {
            return (true, null);
        }

        if (CommandMissing(output))
        {
            return (false, null);
        }

        IReadOnlyList<string> after = TestDispute.FailingItems(output);
        string[] added = after.Where(item => !before.Contains(item, StringComparer.Ordinal)).ToArray();
        if (added.Length > 0)
        {
            return (false, $"with its correction the check fails on tests that did not fail before ({string.Join(", ", added.Take(FinalCheckNamesShown))})");
        }

        return after.Count > 0 && after.Count < before.Count
            ? (false, null)
            : (false, $"with its correction the check still fails: {Cut(output, FinalCheckDetailCharacters)}");
    }
}
