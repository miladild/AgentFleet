namespace AgentFleet.Tests;

public sealed class StuckStepGuardTests
{
    private const string Plan = "plan-1";
    private const string TestRun = "test-run";

    private static StuckDecision Observe(StuckStepGuard guard, OutcomeClass outcome, string family = TestRun, int step = 1, int attempt = 1, string? output = null) =>
        guard.Observe(Plan, step, attempt, family, outcome, output);

    [Fact]
    public void A_time_limit_is_left_alone_the_first_time_noted_the_second_time_and_stopped_the_third_time()
    {
        var guard = new StuckStepGuard();

        StuckDecision first = Observe(guard, OutcomeClass.TimeLimit);
        StuckDecision second = Observe(guard, OutcomeClass.TimeLimit);
        StuckDecision third = Observe(guard, OutcomeClass.TimeLimit);
        StuckDecision fourth = Observe(guard, OutcomeClass.TimeLimit);

        Assert.Equal((StuckAction.None, 1), (first.Action, first.Repeats));
        Assert.Null(first.Note);
        Assert.Equal((StuckAction.Note, 2), (second.Action, second.Repeats));
        Assert.Equal((StuckAction.Stop, 3), (third.Action, third.Repeats));
        Assert.Equal((StuckAction.Stop, 4), (fourth.Action, fourth.Repeats));
        Assert.NotNull(second.Note);
        Assert.NotNull(third.Note);
        Assert.Equal(OutcomeClass.TimeLimit, third.Outcome);
    }

    [Theory]
    [InlineData(nameof(OutcomeClass.Pass))]
    [InlineData(nameof(OutcomeClass.TestFailure))]
    [InlineData(nameof(OutcomeClass.CompileError))]
    [InlineData(nameof(OutcomeClass.OtherError))]
    public void Results_that_are_not_infrastructure_outcomes_are_never_acted_on_and_end_a_streak(string ordinaryName)
    {
        OutcomeClass ordinary = Enum.Parse<OutcomeClass>(ordinaryName);
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit);
        Observe(guard, OutcomeClass.TimeLimit);

        StuckDecision between = Observe(guard, ordinary);
        StuckStreak? streakAfter = guard.Streak(Plan, 1, 1);
        StuckDecision again = Observe(guard, OutcomeClass.TimeLimit);

