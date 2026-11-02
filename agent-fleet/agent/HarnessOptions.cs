using System.Globalization;

namespace AgentFleet;

/// <summary>
/// The switches and limits of the repair-loop hardening, so a run can be compared with and without each part. Every behaviour
/// sits behind one toggle, on unless a setting turns it off, and the options a run used are written to its run log at the start.
/// A setting is read from the environment as FLEET_HARNESS_ followed by the name in capitals with underscores, for example
/// FLEET_HARNESS_ENABLE_FILE_GUARD=false or FLEET_HARNESS_MAX_CALLS_REPAIR=12. A toggle is wired when the part it switches exists;
/// until then it is read and logged but changes nothing.
/// </summary>
internal sealed record HarnessOptions(
    bool EnableStepSplitter = true,
    bool EnableFileGuard = true,
    bool EnableContextPacket = true,
    bool EnableRevertNotes = true,
    bool EnableBudgetFilter = true,
    bool EnableArbiter = true,
    bool EnableRepartition = true,
    bool EnableReviewGate = true,
    bool ReviewGateBlocks = false,
    int MaxCallsInitial = 25,
    int MaxCallsRepair = 10,
    int NudgeAfterReads = 4,
    int EndAfterReads = 6,
    int TestFixMaxCalls = 10,
    int NoProgressRoundsBeforeReset = 2,
    int MaxActRoundsPerStep = 6,
    int MaxPacketTokens = 24000,
    int OllamaNumCtx = 32768,
    double StructuredCallTemperature = 0,
    int UntouchedSendBacks = 2,
    int ArbiterAfterRounds = 2,
    bool EnableWebLookup = true,
    int WebLookupAfterFailures = 2)
{
    private const string Prefix = "FLEET_HARNESS_";

    /// <summary>The options from the environment or configuration. A missing or unreadable value keeps its default.</summary>
    public static HarnessOptions FromSettings(Func<string, string?> setting)
    {
        var defaults = new HarnessOptions();
        bool Flag(string name, bool fallback) => bool.TryParse(setting(Prefix + name), out bool value) ? value : fallback;
        int Number(string name, int fallback, int minimum, int maximum) =>
            int.TryParse(setting(Prefix + name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= minimum && value <= maximum ? value : fallback;

        return new HarnessOptions(
            Flag("ENABLE_STEP_SPLITTER", defaults.EnableStepSplitter),
            Flag("ENABLE_FILE_GUARD", defaults.EnableFileGuard),
            Flag("ENABLE_CONTEXT_PACKET", defaults.EnableContextPacket),
            Flag("ENABLE_REVERT_NOTES", defaults.EnableRevertNotes),
            Flag("ENABLE_BUDGET_FILTER", defaults.EnableBudgetFilter),
            Flag("ENABLE_ARBITER", defaults.EnableArbiter),
            Flag("ENABLE_REPARTITION", defaults.EnableRepartition),
            Flag("ENABLE_REVIEW_GATE", defaults.EnableReviewGate),
            Flag("REVIEW_GATE_BLOCKS", defaults.ReviewGateBlocks),
            Number("MAX_CALLS_INITIAL", defaults.MaxCallsInitial, 1, 200),
            Number("MAX_CALLS_REPAIR", defaults.MaxCallsRepair, 1, 200),
            Number("NUDGE_AFTER_READS", defaults.NudgeAfterReads, 1, 100),
            Number("END_AFTER_READS", defaults.EndAfterReads, 1, 100),
            Number("TEST_FIX_MAX_CALLS", defaults.TestFixMaxCalls, 1, 200),
            Number("NO_PROGRESS_ROUNDS_BEFORE_RESET", defaults.NoProgressRoundsBeforeReset, 1, 20),
            Number("MAX_ACT_ROUNDS_PER_STEP", defaults.MaxActRoundsPerStep, 1, 50),
            Number("MAX_PACKET_TOKENS", defaults.MaxPacketTokens, 1000, 200000),
            Number("OLLAMA_NUM_CTX", defaults.OllamaNumCtx, 2048, 1048576),
            double.TryParse(setting(Prefix + "STRUCTURED_CALL_TEMPERATURE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature) && temperature is >= 0 and <= 2
                ? temperature
                : defaults.StructuredCallTemperature,
            Number("UNTOUCHED_SEND_BACKS", defaults.UntouchedSendBacks, 1, 50),
            Number("ARBITER_AFTER_ROUNDS", defaults.ArbiterAfterRounds, 1, 50),
            Flag("ENABLE_WEB_LOOKUP", defaults.EnableWebLookup),
            Number("WEB_LOOKUP_AFTER_FAILURES", defaults.WebLookupAfterFailures, 1, 50));
    }

    /// <summary>One line for the run log: every option and its value.</summary>
    public string Describe() =>
        string.Join(", ", new (string Name, object Value)[]
        {
            (nameof(EnableStepSplitter), EnableStepSplitter), (nameof(EnableFileGuard), EnableFileGuard), (nameof(EnableContextPacket), EnableContextPacket),
            (nameof(EnableRevertNotes), EnableRevertNotes), (nameof(EnableBudgetFilter), EnableBudgetFilter), (nameof(EnableArbiter), EnableArbiter),
            (nameof(EnableRepartition), EnableRepartition), (nameof(EnableReviewGate), EnableReviewGate), (nameof(ReviewGateBlocks), ReviewGateBlocks),
            (nameof(MaxCallsInitial), MaxCallsInitial), (nameof(MaxCallsRepair), MaxCallsRepair), (nameof(NudgeAfterReads), NudgeAfterReads),
            (nameof(EndAfterReads), EndAfterReads), (nameof(TestFixMaxCalls), TestFixMaxCalls),
            (nameof(NoProgressRoundsBeforeReset), NoProgressRoundsBeforeReset), (nameof(MaxActRoundsPerStep), MaxActRoundsPerStep),
            (nameof(MaxPacketTokens), MaxPacketTokens), (nameof(OllamaNumCtx), OllamaNumCtx),
            (nameof(StructuredCallTemperature), StructuredCallTemperature.ToString(CultureInfo.InvariantCulture)),
            (nameof(UntouchedSendBacks), UntouchedSendBacks), (nameof(ArbiterAfterRounds), ArbiterAfterRounds),
            (nameof(EnableWebLookup), EnableWebLookup), (nameof(WebLookupAfterFailures), WebLookupAfterFailures)
        }.Select(option => $"{option.Name}={(option.Value is bool flag ? (flag ? "on" : "off") : option.Value)}"));
}
