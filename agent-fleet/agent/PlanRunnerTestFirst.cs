using System.Text.Json;

namespace AgentFleet;

internal sealed partial class PlanRunner
{
    private bool IsTestsStep(PlanStep step) => _testFirstMode == TestFirstMode.On && step.Kind == "tests";

    private StepCompletion DecideTestsStep(PlanRecord plan, PlanStep step, int attempt, string tier, int rung, int round,
        ModelAttemptResult model, StepCompletion check, IReadOnlyList<string>? changedFiles)
    {
        TestFirstDecision decision = TestFirst.Evaluate(check.Output, changedFiles);
        _store.AddEvent(plan.Id, step.Id, attempt, RunEventKind.TestFirst, tier, node: model.ModelNode,
            detail: JsonSerializer.Serialize(new { outcome = decision.Outcome, executed = decision.Executed, failing = decision.Failing,
                changedFiles = changedFiles?.Count }),
            modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
        return new StepCompletion(decision.Accepted, decision.Feedback,
            decision.Accepted ? check.Output : decision.Feedback + "\n\n" + check.Output, check.Restored);
    }
}
