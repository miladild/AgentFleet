using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class OutcomeClassifierTests
{
    // The text the forced-exit crash prints (libuv's assertion while a connection is still closing).
    private const string ForcedExitCrash = "Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)";

    [Fact]
    public void A_timed_out_run_with_partial_output_is_a_time_limit()
    {
        Assert.Equal(OutcomeClass.TimeLimit, OutcomeClassifier.Of(null, true, "ok 1 - first\nok 2 - second"));
    }

    [Fact]
    public void A_timed_out_run_is_a_time_limit_even_when_it_printed_a_failing_test()
    {
        Assert.Equal(OutcomeClass.TimeLimit, OutcomeClassifier.Of(1, true, "not ok 3 - third test"));
    }

    [Fact]
    public void An_open_handle_line_is_an_open_handle()
    {
        Assert.Equal(OutcomeClass.OpenHandle, OutcomeClassifier.Of(1, false, "[open-after-tests] TCPServerWrap"));
    }

    [Fact]
    public void A_reference_error_is_a_compile_error()
    {
        Assert.Equal(OutcomeClass.CompileError, OutcomeClassifier.Of(1, false, "ReferenceError: assert is not defined"));
    }

    [Theory]
    [InlineData("SyntaxError: Unexpected token ')'")]
    [InlineData("Error: Cannot find module './helpers'")]
    [InlineData("Error [ERR_MODULE_NOT_FOUND]: Cannot find package 'x'")]
    [InlineData("src/app.ts(3,5): error TS1005: ';' expected.")]
    [InlineData("Program.cs(3,5): error CS1002: ; expected")]
    [InlineData("value is not defined")]
    public void Syntax_import_and_reference_errors_are_compile_errors(string output)
    {
        Assert.Equal(OutcomeClass.CompileError, OutcomeClassifier.Of(1, false, output));
    }

    [Fact]
    public void A_failing_tap_test_is_a_test_failure()
    {
        Assert.Equal(OutcomeClass.TestFailure, OutcomeClassifier.Of(1, false, "not ok 3 - third test"));
    }

    [Fact]
    public void Exit_code_zero_is_a_pass()
    {
        Assert.Equal(OutcomeClass.Pass, OutcomeClassifier.Of(0, false, "ok 1 - first"));
    }

    [Fact]
    public void A_usage_error_is_an_other_error()
    {
        Assert.Equal(OutcomeClass.OtherError, OutcomeClassifier.Of(2, false, "Usage: tool [options] <files>"));
    }

    [Fact]
    public void The_forced_exit_crash_text_is_a_crash()
    {
        Assert.Equal(OutcomeClass.Crash, OutcomeClassifier.Of(1, false, ForcedExitCrash));
    }

    [Theory]
    [InlineData(-1073741819)]
    [InlineData(134)]
    [InlineData(139)]
    public void A_native_crash_exit_code_is_a_crash(int exitCode)
    {
        Assert.Equal(OutcomeClass.Crash, OutcomeClassifier.Of(exitCode, false, string.Empty));
    }

    [Fact]
    public void A_null_exit_code_that_is_not_a_time_limit_is_an_other_error()
    {
        // Without an exit code the failing-test markers do not count: only a non-zero exit makes a test failure.
        Assert.Equal(OutcomeClass.OtherError, OutcomeClassifier.Of(null, false, "not ok 1 - first"));
    }

    [Fact]
    public void A_non_zero_exit_with_no_known_marker_is_an_other_error()
    {
        Assert.Equal(OutcomeClass.OtherError, OutcomeClassifier.Of(7, false, "npm ERR! code ELIFECYCLE"));
    }

    [Theory]
    [InlineData("Pass", "pass")]
    [InlineData("TestFailure", "test-failure")]
    [InlineData("CompileError", "compile-error")]
    [InlineData("OtherError", "other-error")]
    [InlineData("TimeLimit", "time-limit")]
    [InlineData("Crash", "crash")]
    [InlineData("CleanupRejected", "cleanup-rejected")]
    [InlineData("OpenHandle", "open-handle")]
    public void Each_class_has_its_run_log_name(string outcome, string name)
    {
        Assert.Equal(name, OutcomeClassifier.Name(Enum.Parse<OutcomeClass>(outcome)));
    }

    [Theory]
    [InlineData("TimeLimit", true)]
    [InlineData("Crash", true)]
    [InlineData("CleanupRejected", true)]
    [InlineData("OpenHandle", true)]
    [InlineData("Pass", false)]
    [InlineData("TestFailure", false)]
    [InlineData("CompileError", false)]
    [InlineData("OtherError", false)]
    public void Infrastructure_classes_are_the_ones_not_caused_by_the_work(string outcome, bool infra)
    {
        Assert.Equal(infra, OutcomeClassifier.IsInfra(Enum.Parse<OutcomeClass>(outcome)));
    }

    [Theory]
    [InlineData("node --test test/a.test.js")]
    [InlineData("node --test --test-timeout=30000 test/a.test.js 2>&1 | tail -20")]
    [InlineData("cd app && npm test")]
    [InlineData("npx tsx --test x.ts")]
    [InlineData("dotnet test foo.csproj")]
    [InlineData("NPM RUN TEST:unit")]
    [InlineData("npx vitest run")]
    [InlineData("pytest -q tests")]
    [InlineData("node --experimental-strip-types --test test/a.test.ts")]
    [InlineData("node --import ./setup.mjs --test test/a.test.mjs")]
    [InlineData("cd app && node --import tsx --test-timeout=30000 --test test/a.test.ts")]
    public void Test_runner_commands_are_test_runs(string command)
    {
        Assert.Equal("test-run", OutcomeClassifier.CommandFamily(command));
    }

    [Theory]
    [InlineData("npm run build")]
    [InlineData("npx tsc -p .")]
    [InlineData("dotnet build")]
    public void Build_commands_are_builds(string command)
    {
        Assert.Equal("build", OutcomeClassifier.CommandFamily(command));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("cat tsconfig.json")]
    public void Other_commands_are_other(string command)
    {
        Assert.Equal("other", OutcomeClassifier.CommandFamily(command));
    }

    [Fact]
    public void A_missing_command_is_other()
    {
        Assert.Equal("other", OutcomeClassifier.CommandFamily(null));
    }
}

