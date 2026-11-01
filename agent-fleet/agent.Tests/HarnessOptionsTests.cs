namespace AgentFleet.Tests;

public sealed class HarnessOptionsTests
{
    [Fact]
    public void Without_settings_every_part_is_on_except_the_blocking_review_gate_and_the_limits_are_the_defaults()
    {
        HarnessOptions options = HarnessOptions.FromSettings(_ => null);

        Assert.True(options.EnableStepSplitter && options.EnableFileGuard && options.EnableContextPacket && options.EnableRevertNotes &&
                    options.EnableBudgetFilter && options.EnableArbiter && options.EnableRepartition && options.EnableReviewGate);
        Assert.False(options.ReviewGateBlocks);
        Assert.Equal((25, 10, 4, 6, 10, 2, 6, 24000, 32768, 0d),
            (options.MaxCallsInitial, options.MaxCallsRepair, options.NudgeAfterReads, options.EndAfterReads, options.TestFixMaxCalls,
             options.NoProgressRoundsBeforeReset, options.MaxActRoundsPerStep, options.MaxPacketTokens, options.OllamaNumCtx, options.StructuredCallTemperature));
    }

    [Fact]
    public void A_setting_turns_one_part_off_or_changes_one_limit_and_a_bad_value_keeps_the_default()
    {
        var settings = new Dictionary<string, string?>
        {
            ["FLEET_HARNESS_ENABLE_FILE_GUARD"] = "false",
            ["FLEET_HARNESS_REVIEW_GATE_BLOCKS"] = "true",
            ["FLEET_HARNESS_MAX_CALLS_REPAIR"] = "12",
            ["FLEET_HARNESS_NUDGE_AFTER_READS"] = "zero",
            ["FLEET_HARNESS_END_AFTER_READS"] = "0",
            ["FLEET_HARNESS_STRUCTURED_CALL_TEMPERATURE"] = "0.2"
        };

        HarnessOptions options = HarnessOptions.FromSettings(name => settings.GetValueOrDefault(name));

        Assert.False(options.EnableFileGuard);
        Assert.True(options.EnableArbiter);
        Assert.True(options.ReviewGateBlocks);
        Assert.Equal(12, options.MaxCallsRepair);
        Assert.Equal(4, options.NudgeAfterReads);
        Assert.Equal(6, options.EndAfterReads);
        Assert.Equal(0.2, options.StructuredCallTemperature);
    }

    [Fact]
    public void The_run_log_line_names_every_option_and_its_value()
    {
        string line = new HarnessOptions(EnableFileGuard: false, MaxCallsRepair: 12).Describe();

        Assert.Contains("EnableFileGuard=off", line, StringComparison.Ordinal);
        Assert.Contains("EnableArbiter=on", line, StringComparison.Ordinal);
        Assert.Contains("ReviewGateBlocks=off", line, StringComparison.Ordinal);
        Assert.Contains("MaxCallsRepair=12", line, StringComparison.Ordinal);
        Assert.Contains("OllamaNumCtx=32768", line, StringComparison.Ordinal);
        Assert.Contains("StructuredCallTemperature=0", line, StringComparison.Ordinal);
    }
}
