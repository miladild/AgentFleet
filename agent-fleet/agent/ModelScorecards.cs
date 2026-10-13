namespace AgentFleet;

internal sealed record ModelScorecard(
    string? Node,
    string Model,
    int StepsWorked,
    int StepsDoneOnOwn,
    int RescuedElsewhere,
    IReadOnlyDictionary<string, int> RescuedByNode,
    int Rounds,
    IReadOnlyDictionary<string, int> ParksByFailureSignature,
    int ZeroToolCallRounds,
    int LoopGuardNotes,
    double? MedianRoundSeconds);

internal sealed record ModelScorecardsResult(
    IReadOnlyList<ModelScorecard> Models,
    int InfrastructureRounds,
    IReadOnlyDictionary<string, int> InfrastructureRoundsByNode,
    int UnknownRounds,
    IReadOnlyDictionary<string, int> UnknownRoundsByNode,
    IReadOnlyDictionary<string, int> UnattributedParksByFailureSignature,
    int SkippedButCountedDone,
    IReadOnlyDictionary<string, int> SkippedButCountedDoneByNode,
    int TruncatedPlans);

/// <summary>Counts plan-round results by the machine and model that did the work; it never ranks models.</summary>
internal static class ModelScorecards
{
    public static ModelScorecardsResult Build(IReadOnlyList<PlanRecord> plans, DateTimeOffset sinceUtc, DateTimeOffset? untilUtc = null)
    {
        var models = new Dictionary<ModelKey, Counts>();
        var unattributedParks = new Dictionary<string, int>(StringComparer.Ordinal);
        var infrastructureByNode = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknownByNode = new Dictionary<string, int>(StringComparer.Ordinal);
        var skippedByNode = new Dictionary<string, int>(StringComparer.Ordinal);
        int infrastructureRounds = 0;
        int unknownRounds = 0;
        int skippedButCountedDone = 0;
        int truncatedPlans = plans.Count(plan => (plan.Events?.Count ?? 0) >= FleetPlanStore.MaxEvents);

        foreach (PlanRecord plan in plans)
        {
            PlanRunEvent[] events = [.. plan.Events ?? []];
            foreach (PlanRunEvent round in events.Where(runEvent => runEvent.Kind == RunEventKind.RoundClassified && InWindow(runEvent.AtUtc, sinceUtc, untilUtc)))
            {
                if (string.Equals(round.FailureClass, nameof(FailureClass.Infra), StringComparison.Ordinal))
                {
                    infrastructureRounds++;
                    Increment(infrastructureByNode, NodeName(round.ModelNode ?? round.Node));
                    continue;
                }
                if (string.Equals(round.FailureClass, nameof(FailureClass.Unknown), StringComparison.Ordinal))
                {
                    unknownRounds++;
                    Increment(unknownByNode, NodeName(round.ModelNode ?? round.Node));
                    continue;
                }

                Counts counts = Get(models, Key(round));
                counts.Rounds++;
                if (round.StepId is int stepId) counts.WorkedSteps.Add((plan.Id, stepId));
                if (round.ToolCalls == 0) counts.ZeroToolCallRounds++;
                counts.LoopGuardNotes += Math.Max(0, round.LoopGuardNotes ?? 0);
                if (round.DurationSeconds is int seconds) counts.RoundSeconds.Add(seconds);
            }

            for (int index = 0; index < events.Length; index++)
            {
                PlanRunEvent parked = events[index];
                if (parked.Kind != RunEventKind.StepParked || !InWindow(parked.AtUtc, sinceUtc, untilUtc)) continue;

                string signature = Signature(parked.FailureSignature);
                PlanRunEvent? lastRound = events
                    .Take(index)
                    .LastOrDefault(runEvent => runEvent.StepId == parked.StepId && runEvent.Kind == RunEventKind.RoundClassified);
                if (lastRound is null || IsApart(lastRound))
                {
                    Increment(unattributedParks, signature);
                }
                else
                {
                    Increment(Get(models, Key(lastRound)).ParksByFailureSignature, signature);
                }
            }

            foreach (PlanStep step in plan.Steps.Where(candidate => candidate.Status == StepStatus.Done))
            {
                PlanRunEvent[] stepEvents = events.Where(runEvent => runEvent.StepId == step.Id).ToArray();
                PlanRunEvent? completionEvent = stepEvents.LastOrDefault(runEvent =>
                    runEvent.Kind is RunEventKind.StepDone or RunEventKind.StepSkipped);
                DateTimeOffset? completedAt = step.CompletedUtc ?? completionEvent?.AtUtc;
                if (completedAt is null || !InWindow(completedAt.Value, sinceUtc, untilUtc)) continue;

                bool skipped = stepEvents.Any(runEvent => runEvent.Kind == RunEventKind.StepSkipped) ||
                    step.Note?.StartsWith("Skipped by the user", StringComparison.Ordinal) == true;
                if (skipped)
                {
                    skippedButCountedDone++;
                    int skippedAt = Array.FindLastIndex(events, runEvent =>
                        runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.StepSkipped);
                    PlanRunEvent? lastAttempt = events
                        .Take(skippedAt >= 0 ? skippedAt : events.Length)
                        .LastOrDefault(runEvent => runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.RoundClassified);
                    Increment(skippedByNode, NodeName(lastAttempt?.ModelNode ?? lastAttempt?.Node));
                    continue;
                }

                int winningIndex = Array.FindLastIndex(events, runEvent =>
                    runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.RoundClassified &&
                    string.Equals(runEvent.FailureClass, "Passed", StringComparison.Ordinal));
                if (winningIndex < 0) continue;

                PlanRunEvent winner = events[winningIndex];
                ModelKey winningKey = Key(winner);
                Counts winningCounts = Get(models, winningKey);
                winningCounts.WorkedSteps.Add((plan.Id, step.Id));
                winningCounts.StepsDoneOnOwn++;

                HashSet<ModelKey> otherKeys = events
                    .Take(winningIndex)
                    .Where(runEvent => runEvent.StepId == step.Id && runEvent.Kind == RunEventKind.RoundClassified && !IsApart(runEvent))
                    .Select(Key)
                    .Where(key => key != winningKey)
                    .ToHashSet();
                foreach (ModelKey otherKey in otherKeys)
                {
                    Counts otherCounts = Get(models, otherKey);
                    otherCounts.RescuedElsewhere++;
                    Increment(otherCounts.RescuedByNode, NodeName(winningKey.Node));
                }
            }
        }

        ModelScorecard[] scorecards = models
            .OrderBy(pair => pair.Key.Node, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Model, StringComparer.Ordinal)
            .Select(pair => pair.Value.ToScorecard(pair.Key))
            .ToArray();
        return new ModelScorecardsResult(scorecards, infrastructureRounds, SortedCounts(infrastructureByNode),
            unknownRounds, SortedCounts(unknownByNode), SortedCounts(unattributedParks),
            skippedButCountedDone, SortedCounts(skippedByNode), truncatedPlans);
    }

