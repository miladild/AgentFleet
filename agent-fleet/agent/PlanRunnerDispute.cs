using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentFleet;

/// <summary>
/// What a ruling changed for the step: a note for the next round's feedback, and whether a frozen test was corrected (which
/// gives the step's rung its rounds back).
/// </summary>
internal sealed record TestRulingOutcome(string? Note, bool TestCorrected);

/// <summary>
/// A step's model may say that a frozen test is wrong (dispute_test). When the failing check shows only frozen tests fail, an
/// independent model rules on it. A correction is written to the hub copy only when TestRuling.Guard accepts it, and every
/// ruling is logged with both hashes. A ruling that cannot be obtained changes nothing and never blocks the step.
/// </summary>
internal sealed partial class PlanRunner
{
    private static readonly TimeSpan RulingTimeLimit = TimeSpan.FromMinutes(8);
    private const string TestWrongClass = nameof(TestRulingKind.TestWrong);
    private const string CodeWrongClass = nameof(TestRulingKind.CodeWrong);
    private const string NoVerdictClass = nameof(TestRulingKind.NoVerdict);
    private const string RefusedClass = "Refused";
    private static readonly TestRulingOutcome NoRuling = new(null, false);

    private void RecordDispute(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model, int rung, int round,
        TestDisputeCall dispute)
    {
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.TestDisputed, tier, node: model.ModelNode ?? model.Machine,
            detail: Cut($"Tests: {dispute.Tests}. Reason: {dispute.Reason}", 1000),
            modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
    }

    private void RecordRuling(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model, int rung, int round,
        string verdict, string detail, string? node = null)
    {
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.TestRuling, tier, node: node ?? model.ModelNode ?? model.Machine,
            detail: Cut(detail, 4000), failureClass: verdict,
            modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
    }

    private async Task<TestRulingOutcome> RuleOnDisputeAsync(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model,
        IReadOnlyList<string> changedFiles, string checkOutput, int rung, int round, CancellationToken cancellationToken)
    {
        PlanRecord latest = _store.Get(plan.Id) ?? plan;
        IReadOnlyList<TestFileText> frozen = ReadFrozenTestFiles(latest);
        PlanRunEvent[] earlierRulings = StepEventsSinceRetry(plan.Id, step.Id).Where(runEvent => runEvent.Kind == RunEventKind.TestRuling).ToArray();
        int rulingsMade = earlierRulings.Count(runEvent => runEvent.FailureClass is TestWrongClass or CodeWrongClass);
        TestDisputeCall? dispute = model.Dispute;
        int unresolvedAsks = earlierRulings.Count(runEvent => runEvent.FailureClass is NoVerdictClass or RefusedClass);
        TestRulingDue due = TestDispute.Evaluate(_testFirstMode == TestFirstMode.On, !IsTestsStep(step), latest.FrozenTests is { Count: > 0 },
            dispute is not null, rulingsMade, unresolvedAsks, checkOutput, frozen);
        if (dispute is null || !due.Due)
        {
            if (dispute is not null)
            {
                RecordRuling(plan, step, attempt, tier, model, rung, round, "NotDue", $"The disputed test was not ruled on: {due.Reason}.");
            }

            return NoRuling;
        }

        (string reviewerTier, string? ruler, string? unavailable) = ChooseReviewer(latest, step, model);
        if (ruler is null)
        {
            RecordRuling(plan, step, attempt, tier, model, rung, round, "Unavailable",
                $"No independent model is available to rule on the disputed test ({unavailable}); nothing changes.");
            return NoRuling;
        }

        IReadOnlyList<string> items = TestDispute.FailingItems(checkOutput);
        IReadOnlyList<TestFileText> changed = ReadFilesToReview(latest, step, changedFiles).Select(file => new TestFileText(file.Path, file.Content)).ToArray();
        string prompt = TestRuling.BuildPrompt(latest, step, TestDispute.Holding(frozen, items), checkOutput, dispute.Reason, changed);
        IStepSession session = _agent.OpenSession(PlanRunnerToolPolicy.TestRulingRole);
        StepAgentReply reply;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(RulingTimeLimit);
            reply = await session.SendAsync(prompt, reviewerTier, ruler, limit.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            string detail = exception is OperationCanceledException
                ? $"The ruler ({ruler}) did not answer within {Duration(RulingTimeLimit)}; nothing changes."
                : $"The ruler ({ruler}) could not be reached: {exception.Message} Nothing changes.";
            _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the test ruling failed.", plan.Id, step.Id);
            RecordRuling(plan, step, attempt, tier, model, rung, round, "Unavailable", detail, ruler);
            return NoRuling;
        }

        TestRulingVerdict verdict = TestRuling.Parse(reply.Text);
        switch (verdict.Kind)
        {
            case TestRulingKind.CodeWrong:
                RecordRuling(plan, step, attempt, tier, model, rung, round, CodeWrongClass,
                    $"The ruler ({ruler}) found the test right: {Summarize(verdict.Paragraph)}", ruler);
                return new TestRulingOutcome($"An independent model read the disputed test and found it right: {verdict.Paragraph}", false);
            case TestRulingKind.TestWrong:
                return ApplyCorrection(latest, step, attempt, tier, model, rung, round, verdict, frozen, items, ruler);
            default:
                RecordRuling(plan, step, attempt, tier, model, rung, round, nameof(TestRulingKind.NoVerdict),
                    $"The ruler ({ruler}) gave no TEST-WRONG or CODE-WRONG verdict: {Summarize(reply.Text)} Nothing changes.", ruler);
                return NoRuling;
        }
    }