        Assert.Equal((StuckAction.None, 0), (between.Action, between.Repeats));
        Assert.Null(between.Note);
        Assert.Null(streakAfter);
        Assert.Equal((StuckAction.None, 1), (again.Action, again.Repeats));
        for (int index = 0; index < 5; index++)
        {
            Assert.Equal(StuckAction.None, Observe(guard, ordinary).Action);
        }
    }

    [Fact]
    public void A_different_infrastructure_class_starts_a_new_streak_of_one()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit);
        Observe(guard, OutcomeClass.TimeLimit);

        StuckDecision crash = Observe(guard, OutcomeClass.Crash);
        StuckDecision crashAgain = Observe(guard, OutcomeClass.Crash);

        Assert.Equal((StuckAction.None, 1, OutcomeClass.Crash), (crash.Action, crash.Repeats, crash.Outcome));
        Assert.Equal((StuckAction.Note, 2, OutcomeClass.Crash), (crashAgain.Action, crashAgain.Repeats, crashAgain.Outcome));
    }

    [Fact]
    public void A_different_command_family_starts_a_new_streak()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit);

        StuckDecision other = Observe(guard, OutcomeClass.TimeLimit, family: "build");

        Assert.Equal((StuckAction.None, 1), (other.Action, other.Repeats));
    }

    [Fact]
    public void A_pass_between_two_time_limits_starts_the_count_again()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit);
        Observe(guard, OutcomeClass.Pass);

        StuckDecision after = Observe(guard, OutcomeClass.TimeLimit);

        Assert.Equal((StuckAction.None, 1), (after.Action, after.Repeats));
    }

    [Fact]
    public void The_thresholds_come_from_the_options()
    {
        var guard = new StuckStepGuard(new HarnessOptions(StuckNoteAfter: 3, StuckStopAfter: 5));

        StuckAction[] actions = Enumerable.Range(0, 6).Select(_ => Observe(guard, OutcomeClass.OpenHandle).Action).ToArray();

        Assert.Equal([StuckAction.None, StuckAction.None, StuckAction.Note, StuckAction.Note, StuckAction.Stop, StuckAction.Stop], actions);
    }

    [Fact]
    public void A_stop_threshold_below_the_note_threshold_is_raised_to_it()
    {
        var guard = new StuckStepGuard(new HarnessOptions(StuckNoteAfter: 4, StuckStopAfter: 2));

        StuckAction[] actions = Enumerable.Range(0, 4).Select(_ => Observe(guard, OutcomeClass.Crash).Action).ToArray();

        Assert.Equal([StuckAction.None, StuckAction.None, StuckAction.None, StuckAction.Stop], actions);
    }

    [Fact]
    public void Reset_forgets_the_streak_of_that_attempt_only()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit, attempt: 1);
        Observe(guard, OutcomeClass.TimeLimit, attempt: 1);
        Observe(guard, OutcomeClass.TimeLimit, attempt: 2);

        guard.Reset(Plan, 1, 1);

        Assert.Null(guard.Streak(Plan, 1, 1));
        Assert.Equal(1, guard.Streak(Plan, 1, 2)?.Repeats);
        Assert.Equal((StuckAction.None, 1), (Observe(guard, OutcomeClass.TimeLimit, attempt: 1).Action, guard.Streak(Plan, 1, 1)!.Value.Repeats));
    }

    [Fact]
    public void The_streak_can_be_read_for_the_log()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit);
        Observe(guard, OutcomeClass.TimeLimit);

        StuckStreak? streak = guard.Streak(Plan, 1, 1);

        Assert.Equal(new StuckStreak(TestRun, OutcomeClass.TimeLimit, 2), streak);
    }

    [Fact]
    public void Separate_steps_attempts_and_plans_do_not_interfere()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit, step: 1, attempt: 1);
        Observe(guard, OutcomeClass.TimeLimit, step: 1, attempt: 1);

        Assert.Equal(StuckAction.None, Observe(guard, OutcomeClass.TimeLimit, step: 2, attempt: 1).Action);
        Assert.Equal(StuckAction.None, Observe(guard, OutcomeClass.TimeLimit, step: 1, attempt: 2).Action);
        Assert.Equal(StuckAction.None, guard.Observe("plan-2", 1, 1, TestRun, OutcomeClass.TimeLimit).Action);
        Assert.Equal(StuckAction.Stop, Observe(guard, OutcomeClass.TimeLimit, step: 1, attempt: 1).Action);
    }

    // A conversation reset inside an attempt does not touch the guard: it is keyed by the attempt, not by a conversation.
    [Fact]
    public void The_streak_survives_whatever_the_conversation_does_inside_one_attempt()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit);

        // Nothing here names a conversation; the next result of the same attempt is the second in the streak.
        StuckDecision next = Observe(guard, OutcomeClass.TimeLimit);

        Assert.Equal(StuckAction.Note, next.Action);
    }

    [Theory]
    [InlineData(nameof(OutcomeClass.TimeLimit), "time limit")]
    [InlineData(nameof(OutcomeClass.Crash), "crash")]
    [InlineData(nameof(OutcomeClass.OpenHandle), "stays open")]
    [InlineData(nameof(OutcomeClass.CleanupRejected), "left behind")]
    public void Each_infrastructure_class_has_a_note_in_plain_words_that_names_no_tool_model_or_machine(string outcomeName, string phrase)
    {
        OutcomeClass outcome = Enum.Parse<OutcomeClass>(outcomeName);
        var guard = new StuckStepGuard();
        Observe(guard, outcome);
        string note = Observe(guard, outcome).Note!;
        string stop = Observe(guard, outcome).Note!;

        Assert.Contains(phrase, note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 times in a row", note, StringComparison.Ordinal);
        Assert.Contains("3 times in a row", stop, StringComparison.Ordinal);
        Assert.Contains("round ends here", stop, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next round starts fresh", stop, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("round ends here", note, StringComparison.OrdinalIgnoreCase);
        // Plain words only: no name of a tool, model or machine (those are capitalised or hyphenated words), no path, no bracketed tag.
        foreach (string text in new[] { note, stop })
        {
            Assert.DoesNotMatch(@"[a-z,;] [A-Z]|\w-\w|[\/\[\]]", text);
        }
    }

    [Fact]
    public void The_time_limit_note_says_running_it_again_will_not_help_and_where_to_look()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.TimeLimit);

        string note = Observe(guard, OutcomeClass.TimeLimit).Note!;

        Assert.Contains("however it was written", note, StringComparison.Ordinal);
        Assert.Contains("will not help", note, StringComparison.Ordinal);
        Assert.Contains("server or timer left open", note, StringComparison.Ordinal);
        Assert.Contains("waiting for input", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_open_handle_note_names_the_resource_the_output_names()
    {
        var guard = new StuckStepGuard();
        const string output = "# tests 3\n# pass 3\n[open-after-tests] TCPServerWrap\n";
        Observe(guard, OutcomeClass.OpenHandle, output: output);

        string note = Observe(guard, OutcomeClass.OpenHandle, output: output).Note!;

        Assert.Contains("TCPServerWrap", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_open_handle_note_still_reads_well_when_the_output_names_no_resource()
    {
        var guard = new StuckStepGuard();
        Observe(guard, OutcomeClass.OpenHandle, output: "tests finished");

        string note = Observe(guard, OutcomeClass.OpenHandle, output: "tests finished").Note!;

        Assert.Contains("stays open", note, StringComparison.Ordinal);
        Assert.DoesNotContain("[open-after-tests]", note, StringComparison.Ordinal);
    }

    // The checks of one hard step, in the order they came back: four time limits, then ordinary failures, then a pass.
    [Fact]
    public void Replay_of_a_real_hard_step_notes_the_second_time_limit_stops_at_the_third_and_is_silent_after()
    {
        var guard = new StuckStepGuard();
        OutcomeClass[] sequence =
        [
            OutcomeClass.TimeLimit, OutcomeClass.TimeLimit, OutcomeClass.TimeLimit, OutcomeClass.TimeLimit,
            OutcomeClass.CompileError, OutcomeClass.TestFailure, OutcomeClass.CompileError, OutcomeClass.Pass
        ];

        StuckAction[] actions = sequence.Select(outcome => Observe(guard, outcome).Action).ToArray();

        Assert.Equal(
            [StuckAction.None, StuckAction.Note, StuckAction.Stop, StuckAction.Stop,
             StuckAction.None, StuckAction.None, StuckAction.None, StuckAction.None],
            actions);
    }

    [Fact]
    public void Replay_of_a_healthy_step_stays_silent()
    {
        var guard = new StuckStepGuard();
        OutcomeClass[] sequence = [OutcomeClass.CompileError, OutcomeClass.TestFailure, OutcomeClass.Pass];

        Assert.All(sequence, outcome => Assert.Equal(StuckAction.None, Observe(guard, outcome).Action));
    }

    [Fact]
    public void The_stuck_options_have_defaults_and_read_their_settings_within_bounds()
    {
        HarnessOptions defaults = HarnessOptions.FromSettings(_ => null);
        Assert.Equal((true, 2, 3, 3), (defaults.StuckGuardEnabled, defaults.StuckNoteAfter, defaults.StuckStopAfter, defaults.StuckUnchargedRoundsPerAttempt));

        var settings = new Dictionary<string, string?>
        {
            ["FLEET_HARNESS_STUCK_GUARD_ENABLED"] = "false",
            ["FLEET_HARNESS_STUCK_NOTE_AFTER"] = "4",
            ["FLEET_HARNESS_STUCK_STOP_AFTER"] = "6",
            ["FLEET_HARNESS_STUCK_UNCHARGED_ROUNDS_PER_ATTEMPT"] = "0"
        };
        HarnessOptions set = HarnessOptions.FromSettings(name => settings.GetValueOrDefault(name));
        Assert.Equal((false, 4, 6, 0), (set.StuckGuardEnabled, set.StuckNoteAfter, set.StuckStopAfter, set.StuckUnchargedRoundsPerAttempt));

        var bad = new Dictionary<string, string?>
        {
            ["FLEET_HARNESS_STUCK_NOTE_AFTER"] = "0",
            ["FLEET_HARNESS_STUCK_STOP_AFTER"] = "1",
            ["FLEET_HARNESS_STUCK_UNCHARGED_ROUNDS_PER_ATTEMPT"] = "11"
        };
        HarnessOptions kept = HarnessOptions.FromSettings(name => bad.GetValueOrDefault(name));
        Assert.Equal((2, 3, 3), (kept.StuckNoteAfter, kept.StuckStopAfter, kept.StuckUnchargedRoundsPerAttempt));
    }

    [Fact]
    public void A_stop_setting_below_the_note_setting_is_raised_to_it()
    {
        var settings = new Dictionary<string, string?>
        {
            ["FLEET_HARNESS_STUCK_NOTE_AFTER"] = "5",
            ["FLEET_HARNESS_STUCK_STOP_AFTER"] = "3"
        };

        HarnessOptions options = HarnessOptions.FromSettings(name => settings.GetValueOrDefault(name));

        Assert.Equal((5, 5), (options.StuckNoteAfter, options.StuckStopAfter));
    }

    [Fact]
    public void The_run_log_line_names_the_stuck_options()
    {
        string line = new HarnessOptions(StuckGuardEnabled: false, StuckNoteAfter: 4).Describe();

        Assert.Contains("StuckGuardEnabled=off", line, StringComparison.Ordinal);
        Assert.Contains("StuckNoteAfter=4", line, StringComparison.Ordinal);
        Assert.Contains("StuckStopAfter=3", line, StringComparison.Ordinal);
        Assert.Contains("StuckUnchargedRoundsPerAttempt=3", line, StringComparison.Ordinal);
    }
}