    public static bool TrySinceUtc(int? days, DateTimeOffset nowUtc, out DateTimeOffset sinceUtc)
    {
        int window = days ?? 7;
        if (window is < 1 or > 90)
        {
            sinceUtc = default;
            return false;
        }

        sinceUtc = nowUtc.AddDays(-window);
        return true;
    }

    private static bool IsApart(PlanRunEvent round) =>
        string.Equals(round.FailureClass, nameof(FailureClass.Infra), StringComparison.Ordinal) ||
        string.Equals(round.FailureClass, nameof(FailureClass.Unknown), StringComparison.Ordinal);

    private static bool InWindow(DateTimeOffset atUtc, DateTimeOffset sinceUtc, DateTimeOffset? untilUtc) =>
        atUtc >= sinceUtc && (untilUtc is null || atUtc < untilUtc.Value);

    private static string NodeName(string? value) => string.IsNullOrWhiteSpace(value) ? "not recorded" : value;

    private static ModelKey Key(PlanRunEvent round) => new(
        string.IsNullOrWhiteSpace(round.ModelNode ?? round.Node) ? null : round.ModelNode ?? round.Node,
        string.IsNullOrWhiteSpace(round.Model) ? "not recorded" : round.Model);

    private static Counts Get(Dictionary<ModelKey, Counts> counts, ModelKey key)
    {
        if (!counts.TryGetValue(key, out Counts? value)) counts.Add(key, value = new Counts());
        return value;
    }

    private static string Signature(string? value) => string.IsNullOrWhiteSpace(value) ? "not recorded" : value;

    private static void Increment(Dictionary<string, int> values, string key) =>
        values[key] = values.GetValueOrDefault(key) + 1;

    private static IReadOnlyDictionary<string, int> SortedCounts(Dictionary<string, int> counts) =>
        counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private readonly record struct ModelKey(string? Node, string Model);

    private sealed class Counts
    {
        public HashSet<(string PlanId, int StepId)> WorkedSteps { get; } = [];
        public int StepsDoneOnOwn { get; set; }
        public int RescuedElsewhere { get; set; }
        public Dictionary<string, int> RescuedByNode { get; } = new(StringComparer.Ordinal);
        public int Rounds { get; set; }
        public Dictionary<string, int> ParksByFailureSignature { get; } = new(StringComparer.Ordinal);
        public int ZeroToolCallRounds { get; set; }
        public int LoopGuardNotes { get; set; }
        public List<int> RoundSeconds { get; } = [];

        public ModelScorecard ToScorecard(ModelKey key)
        {
            int[] ordered = [.. RoundSeconds.Order()];
            double? median = ordered.Length switch
            {
                0 => null,
                var length when length % 2 == 1 => ordered[length / 2],
                var length => (ordered[length / 2 - 1] + (double)ordered[length / 2]) / 2,
            };
            return new ModelScorecard(key.Node, key.Model, WorkedSteps.Count, StepsDoneOnOwn, RescuedElsewhere,
                SortedCounts(RescuedByNode), Rounds, SortedCounts(ParksByFailureSignature), ZeroToolCallRounds,
                LoopGuardNotes, median);
        }
    }
}
