namespace AgentFleet.Tests;

public sealed class ModelScorecardsTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_last_passing_key_owns_a_done_step_and_the_other_key_is_rescued_elsewhere()
    {
        PlanRecord plan = Plan(
        [
            Round(WindowStart.AddMinutes(1), 1, "node-a.example.test", "model-a", "NoOp"),
            Round(WindowStart.AddMinutes(2), 1, "node-b.example.test", "model-b", "Passed", rung: 2),
            Event(WindowStart.AddMinutes(3), 1, RunEventKind.StepDone),
        ], [Step(1, "done", WindowStart.AddMinutes(3))]);

        ModelScorecardsResult result = ModelScorecards.Build([plan], WindowStart);

        ModelScorecard winner = Find(result, "node-b.example.test", "model-b");
        ModelScorecard rescued = Find(result, "node-a.example.test", "model-a");
        Assert.Equal(1, winner.StepsDoneOnOwn);
        Assert.Equal(0, winner.RescuedElsewhere);
        Assert.Equal(1, rescued.RescuedElsewhere);
        Assert.Equal(0, rescued.StepsDoneOnOwn);
    }

    [Fact]
    public void A_round_without_a_recorded_model_uses_the_not_recorded_key()
    {
        PlanRecord plan = Plan([Round(WindowStart.AddMinutes(1), 1, "node-a.example.test", null, "CodeProgress")]);

        ModelScorecard scorecard = Assert.Single(ModelScorecards.Build([plan], WindowStart).Models);

        Assert.Equal("node-a.example.test", scorecard.Node);
        Assert.Equal("not recorded", scorecard.Model);
    }

    [Fact]
    public void Infrastructure_and_unknown_rounds_are_counted_apart_from_models()
    {
        PlanRecord plan = Plan(
        [
            Round(WindowStart.AddMinutes(1), 1, "node-a.example.test", "model-a", "Infra"),
            Round(WindowStart.AddMinutes(2), 1, "node-b.example.test", "model-b", "Unknown"),
            Round(WindowStart.AddMinutes(3), 1, "node-c.example.test", "model-c", "CodeProgress"),
        ]);

        ModelScorecardsResult result = ModelScorecards.Build([plan], WindowStart);

        Assert.Equal(1, result.InfrastructureRounds);
        Assert.Equal(1, result.UnknownRounds);
        Assert.Equal(1, Assert.Single(result.Models).Rounds);
        Assert.Equal("model-c", result.Models[0].Model);
    }

    [Fact]
    public void A_park_is_grouped_by_signature_on_the_last_round_key()
    {
        PlanRecord plan = Plan(
        [
            Round(WindowStart.AddMinutes(1), 1, "node-a.example.test", "model-a", "NoOp"),
            Round(WindowStart.AddMinutes(2), 1, "node-b.example.test", "model-b", "Stall"),
            Event(WindowStart.AddMinutes(3), 1, RunEventKind.StepParked, failureClass: "Parked", failureSignature: "no-progress"),
            Round(WindowStart.AddMinutes(4), 2, "node-c.example.test", "model-c", "Infra"),
            Event(WindowStart.AddMinutes(5), 2, RunEventKind.StepParked, failureClass: "Parked", failureSignature: "worker-unavailable"),
        ]);

        ModelScorecardsResult result = ModelScorecards.Build([plan], WindowStart);

        Assert.Equal(1, Find(result, "node-b.example.test", "model-b").ParksByFailureSignature["no-progress"]);
        Assert.Empty(Find(result, "node-a.example.test", "model-a").ParksByFailureSignature);
        Assert.Equal(1, result.UnattributedParksByFailureSignature["worker-unavailable"]);
    }

    [Fact]
    public void Scorecards_count_zero_calls_guard_notes_and_only_checked_round_durations()
    {
        PlanRecord plan = Plan(
        [
            Round(WindowStart.AddMinutes(1), 1, "node-a.example.test", "model-a", "NoOp", toolCalls: 0, duration: 10, notes: 2),
            Round(WindowStart.AddMinutes(2), 1, "node-a.example.test", "model-a", "CodeProgress", toolCalls: 1, duration: 20),
            Round(WindowStart.AddMinutes(3), 1, "node-a.example.test", "model-a", "NoOp", toolCalls: 0),
        ]);

        ModelScorecard scorecard = Assert.Single(ModelScorecards.Build([plan], WindowStart).Models);

        Assert.Equal(3, scorecard.Rounds);
        Assert.Equal(2, scorecard.ZeroToolCallRounds);
        Assert.Equal(2, scorecard.LoopGuardNotes);
        Assert.Equal(15, scorecard.MedianRoundSeconds);
    }

    [Fact]
    public void A_skipped_step_is_counted_separately_from_model_work()
    {
        DateTimeOffset completed = WindowStart.AddMinutes(2);
        PlanRecord plan = Plan(
            [Event(completed, 1, RunEventKind.StepSkipped)],
            [Step(1, "done", completed, "Skipped by the user: handled manually.")]);

        ModelScorecardsResult result = ModelScorecards.Build([plan], WindowStart);

        Assert.Equal(1, result.SkippedButCountedDone);
        Assert.Empty(result.Models);
    }

    [Fact]
    public void A_plan_at_the_event_cap_is_reported_as_truncated()
    {
        PlanRunEvent[] events = Enumerable.Range(0, FleetPlanStore.MaxEvents)
            .Select(index => Event(WindowStart.AddSeconds(index), null, RunEventKind.RunStarted))
            .ToArray();
        PlanRecord plan = Plan(events);

        Assert.Equal(1, ModelScorecards.Build([plan], WindowStart).TruncatedPlans);
    }

    [Fact]
    public void The_days_window_filters_rounds_and_step_completions()
    {
        DateTimeOffset before = WindowStart.AddDays(1);
        DateTimeOffset since = WindowStart.AddDays(2);
        DateTimeOffset after = WindowStart.AddDays(3);
        PlanRecord plan = Plan(
        [
            Round(before, 1, "node-a.example.test", "model-a", "Passed"),
            Round(after, 2, "node-a.example.test", "model-a", "Passed"),
            Event(after, 2, RunEventKind.StepDone),
        ],
        [
            Step(1, "done", before),
            Step(2, "done", after),
        ]);

        ModelScorecard scorecard = Assert.Single(ModelScorecards.Build([plan], since).Models);

        Assert.Equal(1, scorecard.Rounds);
        Assert.Equal(1, scorecard.StepsDoneOnOwn);
        Assert.True(ModelScorecards.TrySinceUtc(null, WindowStart, out DateTimeOffset defaultSince));
        Assert.Equal(WindowStart.AddDays(-7), defaultSince);
        Assert.True(ModelScorecards.TrySinceUtc(1, WindowStart, out DateTimeOffset oneDaySince));
        Assert.Equal(WindowStart.AddDays(-1), oneDaySince);
        Assert.True(ModelScorecards.TrySinceUtc(90, WindowStart, out _));
        Assert.False(ModelScorecards.TrySinceUtc(0, WindowStart, out _));
        Assert.False(ModelScorecards.TrySinceUtc(91, WindowStart, out _));
    }

    [Fact]
    public void A_step_completed_in_the_window_counts_as_work_even_when_its_last_round_was_earlier()
    {
        DateTimeOffset since = WindowStart.AddDays(7);
        DateTimeOffset completed = since.AddMinutes(1);
        PlanRecord plan = Plan(
        [
            Round(WindowStart.AddDays(6), 1, "node-a.example.test", "model-a", "Passed"),
            Event(completed, 1, RunEventKind.StepDone),
        ], [Step(1, "done", completed)]);

        ModelScorecard scorecard = Assert.Single(ModelScorecards.Build([plan], since, since.AddDays(7)).Models);

        Assert.Equal(1, scorecard.StepsWorked);
        Assert.Equal(1, scorecard.StepsDoneOnOwn);
    }

    private static ModelScorecard Find(ModelScorecardsResult result, string node, string model) =>
        Assert.Single(result.Models, scorecard => scorecard.Node == node && scorecard.Model == model);

    private static PlanRecord Plan(IReadOnlyList<PlanRunEvent> events, IReadOnlyList<PlanStep>? steps = null) =>
        new("dddddddddddddddddddddddddddddddd", "Scorecard fixture", "Count fixture events.", "running", null,
            [], [], [], null, null, steps ?? [], WindowStart, WindowStart, null, events);

    private static PlanStep Step(int id, string status, DateTimeOffset? completed, string? note = null) =>
        new(id, "Step " + id, "", [], null, "standard", status, note, 1, null, completed);

    private static PlanRunEvent Round(
        DateTimeOffset at,
        int step,
        string? node,
        string? model,
        string failureClass,
        int? toolCalls = 1,
        int? duration = null,
        int? notes = null,
        int? rung = 1) =>
        new(at, step, 1, RunEventKind.RoundClassified, "standard", node, "fixture round",
            ModelNode: node, FailureClass: failureClass, ToolCalls: toolCalls, Rung: rung,
            DurationSeconds: duration, Model: model, LoopGuardNotes: notes);

    private static PlanRunEvent Event(
        DateTimeOffset at,
        int? step,
        string kind,
        string? failureClass = null,
        string? failureSignature = null) =>
        new(at, step, null, kind, null, null, "fixture event", FailureClass: failureClass,
            FailureSignature: failureSignature);
}
