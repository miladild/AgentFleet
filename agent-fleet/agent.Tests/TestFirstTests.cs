using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class TestFirstTests
{
    private const string Failed = "Exit code: 1\n  Failed Limits.rejects_zero [1 ms]\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1";
    private const string Passed = "Exit code: 0\nPassed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1";

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("off", false)]
    [InlineData(" ON ", true)]
    public void Mode_is_explicit_and_defaults_off(string? value, bool expected) =>
        Assert.Equal(expected, TestFirst.ParseMode(value, null) == TestFirstMode.On);

    [Fact]
    public void Unknown_mode_warns_once_and_is_off()
    {
        var logger = new WarningLog();
        Assert.Equal(TestFirstMode.Off, TestFirst.ParseMode("invalid", logger));
        Assert.Equal(1, logger.Warnings);
    }

    [Theory]
    [InlineData(Failed, "accepted")]
    [InlineData(Passed, "tests-pass")]
    [InlineData("Exit code: 1\nNo test is available in project", "no-test-ran")]
    [InlineData("Exit code: 1\nerror CS1002: ; expected\nBuild FAILED", "build-error")]
    [InlineData("Exit code: 1\nsomething failed", "unreadable")]
    [InlineData("Exit code: 1\nFAILED cannot_import\nModuleNotFoundError: missing", "no-test-ran")]
    [InlineData("Exit code: 1\nnot ok 1 - tests/limits.test.js\n# tests 1\n# fail 1\nSyntaxError: Unexpected token", "build-error")]
    [InlineData("Exit code: 1\nnot ok 1 - rejects zero\n# tests 1\n# fail 1\n# skipped 0", "accepted")]
    [InlineData("Exit code: 1\n✖ rejects zero (2ms)\nℹ tests 1\nℹ fail 1\nℹ skipped 0", "accepted")]
    [InlineData("Exit code: 1\nnot ok 1 - tests/limits.test.mjs\n# tests 1\n# fail 1", "unreadable")]
    [InlineData("Exit code: 1\nnot ok 1 - tests/limits.test.js\n# tests 1\n# fail 1", "unreadable")]
    [InlineData("Exit code: 1\nnot ok 1 - tests/limits.test.cjs\n# tests 1\n# fail 1", "unreadable")]
    [InlineData("Exit code: 1\nnot ok 1 - tests/limits.test.ts\n# tests 1\n# fail 1", "unreadable")]
    [InlineData("Exit code: 1\nnot ok 1 - tests/limits.test.ts:12\n# tests 1\n# fail 1", "unreadable")]
    [InlineData("Exit code: 1\nnot ok 1 - limits.test.mjs\n# tests 1\n# fail 1", "unreadable")]
    [InlineData("Exit code: 1\nFailed Limits.rejects_zero\nFailed! - Failed: 0, Passed: 0, Skipped: 1, Total: 1", "no-test-ran")]
    [InlineData("Exit code: 1\nFailed Limits.rejects_zero\nFailed! - Failed: 1, Passed: 0, Skipped: 3, Total: 1", "no-test-ran")]
    [InlineData("Exit code: 1\n  Failed Limits.rejects_zero [1 ms]\n  Assert.Equal() Failure\n  Expected: No tests found\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1", "accepted")]
    [InlineData("Exit code: 1\n  Failed Limits.rejects_zero [1 ms]\n  Expected: Cannot find module 'limits'\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1", "accepted")]
    [InlineData("Exit code: 1\nnot ok 1 - suite\n    not ok 1 - rejects zero\n    # tests 1\n# tests 1\n# fail 1", "accepted")]
    [InlineData("Exit code: 1\nnot ok 1 - tests/limits.test.mjs\n    not ok 1 - rejects zero\n# tests 1\n# fail 1", "accepted")]
    [InlineData("Exit code: 1\nnot ok 1 - rejects zero\n  error: 'TypeError: limit is not a function'\n# tests 1\n# fail 1", "accepted")]
    [InlineData("Exit code: 1\nnot ok 1 - rejects zero\r\n# tests 1\r\n# fail 1", "accepted")]
    [InlineData("Exit code: 1\nFailed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3\nerror TS2322: Type 'string' is not assignable\nBuild FAILED", "build-error")]
    [InlineData("Exit code: 1\nFailed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3\nerror: lint failed", "unreadable")]
    [InlineData("Failed Limits.rejects_zero\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1", "unreadable")]
    [InlineData("Exit code: 1\n  Failed Limits.rejects_zero [1 ms]\n  Failed! - Failed: 0, Passed: 0, Skipped: 3, Total: 3\nFailed! - Failed: 1, Passed: 0, Skipped: 0, Total: 2", "accepted")]
    public void Decision_requires_an_executed_failing_test(string output, string outcome)
    {
        TestFirstDecision decision = TestFirst.Evaluate(output, ["tests/LimitsTests.cs"]);
        Assert.Equal(outcome, decision.Outcome);
        Assert.Equal(outcome == "accepted", decision.Accepted);
        if (outcome == "tests-pass") Assert.Contains("these tests already pass, so they do not describe new behaviour; test what this step must make true", decision.Feedback);
        if (outcome == "no-test-ran") Assert.Contains("no test ran: if the tests cannot import the code under test, say so with report_blocker", decision.Feedback);
    }

    [Fact]
    public void Changed_implementation_is_refused_even_with_a_real_failure()
    {
        var decision = TestFirst.Evaluate(Failed, ["tests/LimitsTests.cs", "src/limits.cs"]);
        Assert.Equal("non-test-changed", decision.Outcome);
        Assert.Contains("src/limits.cs", decision.Feedback);
    }

    [Fact]
    public void Unknown_change_evidence_is_refused() => Assert.Equal("unreadable", TestFirst.Evaluate(Failed, null).Outcome);

    [Fact]
    public void A_step_that_changed_no_files_is_refused_even_with_a_real_failure()
    {
        TestFirstDecision decision = TestFirst.Evaluate(Failed, []);
        Assert.Equal("no-test-changes", decision.Outcome);
        Assert.False(decision.Accepted);
        Assert.Equal("no test file was written or changed in this step: write the tests this step names", decision.Feedback);
    }

    [Fact]
    public async Task Real_node_test_output_identifies_assertion_failures_and_refuses_module_load_failures()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fleet-test-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "limits.test.mjs"), "import test from 'node:test'; import assert from 'node:assert/strict'; test('rejects zero', () => assert.equal(0, 1));");
            string failed = await FailFirstTests.RunShell("node --test --test-reporter=tap limits.test.mjs", folder, default);
            Assert.Equal("accepted", TestFirst.Evaluate(failed, ["limits.test.mjs"]).Outcome);
            File.WriteAllText(Path.Combine(folder, "limits.test.mjs"), "import './missing-module.mjs';");
            string broken = await FailFirstTests.RunShell("node --test --test-reporter=tap limits.test.mjs", folder, default);
            Assert.Equal("no-test-ran", TestFirst.Evaluate(broken, ["limits.test.mjs"]).Outcome);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Off_planning_text_is_identical_and_on_appends_the_shape()
    {
        Assert.Equal(PlanGate.PlanningInstructions(null), PlanGate.PlanningInstructions(null, testFirst: TestFirstMode.Off));
        string rules = PlanGate.PlanningInstructions(null, testFirst: TestFirstMode.On);
        Assert.StartsWith(PlanGate.PlanningInstructions(null), rules);
        Assert.Contains("kind `tests`", rules);
        Assert.Contains("skeleton", rules);
        Assert.Contains("last step", rules);
    }

    private static PlanStepInput Step(string[]? files, string? kind = null, string? verify = "dotnet test", string detail = "Add behaviour") =>
        new("Work", detail, files, verify, Kind: kind);

    private static IReadOnlyList<string> Problems(params PlanStepInput[] steps) =>
        PlanReview.Problems(Path.GetTempPath(), steps, _ => true, TestFirstMode.On);

    [Fact]
    public void Skeleton_tests_implementation_shape_is_valid()
    {
        Assert.Empty(Problems(
            Step(["src/limits.cs"], verify: "dotnet build", detail: "Create a skeleton whose bodies throw not implemented"),
            Step(["tests/LimitsTests.cs"], "tests"), Step(["src/limits.cs"])));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("TESTS")]
    public void Unknown_kinds_are_refused_only_when_on(string kind)
    {
        PlanStepInput[] steps = [Step([], kind), Step([])];
        Assert.Contains(Problems(steps), problem => problem.Contains("unknown kind"));
        Assert.Empty(PlanReview.Problems(Path.GetTempPath(), steps, _ => true));
    }

    [Fact]
    public void Tests_step_requires_files_test_paths_a_check_and_an_ordinary_final_step()
    {
        Assert.Contains(Problems(Step([], "tests"), Step([])), p => p.Contains("no files"));
        Assert.Contains(Problems(Step(["src/limits.cs"], "tests"), Step([])), p => p.Contains("not a test path"));
        Assert.Contains(Problems(Step(["tests/LimitsTests.cs"], "tests", null), Step([])), p => p.Contains("verify command"));
        Assert.Contains(Problems(Step(["tests/LimitsTests.cs"], "tests")), p => p.Contains("last step"));
        Assert.Contains(Problems(Step(["src/limits.cs"])), p => p.Contains("earlier tests step"));
    }

    [Fact]
    public void Kind_survives_parse_store_reload_and_markdown()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fleet-kind-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inputs = PlanTools.ToSteps(JsonSerializer.SerializeToElement(new[] { new { title = "Write limits tests", detail = "Cover zero", files = new[] { "tests/LimitsTests.cs" }, kind = "tests" } }));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = folder }).Build();
            var store = new FleetPlanStore(configuration);
            PlanRecord plan = store.Create("Limits", "Reject zero", null, [], [], [], null, null, inputs);
            PlanRecord read = new FleetPlanStore(configuration).Get(plan.Id)!;
            Assert.Equal("tests", read.Steps[0].Kind);
            Assert.Contains("Kind: tests", FleetPlanStore.ToMarkdown(read));
            Assert.Null(JsonSerializer.Deserialize<PlanStepInput>("{\"Title\":\"Old\",\"Detail\":\"Old\"}")!.Kind);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class WarningLog : ILogger
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Warning) Warnings++; }
    }
}
