namespace AgentFleet.Tests;

/// <summary>
/// A step's check can run code without type checking it (a test runner that strips types), so a worker can write a class that
/// the compiler rejects and then chase the test failures it causes without ever seeing the real cause. When a check fails and the
/// project has a typecheck script, the type errors in the step's own files go back to the worker with the failure.
/// </summary>
public sealed partial class PlanNoChangeTests
{
    private const string TypecheckKind = "typecheck-hint";
    private const string TypecheckCommand = "npm run typecheck";

    private const string TypecheckErrors = "Exit code: 2\n--- stdout ---\n" +
        "src/a.ts(3,11): error TS2300: Duplicate identifier 'history'.\n" +
        "  The other declaration is here.\n" +
        "src/b.ts(1,1): error TS2304: Cannot find name 'later'.\n" +
        "src/a.ts(9,3): error TS2300: Duplicate identifier 'history'.";

    private void WritePackageScripts(string scripts) =>
        WriteProjectFile("package.json", "{ \"name\": \"p\", \"scripts\": { " + scripts + " } }");

    // One step that lists src/a.ts. The check fails while a.ts says "bad" and passes once it says "good".
    private (PlanRecord Plan, FakeStepAgent Agent, List<string> Commands, Func<string, string> Verify) TypecheckPlan(string typecheckOutput)
    {
        SeedFiles("src/a.ts");
        PlanRecord plan = Store.Approve(Store.Create("One step", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Listed("write a", "check1", "src/a.ts")]).Id)!;
        var commands = new List<string>();
        int writes = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("src/a.ts", writes++ == 0 ? "bad" : "good");
            return Task.FromResult("Done.");
        });
        string Verify(string command)
        {
            commands.Add(command);
            if (command == TypecheckCommand) return typecheckOutput;
            string text = File.ReadAllText(Path.Combine(ProjectDirectory, "src/a.ts"));
            return text == "good" ? Pass(command) : "Exit code: 1\n--- stdout ---\n✖ stores the alert (3 ms)\n  TypeError: store.history is not a function\nℹ fail 1";
        }

        return (plan, agent, commands, Verify);
    }

    private int TypecheckHints(string planId) => Store.Get(planId)?.Events?.Count(runEvent => runEvent.Kind == TypecheckKind) ?? 0;

    [Fact]
    public async Task A_failing_check_sends_the_type_errors_in_the_steps_own_files_back_to_the_worker()
    {
        WritePackageScripts("\"typecheck\": \"tsc --noEmit\"");
        var (plan, agent, commands, verify) = TypecheckPlan(TypecheckErrors);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        string retry = string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));
        Assert.Contains("Duplicate identifier 'history'", retry, StringComparison.Ordinal);
        Assert.Contains("src/a.ts(3,11)", retry, StringComparison.Ordinal);
        Assert.Contains("src/a.ts(9,3)", retry, StringComparison.Ordinal);
        Assert.Contains("The other declaration is here.", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot find name 'later'", retry, StringComparison.Ordinal);
        Assert.Equal(1, TypecheckHints(plan.Id));
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == TypecheckKind && runEvent.Detail.Contains("src/a.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_without_a_typecheck_script_is_not_type_checked()
    {
        WritePackageScripts("\"test\": \"node --test\"");
        var (plan, agent, commands, verify) = TypecheckPlan(TypecheckErrors);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.DoesNotContain(TypecheckCommand, commands);
        Assert.Equal(0, TypecheckHints(plan.Id));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_project_without_a_package_file_is_not_type_checked()
    {
        var (plan, agent, commands, verify) = TypecheckPlan(TypecheckErrors);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.DoesNotContain(TypecheckCommand, commands);
        Assert.Equal(0, TypecheckHints(plan.Id));
    }

    [Fact]
    public async Task Type_errors_only_in_files_the_step_does_not_list_give_no_hint()
    {
        WritePackageScripts("\"typecheck\": \"tsc --noEmit\"");
        var (plan, agent, commands, verify) = TypecheckPlan("Exit code: 2\n--- stdout ---\nsrc/b.ts(1,1): error TS2304: Cannot find name 'later'.");

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Contains(TypecheckCommand, commands);
        Assert.Equal(0, TypecheckHints(plan.Id));
        string retry = string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));
        Assert.DoesNotContain("Cannot find name 'later'", retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_check_that_passes_or_cannot_run_gives_no_hint_and_never_fails_the_step()
    {
        WritePackageScripts("\"typecheck\": \"tsc --noEmit\"");
        var (plan, agent, commands, verify) = TypecheckPlan("Exit code: 0\n--- stdout ---\n");

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Equal(0, TypecheckHints(plan.Id));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_step_whose_own_check_is_the_type_check_is_not_checked_twice()
    {
        WritePackageScripts("\"typecheck\": \"tsc --noEmit\"");
        SeedFiles("src/a.ts");
        PlanRecord plan = Store.Approve(Store.Create("One step", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Listed("write a", TypecheckCommand, "src/a.ts")]).Id)!;
        var commands = new List<string>();
        int writes = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("src/a.ts", writes++ == 0 ? "bad" : "good");
            return Task.FromResult("Done.");
        });
        string Verify(string command)
        {
            commands.Add(command);
            return File.ReadAllText(Path.Combine(ProjectDirectory, "src/a.ts")) == "good" ? Pass(command) : TypecheckErrors;
        }

        await RepairRunner(agent, Verify).RunPlanAsync(plan.Id, default);

        Assert.Equal(0, TypecheckHints(plan.Id));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Theory]
    [InlineData("{ \"scripts\": { \"typecheck\": \"tsc --noEmit\" } }", true)]
    [InlineData("{ \"scripts\": { \"test\": \"node --test\", \"typecheck\": \"tsc\" } }", true)]
    [InlineData("{ \"scripts\": { \"test\": \"node --test\" } }", false)]
    [InlineData("{ \"scripts\": { \"typecheck\": \"\" } }", false)]
    [InlineData("{ \"name\": \"p\" }", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void A_typecheck_script_is_found_in_the_package_file(string packageJson, bool expected) =>
        Assert.Equal(expected, PlanRunner.HasTypecheckScript(packageJson));

    [Fact]
    public void Only_the_type_errors_in_the_listed_files_are_kept_with_their_continuation_lines()
    {
        string[] kept = PlanRunner.TypecheckErrorLines(TypecheckErrors, path => path == "src/a.ts").ToArray();

        Assert.Equal(
        [
            "src/a.ts(3,11): error TS2300: Duplicate identifier 'history'.",
            "  The other declaration is here.",
            "src/a.ts(9,3): error TS2300: Duplicate identifier 'history'."
        ], kept);
    }
}
