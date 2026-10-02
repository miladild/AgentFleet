namespace AgentFleet.Tests;

public sealed class CheckGuardTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"fleet-guard-{Guid.NewGuid():N}");

    public CheckGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, "src"));
        Directory.CreateDirectory(Path.Combine(_project, "test"));
        File.WriteAllText(Path.Combine(_project, "package.json"),
            """{"scripts":{"dev":"tsx watch src/index.ts","build":"tsc","test":"node --test"}}""");
        File.WriteAllText(Path.Combine(_project, "src", "index.js"), "");
        File.WriteAllText(Path.Combine(_project, "test", "a.test.js"), "");
        File.WriteAllText(Path.Combine(_project, "test", "b.test.js"), "");
    }

    public void Dispose() => Directory.Delete(_project, recursive: true);

    private static readonly Func<string, bool> Installed = program => program is "node" or "npm" or "dotnet" or "python" or "cd";

    // Step 1 is the one under test; step 2 closes the plan with a whole-project check, so the final-step rule is about step 2.
    private PlanRecord Plan(string firstCheck, string lastCheck = "npm run build && npm test")
    {
        PlanStep Step(int id, string title, string check, params string[] files) => new(
            id, title, "do it", files, check, "standard", StepStatus.Pending, null, 0, null, null);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new PlanRecord("id", "plan", "goal", PlanStatus.Running, _project, [], [], [], null, null,
            [Step(1, "first", firstCheck, "src/index.js", "test/a.test.js"), Step(2, "last", lastCheck, "src/index.js")], now, now, now);
    }

    private CheckVerdict Judge(string original, string? proposal, int step = 1, string? lastCheck = null)
    {
        PlanRecord plan = lastCheck is null ? Plan(original) : Plan(original, lastCheck);
        return CheckGuard.Validate(plan, plan.Steps[step - 1], original, proposal, Installed);
    }

    [Theory]
    [InlineData("echo ok")]
    [InlineData("true")]
    [InlineData("exit 0")]
    [InlineData("node --version")]
    [InlineData("type src\\index.js")]
    [InlineData("dir")]
    [InlineData("rem done")]
    [InlineData("npm run build || echo ok")]
    [InlineData("npm run build; exit 0")]
    [InlineData("npm run build || true")]
    [InlineData("npm run build; $true")]
    public void A_proposal_that_passes_without_the_work_is_refused(string proposal)
    {
        CheckVerdict verdict = Judge("npm run dev", proposal);
        Assert.False(verdict.Accepted);
        Assert.NotEmpty(verdict.Reason);
    }

    [Fact]
    public void A_dev_server_check_may_become_the_build_or_the_tests()
    {
        CheckVerdict build = Judge("npm run dev", "npm run build");
        Assert.True(build.Accepted, build.Reason);
        Assert.Equal("validation", build.Kind);
        Assert.True(Judge("npm run dev", "node --test test/a.test.js").Accepted);
    }

    [Fact]
    public void A_proposal_that_is_the_same_check_or_not_a_runnable_command_is_refused()
    {
        Assert.Contains("already has", Judge("npm run dev", "npm  run   dev").Reason);
        Assert.Contains("one command on one line", Judge("npm run dev", "npm run build\nnpm test").Reason);
        Assert.Contains("short command", Judge("npm run dev", "npm test " + new string('x', 700)).Reason);
        Assert.False(Judge("npm run dev", "").Accepted);
        Assert.False(Judge("npm run dev", null).Accepted);
        // The plan's own rules: a program that is not there, a file nobody creates, a command that never ends.
        Assert.Contains("not a command", Judge("npm run dev", "nonexistent-tool build").Reason);
        Assert.Contains("does not exist", Judge("npm run dev", "node --test test/missing.test.js").Reason);
        Assert.Contains("does not exit", Judge("npm run dev", "npm run dev -- --port 4000").Reason);
        Assert.Contains("does not exit", Judge("npm run dev", "node --test --watch").Reason);
    }

    [Fact]
    public void A_proposal_the_unattended_policy_would_refuse_is_refused()
    {
        Assert.False(Judge("npm run dev", "git status").Accepted);
        Assert.False(Judge("npm run dev", "npm run build && curl http://example.com").Accepted);
        Assert.False(Judge("npm run dev", "npm run build && rm -rf node_modules").Accepted);
    }

    private void AssertSyntaxOnly(string original, string proposal, int step = 1)
    {
        CheckVerdict verdict = Judge(original, proposal, step);
        Assert.True(verdict.Accepted, $"{original}  ->  {proposal}: {verdict.Reason}");
        Assert.Equal("syntax-only", verdict.Kind);
    }

    [Fact]
    public void A_syntax_only_rewrite_keeps_the_same_programs_and_arguments()
    {
        AssertSyntaxOnly("cd src && node --test test/a.test.js", "cd src; if ($?) { node --test test/a.test.js }");
        AssertSyntaxOnly("cd test && node --test", "Set-Location test; node --test");
        AssertSyntaxOnly("cd /d test && node --test", "cd test; node --test");
        AssertSyntaxOnly(@"node --test test\a.test.js", "node --test test/a.test.js");
        AssertSyntaxOnly("node -e \"require('./src/index.js')\"", "node -e 'require(\"./src/index.js\")'");
        AssertSyntaxOnly("npm test", "npm test; exit $LASTEXITCODE");
        AssertSyntaxOnly("npm run build && npm test", "cmd /c \"npm run build && npm test\"");
        AssertSyntaxOnly("npm run build && npm test", "powershell -NoProfile -Command \"npm run build; if ($?) { npm test }\"");
    }

    [Fact]
    public void A_rewrite_that_changes_an_argument_or_drops_a_program_is_not_syntax_only()
    {
        // Another test file: not the same arguments, and a narrower run than the original.
        CheckVerdict other = Judge("node --test test/a.test.js", "node --test test/b.test.js");
        Assert.False(other.Accepted);
        Assert.Contains("drops", other.Reason);

        // The original ran the build and the tests; the proposal only runs the tests.
        CheckVerdict dropped = Judge("npm run build && npm test", "npm test");
        Assert.False(dropped.Accepted);
        Assert.Contains("npm run build", dropped.Reason);
    }

    [Fact]
    public void A_wider_run_of_the_same_tool_covers_the_narrower_one()
    {
        CheckVerdict verdict = Judge("node --test test/a.test.js; npm run dev", "node --test");
        Assert.True(verdict.Accepted, verdict.Reason);
        Assert.Equal("validation", verdict.Kind);
    }

    [Fact]
    public void A_rewrite_may_add_tests_and_builds_but_no_other_program()
    {
        CheckVerdict added = Judge("npm run dev", "npm run build && node src/index.js");
        Assert.False(added.Accepted);
        Assert.Contains("which the original check did not run", added.Reason);
        Assert.False(Judge("npm run dev", "npm run build; python -c \"print(1)\"").Accepted);

        // More of the project's own checks, and a change of folder, are fine.
        Assert.True(Judge("npm run dev", "cd src && npm run build && node --test").Accepted);
        // What the original already ran stays allowed.
        Assert.True(Judge("node src/index.js; npm run dev", "node src/index.js; npm run build").Accepted);
    }

    [Theory]
    // The exit code of a chain is its last command's: a harmless command at the end would make the check pass whatever the tests did.
    [InlineData("npm run build; ls")]
    [InlineData("npm run build || dir")]
    [InlineData("npm run build | cat")]
    [InlineData("npm run build && type package.json")]
    [InlineData("node --test; rem done")]
    [InlineData("npm run build; Get-Date")]
    [InlineData("npm run build; Write-Host ok")]
    public void A_harmless_command_added_after_the_tests_is_refused(string proposal)
    {
        CheckVerdict verdict = Judge("npm run dev", proposal);
        Assert.False(verdict.Accepted, proposal);
        Assert.Contains("which the original check did not run", verdict.Reason);
    }

    [Fact]
    public void A_harmless_command_the_original_already_ran_may_stay()
    {
        Assert.True(Judge("echo start; npm run dev", "echo start; npm run build").Accepted);
    }

    [Fact]
    public void A_shell_wrapper_does_not_hide_what_runs()
    {
        Assert.False(Judge("npm run build", "cmd /c \"npm run dev\"").Accepted);
        Assert.False(Judge("npm run dev", "powershell -Command \"npm run build; exit 0\"").Accepted);
        Assert.False(Judge("npm run dev", "cmd /c \"npm run build && node evil.js\"").Accepted);
    }

    [Fact]
    public void A_rewrite_that_changes_what_runs_must_still_run_tests_or_a_build()
    {
        CheckVerdict verdict = Judge("npm run dev", "node src/index.js");
        Assert.False(verdict.Accepted);
        Assert.Contains("tests, typecheck or build", verdict.Reason);
        Assert.False(Judge("npm run dev", "node -e \"process.exit(0)\"").Accepted);
    }

    [Fact]
    public void The_last_step_keeps_the_final_validation_rule()
    {
        // The project defines build and test scripts, so the final check has to run both.
        CheckVerdict onlyBuild = Judge("npm run dev", "npm run build", step: 2, lastCheck: "npm run dev");
        Assert.False(onlyBuild.Accepted);
        Assert.Contains("last step", onlyBuild.Reason);

        CheckVerdict both = Judge("npm run dev", "npm run build && npm test", step: 2, lastCheck: "npm run dev");
        Assert.True(both.Accepted, both.Reason);
    }

    [Fact]
    public void Segments_read_what_is_run_and_not_how_it_is_spelled()
    {
        Assert.Equal(["cd", "app"], CheckGuard.Segments("Set-Location .\\app")[0]);
        Assert.Equal(
            CheckGuard.Segments("cd app && npm test").SelectMany(words => words),
            CheckGuard.Segments("cd app; if ($?) { npm test }").SelectMany(words => words));
        Assert.Equal(CheckGuard.Segments("npm test"), CheckGuard.Segments("npm test; exit $LASTEXITCODE"));
        Assert.NotEqual(CheckGuard.Segments("npm test"), CheckGuard.Segments("npm test; exit 0"));
    }
}