    // The correction is written to the hub copy (the accepted one) and its hash recorded, and only then does the step continue
    // against it. Every file is checked before the first write; a write that fails puts back the files already written.
    private TestRulingOutcome ApplyCorrection(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model, int rung,
        int round, TestRulingVerdict verdict, IReadOnlyList<TestFileText> frozen, IReadOnlyList<string> items, string ruler)
    {
        string? refusal = TestRuling.Guard(frozen, verdict.Files, items);
        if (refusal is not null)
        {
            RecordRuling(plan, step, attempt, tier, model, rung, round, "Refused",
                $"The ruler ({ruler}) asked for a correction that is refused, so nothing changed: {refusal}.", ruler);
            return NoRuling;
        }

        string root = Path.GetFullPath(plan.WorkingDirectory!);
        var writes = new List<(string Path, int StepId, string Full, string Before, byte[] Bytes, byte[]? Original, LineChange Change)>();
        foreach (TestFileText file in verdict.Files)
        {
            (int stepId, string before) = FrozenHashOf(plan, file.Path);
            string full = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                RecordRuling(plan, step, attempt, tier, model, rung, round, "Refused",
                    $"The ruler ({ruler}) named a file outside the project, so nothing changed.", ruler);
                return NoRuling;
            }

            string existing = File.Exists(full) ? File.ReadAllText(full) : string.Empty;
            // The ruler read the file when the ruling began; a file changed since then is not the one the correction was made for.
            if (frozen.FirstOrDefault(snapshot => string.Equals(snapshot.Path, file.Path, StringComparison.Ordinal)) is { } snapshot &&
                snapshot.Text != existing)
            {
                RecordRuling(plan, step, attempt, tier, model, rung, round, "Refused",
                    $"The frozen test {file.Path} changed while the ruler was working, so the correction was not written and nothing changed.", ruler);
                return NoRuling;
            }

            string lineEnd = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string text = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", lineEnd, StringComparison.Ordinal);
            byte[]? original = File.Exists(full) ? File.ReadAllBytes(full) : null;
            // A file that starts with a byte order mark keeps it, as the file it replaces does.
            byte[] encoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
            bool bom = original is { Length: >= 3 } && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
            byte[] bytes = bom ? [0xEF, 0xBB, 0xBF, .. encoded] : encoded;
            writes.Add((file.Path, stepId, full, before, bytes, original, LineDiff.Compute(existing, text)));
        }

        var written = new List<(string Full, byte[]? Original)>();
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
            foreach ((string full, byte[]? original) in written)
            {
                if (original is not null) File.WriteAllBytes(full, original);
            }

