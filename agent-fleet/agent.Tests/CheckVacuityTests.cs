namespace AgentFleet.Tests;

public sealed class CheckVacuityTests
{
    private const string Tick = "\u2714";
    private const string Info = "\u2139";

    private static string Spec(params string[] lines) => "Exit code: 0\n--- stdout ---\n" + string.Join("\n", lines) + "\n";

    [Fact]
    public void A_file_that_registers_no_test_is_the_only_test_node_counted()
    {
        // The real output of `node --test test/cron.test.js` for a script that only prints.
        string output = Spec("PASS: 30 9 * * 1-5 -> 2026-01-05T09:30:00.000Z", "ERROR: 59 23 31 12 * -> No match",
            $"{Tick} test\\cron.test.js (165.0878ms)", $"{Info} tests 1", $"{Info} suites 0", $"{Info} pass 1", $"{Info} fail 0");

        Assert.True(CheckVacuity.NoTestsRan(output));
    }

    [Fact]
    public void The_tap_reporters_form_of_the_same_thing_is_found_too()
    {
        string output = "Exit code: 0\n--- stdout ---\nTAP version 13\n# hi\n# Subtest: test\\\\bare.test.js\nok 1 - test\\\\bare.test.js\n  ---\n  duration_ms: 349.5\n  type: 'test'\n  ...\n1..1\n# tests 1\n# suites 0\n# pass 1\n# fail 0\n";

        Assert.True(CheckVacuity.NoTestsRan(output));
    }

    [Fact]
    public void Tests_that_ran_are_not_vacuous_even_when_a_bare_file_passed_next_to_them()
    {
        string real = Spec($"{Tick} adds (3.0785ms)", $"{Tick} subtracts (0.5287ms)", $"{Info} tests 2", $"{Info} pass 2", $"{Info} fail 0");
        string both = Spec("hi", $"{Tick} test\\bare.test.js (170.3237ms)", $"{Tick} a (0.9784ms)", $"{Tick} b (0.161ms)", $"{Info} tests 3", $"{Info} pass 3", $"{Info} fail 0");
        string tapReal = "TAP version 13\nok 1 - adds\nok 2 - subtracts\n1..2\n# tests 2\n# pass 2\n# fail 0\n";

        Assert.False(CheckVacuity.NoTestsRan(real));
        Assert.False(CheckVacuity.NoTestsRan(both));
        Assert.False(CheckVacuity.NoTestsRan(tapReal));
    }

    [Theory]
    [InlineData("Exit code: 0\n--- stdout ---\nNo test is available in C:\\p\\bin\\Debug\\net9.0\\App.Tests.dll. Make sure that test discoverers are registered.", true)]
    [InlineData("Passed!  - Failed:     0, Passed:     0, Skipped:     0, Total:     0, Duration: 1 ms - App.Tests.dll (net9.0)", true)]
    [InlineData("Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 1 s - App.Tests.dll (net9.0)", false)]
    [InlineData("Exit code: 0\n--- stdout ---\nok", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_dotnet_runner_and_plain_output_are_judged_the_same_way(string? output, bool vacuous)
    {
        Assert.Equal(vacuous, CheckVacuity.NoTestsRan(output));
    }

    [Fact]
    public void A_test_named_like_a_file_among_real_ones_does_not_hide_them()
    {
        // Two tests counted, one of them called "a.js": not every counted test is a bare file.
        string output = Spec($"{Tick} a.js (1.2ms)", $"{Tick} behaves (0.4ms)", $"{Info} tests 2", $"{Info} pass 2");

        Assert.False(CheckVacuity.NoTestsRan(output));
    }
}