public sealed class OutcomeClassRunLogTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private static readonly JsonSerializerOptions StoredOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private PlanRunner Runner(FakeStepAgent agent, Func<string, string> verify) =>
        new(Store, new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(verify(command))), agent, NullLogger.Instance);

    private PlanRecord Approved(params PlanStepInput[] steps) => Store.Approve(NewPlan(steps).Id, review: PlanSecondOpinion.Off)!;

    private static FakeStepAgent Agent() => new((_, _, _) => Task.FromResult("Wrote it.\n\n_— via worker1_"));

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private static string Fail(string _) => "Exit code: 1\n--- stdout ---\nAssertionError: expected 3 got 2";

    [Fact]
    public void An_event_written_before_the_outcome_fields_existed_still_loads_with_them_empty()
    {
        PlanRecord plan = Approved(Step("one"));
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.CheckFailed, detail: "Check failed.");
        string path = Path.Combine(PlansDirectory, plan.Id + ".json");
        string legacy = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), ",\\s*\"outcomeClass\": null", string.Empty);
        legacy = System.Text.RegularExpressions.Regex.Replace(legacy, ",\\s*\"seconds\": null", string.Empty);
        Assert.DoesNotContain("\"outcomeClass\"", legacy);
        Assert.DoesNotContain("\"seconds\"", legacy);
        File.WriteAllText(path, legacy);

        PlanRunEvent failed = Assert.Single(Store.Get(plan.Id)!.Events!);

        Assert.Null(failed.OutcomeClass);
        Assert.Null(failed.Seconds);
    }

    [Fact]
    public void An_event_with_the_outcome_fields_round_trips_through_the_plan_file()
    {
        PlanRecord plan = Approved(Step("one"));
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.CheckFailed, detail: "Check timed out.", outcomeClass: "time-limit", seconds: 31.5);

        PlanRunEvent stored = Assert.Single(Store.Get(plan.Id)!.Events!);

        Assert.Equal("time-limit", stored.OutcomeClass);
        Assert.Equal(31.5, stored.Seconds);
        Assert.Contains("\"seconds\": 31.5", File.ReadAllText(Path.Combine(PlansDirectory, plan.Id + ".json")));
        Assert.Equal(31.5, JsonSerializer.Deserialize<PlanRunEvent>(JsonSerializer.Serialize(stored, StoredOptions), StoredOptions)!.Seconds);
    }

    [Fact]
    public void The_run_log_shows_the_outcome_and_seconds_only_for_events_that_have_them()
    {
        PlanRecord plan = Approved(Step("one"));
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.CheckFailed, detail: "Check timed out.", outcomeClass: "time-limit", seconds: 31);
        Store.AddEvent(plan.Id, 1, 2, RunEventKind.CheckFailed, detail: "Check failed without a class.");

        string report = FleetPlanStore.ToReportMarkdown(Store.Get(plan.Id)!);

        Assert.Contains("  > outcome time-limit, took 31 s", report);
        Assert.Single(report.Split('\n').Where(line => line.Contains("outcome ") || line.Contains("took ")));
    }

    [Fact]
    public async Task A_failing_check_is_recorded_with_its_outcome_class_and_how_long_it_took()
    {
        PlanRecord plan = Approved(Step("hard one"));

        await Runner(Agent(), Fail).RunPlanAsync(plan.Id, default);

        PlanRunEvent failed = Store.Get(plan.Id)!.Events!.First(e => e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed);
        Assert.Equal("test-failure", failed.OutcomeClass);
        Assert.NotNull(failed.Seconds);
    }

    [Fact]
    public async Task A_passing_check_is_recorded_as_a_pass_with_its_duration()
    {
        PlanRecord plan = Approved(Step("easy one"));

        await Runner(Agent(), Pass).RunPlanAsync(plan.Id, default);

        PlanRunEvent passed = Store.Get(plan.Id)!.Events!.First(e => e.Kind is RunEventKind.CheckPassed or RunEventKind.FinalValidationPassed);
        Assert.Equal("pass", passed.OutcomeClass);
        Assert.NotNull(passed.Seconds);
    }
}