            RecordRuling(plan, step, attempt, tier, model, rung, round, "Unavailable",
                $"The corrected test could not be written ({exception.Message}), so nothing changed.", ruler);
            return NoRuling;
        }

        var amended = new List<object>();
        var diff = new StringBuilder();
        foreach (var write in writes)
        {
            // The corrected bytes are kept before the hash that names them is recorded.
            string after = _store.SaveFrozenContent(plan.Id, write.Bytes);
            _store.AmendFrozenFile(plan.Id, write.StepId, write.Path, after);
            amended.Add(new { path = write.Path, before = write.Before, after });
            diff.Append(write.Path).Append('\n');
            foreach (string line in write.Change.Removed) diff.Append("- ").Append(line).Append('\n');
            foreach (string line in write.Change.Added) diff.Append("+ ").Append(line).Append('\n');
        }

        // The hashes come first in the detail, so a cut for length takes only the diff at its end.
        // The relaxed encoder keeps the changed lines readable in the log (a plus sign is not written as an escape).
        string detail = JsonSerializer.Serialize(new { verdict = TestWrongClass, ruler, files = amended, diff = Cut(diff.ToString().TrimEnd(), 3000) },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        RecordRuling(plan, step, attempt, tier, model, rung, round, TestWrongClass, detail, ruler);
        string names = string.Join(", ", verdict.Files.Select(file => file.Path));
        return new TestRulingOutcome(
            $"These fixed test files were corrected after an independent model ruled that the test was wrong, and the step continues against the corrected tests: {names}.",
            true);
    }

    // The step that fixed the file and the hash it was accepted with (the hash the store holds now).
    private static (int StepId, string Sha256) FrozenHashOf(PlanRecord plan, string path)
    {
        foreach (PlanFrozenTestSet set in plan.FrozenTests ?? [])
        {
            PlanFrozenTestFile? file = set.Files.FirstOrDefault(candidate => string.Equals(candidate.Path, path, StringComparison.Ordinal));
            if (file is not null) return (set.StepId, file.Sha256);
        }

        return (0, string.Empty);
    }

    // The frozen test files as the hub copy holds them. A file that cannot be read is left out, so a ruling never sees a partial set.
    private static IReadOnlyList<TestFileText> ReadFrozenTestFiles(PlanRecord plan)
    {
        var files = new List<TestFileText>();
        if (plan.FrozenTests is null || string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return files;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.WorkingDirectory));
        foreach (PlanFrozenTestFile frozen in plan.FrozenTests.SelectMany(set => set.Files))
        {
            string full = Path.GetFullPath(Path.Combine(root, frozen.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                continue;
            }

            try
            {
                files.Add(new TestFileText(frozen.Path, File.ReadAllText(full)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return files;
    }

    // The names of the failing frozen tests when a step ended without a passing check, its model disputed a frozen test, no
    // correction was applied for the step, no ruler found the test right, and the failures are all frozen tests; otherwise
    // null. Same evaluation as a ruling, without the setting and the ruling count, which are the caller's concern.
    private string? SuspectTestNames(PlanRecord plan, PlanStep step, string? lastCheckOutput)
    {
        PlanRunEvent[] earlier = StepEventsSinceRetry(plan.Id, step.Id);
        if (!earlier.Any(runEvent => runEvent.Kind == RunEventKind.TestDisputed) ||
            earlier.Any(runEvent => runEvent.Kind == RunEventKind.TestRuling && runEvent.FailureClass is TestWrongClass or CodeWrongClass))
        {
            return null;
        }

        PlanRecord latest = _store.Get(plan.Id) ?? plan;
        IReadOnlyList<TestFileText> frozen = ReadFrozenTestFiles(latest);
        if (!TestDispute.Evaluate(testFirstOn: true, ordinaryStep: true, hasFrozenFiles: true, disputed: true, rulingsMade: 0,
                unresolvedAsks: 0, lastCheckOutput, frozen).Due)
        {
            return null;
        }

        return string.Join(", ", TestDispute.FailingItems(lastCheckOutput).Take(5));
    }

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..limit];
}
