namespace AgentFleet.Tests;

public sealed class FailureLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-location-" + Guid.NewGuid().ToString("N"));

    public FailureLocationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "test"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "dep"));
        File.WriteAllLines(Path.Combine(_root, "src", "semver.js"),
            ["'use strict';", "function validate(v) {", "  const prerelease = v.split('-')[1];", "  const ids = prerelease.split('.');", "}"]);
        File.WriteAllLines(Path.Combine(_root, "test", "lru.test.js"),
            ["const test = require('node:test');", "", "test('a', () => {", "  assert.throws(() => new LruCache({ maxSize: 1 }), RangeError);", "});"]);
        File.WriteAllLines(Path.Combine(_root, "node_modules", "dep", "index.js"), ["module.exports = 1;"]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_node_stack_frame_from_another_machine_is_found_by_the_tail_of_its_path()
    {
        string output = "TypeError: Cannot read properties of undefined (reading 'split')\n" +
                        "    at validate (C:\\AgentFleet\\Workspaces\\agentfleet\\plans\\abc\\worker-a\\project\\src\\semver.js:4:28)\n" +
                        "    at Test.runInAsyncScope (node:async_hooks:227:14)";

        string? where = FailureLocation.Describe(output, _root);

        Assert.NotNull(where);
        Assert.Contains("- src/semver.js line 4: const ids = prerelease.split('.');", where);
        Assert.DoesNotContain("async_hooks", where);
    }

    [Fact]
    public void The_assertion_a_test_failed_on_is_shown_and_only_two_frames_are()
    {
        string output = "AssertionError: Missing expected exception (RangeError).\n" +
                        "      at TestContext.<anonymous> (C:\\x\\project\\test\\lru.test.js:4:14)\n" +
                        "      at validate (/home/runner/work/project/src/semver.js:3:20)\n" +
                        "      at validate (/home/runner/work/project/src/semver.js:4:28)";

        string? where = FailureLocation.Describe(output, _root);

        Assert.Equal(
            "Where it failed (the source line each stack frame names):\n" +
            "- test/lru.test.js line 4: assert.throws(() => new LruCache({ maxSize: 1 }), RangeError);\n" +
            "- src/semver.js line 3: const prerelease = v.split('-')[1];",
            where?.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("  File \"/work/project/src/semver.js\", line 4, in validate")]
    [InlineData("   at Semver.Validate(String v) in C:\\build\\project\\src\\semver.js:line 4")]
    [InlineData("src/semver.js:4: TypeError")]
    public void Python_dotnet_and_short_forms_are_read_too(string frame)
    {
        Assert.Contains("src/semver.js line 4", FailureLocation.Describe(frame, _root));
    }

    [Theory]
    [InlineData("at x (C:\\w\\project\\node_modules\\dep\\index.js:1:1)")] // a dependency is not the project
    [InlineData("at x (node:internal/test_runner/test:1306:25)")]
    [InlineData("at x (C:\\w\\project\\src\\missing.js:3:1)")] // not a file of the project
    [InlineData("at x (C:\\w\\project\\src\\semver.js:99:1)")] // past the end of the file
    [InlineData("at x (..\\..\\outside.js:3:1)")] // never outside the project
    [InlineData("ok in 12:34:56 on localhost:3000")]
    [InlineData("")]
    public void Nothing_is_said_when_the_output_names_no_line_of_the_project(string output)
    {
        Assert.Null(FailureLocation.Describe(output, _root));
    }

    [Fact]
    public void No_project_folder_means_nothing_is_said()
    {
        Assert.Null(FailureLocation.Describe("at x (src/semver.js:4:1)", null));
        Assert.Null(FailureLocation.Describe("at x (src/semver.js:4:1)", Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void A_long_source_line_is_cut()
    {
        File.WriteAllText(Path.Combine(_root, "src", "wide.js"), new string('x', 500));

        string? where = FailureLocation.Describe("at f (src/wide.js:1:1)", _root);

        Assert.EndsWith(" ...", where);
        Assert.True(where!.Length < 300);
    }

    [Fact]
    public void The_follow_up_and_the_retry_prompt_both_say_where_the_output_points()
    {
        var step = new PlanStep(1, "fix it", "detail", ["src/semver.js"], "node --test", "standard", StepStatus.Running, null, 1, null, null);
        var plan = new PlanRecord("0123456789abcdef0123456789abcdef", "t", "Fix the widget", PlanStatus.Running, null, [], [], [], null, null,
            [step], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) with { WorkingDirectory = _root };
        string failure = "TypeError: Cannot read properties of undefined\n    at validate (C:\\x\\project\\src\\semver.js:4:28)";
        const string line = "- src/semver.js line 4: const ids = prerelease.split('.');";

        string followUp = RepairMessages.Build(plan, step, failure, 2, [], [], sameOutput: false, changedFiles: null);
        string retry = PlanRunner.BuildPrompt(plan, step, 2, failure);

        foreach (string message in new[] { followUp, retry })
        {
            Assert.Contains(line, message);
            Assert.True(message.IndexOf(line, StringComparison.Ordinal) > message.IndexOf("semver.js:4:28", StringComparison.Ordinal), "after the failure it is about");
        }

        string without = RepairMessages.Build(plan with { WorkingDirectory = null }, step, failure, 2, [], [], sameOutput: false, changedFiles: null);
        Assert.DoesNotContain("Where it failed", without);
    }
}
