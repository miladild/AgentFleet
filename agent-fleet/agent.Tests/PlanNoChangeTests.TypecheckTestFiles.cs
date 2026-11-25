namespace AgentFleet.Tests;

/// <summary>
/// A project's type check can leave a step's own test files out (a tsconfig.json whose "include" lists only the source folder), and the
/// test run does not type check them, so a test that hands a constructor the wrong kind of value fails far from the cause. When a check
/// fails with a TypeError or a ReferenceError, the step's TypeScript test files are type-checked with the project's own compiler options
/// and the errors that name them go back to the worker with the failure.
/// </summary>
public sealed partial class PlanNoChangeTests
{
    private const string CompilerStart = "node node_modules/typescript/bin/tsc ";
    private const string CompilerPasses = "Exit code: 0\n--- stdout ---\n";

    private const string ProjectOptions = "{ \"compilerOptions\": { \"target\": \"ES2022\", \"module\": \"ESNext\", \"moduleResolution\": \"bundler\", " +
        "\"strict\": true, \"skipLibCheck\": true, \"noEmit\": true }, \"include\": [\"src/server\"] }";

    private const string ProjectFlags = "--target ES2022 --module ESNext --moduleResolution bundler --strict --skipLibCheck";

    private const string PriceTypeError = "Exit code: 1\n--- stdout ---\n✖ keeps the alert (3 ms)\n  TypeError: Cannot read properties of undefined (reading 'price')\nℹ fail 1";

    private const string TestFileTypeError = "test/a.test.ts(35,69): error TS2345: Argument of type 'PriceAlert' is not assignable to parameter of type 'AlertStore'.";

    private const string TestFileTypeErrorMore = "  Type 'PriceAlert' is missing the following properties from type 'AlertStore': filePath, folder, active, firedHistory, and 7 more.";

    // The compiler's report: two lines for the test file, one for a step file that the typecheck script reports, one for a file the step does not list.
    private const string TestFileErrors = "Exit code: 2\n--- stdout ---\n" + TestFileTypeError + "\n" + TestFileTypeErrorMore + "\n" +
        "src/a.ts(2,7): error TS2322: Type 'string' is not assignable to type 'number'.\n" +
        "src/other.ts(4,1): error TS2304: Cannot find name 'later'.";

    private const string OtherFileErrors = "Exit code: 2\n--- stdout ---\nsrc/other.ts(4,1): error TS2304: Cannot find name 'later'.";

    private const string TestFilesHintStart = "The step's test files, type-checked with the project's own compiler options, report errors:";

    // One step that lists src/a.ts and test/a.test.ts (or the given files). The check fails with checkFailure while a.ts says "bad" and
    // passes once it says "good"; the second round writes every listed file, so a listed test file counts as changed. The compiler prints
    // compilerOutput, or throws when that is null; the type check script prints scriptOutput.
    private (PlanRecord Plan, FakeStepAgent Agent, List<string> Commands, Func<string, string> Verify) TestFilesPlan(
        string? compilerOutput, string scriptOutput = CompilerPasses, string checkFailure = PriceTypeError, string[]? files = null)
    {
        SeedFiles("src/a.ts", "test/a.test.ts");
        string[] listed = files ?? ["src/a.ts", "test/a.test.ts"];
        PlanRecord plan = Store.Approve(Store.Create("One step", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Listed("write a", "check1", listed)]).Id)!;
        var commands = new List<string>();
        int writes = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("src/a.ts", writes == 0 ? "bad" : "good");
            if (writes++ > 0)
            {
                foreach (string file in listed.Where(file => file != "src/a.ts"))
                {
                    WriteProjectFile(file, "changed");
                }
            }

            return Task.FromResult("Done.");
        });
        string Verify(string command)
        {
            commands.Add(command);
            if (command == TypecheckCommand) return scriptOutput;
            if (command.StartsWith(CompilerStart, StringComparison.Ordinal))
            {
                return compilerOutput ?? throw new InvalidOperationException("The compiler could not start.");
            }

            return File.ReadAllText(Path.Combine(ProjectDirectory, "src/a.ts")) == "good" ? Pass(command) : checkFailure;
        }

        return (plan, agent, commands, Verify);
    }

    private static string RetryPrompts(FakeStepAgent agent) => string.Join('\n', agent.Calls.Skip(1).Select(call => call.Prompt));

    private static IEnumerable<string> CompilerRuns(List<string> commands) =>
        commands.Where(command => command.StartsWith(CompilerStart, StringComparison.Ordinal));

    [Fact]
    public async Task A_runtime_error_sends_back_the_type_errors_of_the_steps_test_files_that_the_projects_type_check_does_not_read()
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal($"{CompilerStart}--noEmit --incremental false {ProjectFlags} test/a.test.ts", Assert.Single(CompilerRuns(commands)));
        string retry = RetryPrompts(agent);
        Assert.Contains("The test run does not type check. The compiler, run on this step's test files with the project's own options, reports these errors", retry, StringComparison.Ordinal);
        Assert.Contains(TestFileTypeError, retry, StringComparison.Ordinal);
        Assert.Contains(TestFileTypeErrorMore, retry, StringComparison.Ordinal);
        Assert.DoesNotContain("src/a.ts(2,7)", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot find name 'later'", retry, StringComparison.Ordinal);
        Assert.Equal(1, TypecheckHints(plan.Id));
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == TypecheckKind &&
            runEvent.Detail.StartsWith(TestFilesHintStart + "\n" + TestFileTypeError, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reference_error_is_type_checked_too_and_a_project_that_sets_no_options_runs_the_compiler_with_none()
    {
        WriteProjectFile("tsconfig.json", "{ \"compilerOptions\": { \"noEmit\": true }, \"include\": [\"src\"] }");
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors,
            checkFailure: "Exit code: 1\n--- stdout ---\n  ReferenceError: alerts is not defined");

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Equal($"{CompilerStart}--noEmit --incremental false test/a.test.ts", Assert.Single(CompilerRuns(commands)));
        Assert.Contains(TestFileTypeError, RetryPrompts(agent), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Exit code: 1\n--- stdout ---\n✖ keeps the alert (3 ms)\n  AssertionError: 1 !== 2\nℹ fail 1")]
    [InlineData("Exit code: 1\n--- stdout ---\n  SyntaxError: Unexpected token '}'")]
    [InlineData("Exit code: 1\n--- stdout ---\n  MyTypeError: not the built-in error\n  typeerror in lower case\n  TypeErrors in the plural")]
    public async Task A_failure_that_is_not_a_type_error_or_a_reference_error_is_not_type_checked(string checkFailure)
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors, checkFailure: checkFailure);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Empty(CompilerRuns(commands));
        Assert.Equal(0, TypecheckHints(plan.Id));
        Assert.DoesNotContain(TestFileTypeError, RetryPrompts(agent), StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ \"compilerOptions\": { \"strict\": true, \"paths\": { \"@/*\": [\"src/*\"] } } }")]
    [InlineData("{ \"extends\": \"./tsconfig.base.json\", \"compilerOptions\": { \"strict\": true } }")]
    [InlineData("{ \"compilerOptions\": { \"target\": \"es5; echo x\" } }")]
    [InlineData("not json")]
    public async Task A_project_without_a_tsconfig_file_or_with_options_that_cannot_be_reproduced_is_not_type_checked(string? tsconfig)
    {
        if (tsconfig is not null) WriteProjectFile("tsconfig.json", tsconfig);
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Empty(CompilerRuns(commands));
        Assert.Equal(0, TypecheckHints(plan.Id));
        Assert.DoesNotContain(TestFileTypeError, RetryPrompts(agent), StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    // test/a.test.ts exists in the project but the step does not list it; the listed test is not a TypeScript file, is not there yet,
    // or has a space in its name.
    [Theory]
    [InlineData(null)]
    [InlineData("test/a.test.js")]
    [InlineData("test/b.test.ts")]
    [InlineData("test/a b.test.ts")]
    public async Task A_step_with_no_existing_typescript_test_file_it_can_name_is_not_type_checked(string? listedTest)
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        WriteProjectFile("test/a.test.js", "old");
        WriteProjectFile("test/a b.test.ts", "old");
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors, files: listedTest is null ? ["src/a.ts"] : ["src/a.ts", listedTest]);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Empty(CompilerRuns(commands));
        Assert.Equal(0, TypecheckHints(plan.Id));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task At_most_five_test_files_are_named_to_the_compiler_and_only_their_errors_are_kept()
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        string[] tests = Enumerable.Range(1, 7).Select(number => $"test/t{number}.test.ts").ToArray();
        SeedFiles(tests);
        var (plan, agent, commands, verify) = TestFilesPlan(
            "Exit code: 2\n--- stdout ---\ntest/t2.test.ts(1,1): error TS2322: Type 'string' is not assignable to type 'number'.\n" +
            "test/t7.test.ts(1,1): error TS2322: Type 'seventh' is not assignable to type 'number'.",
            files: ["src/a.ts", .. tests]);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Equal($"{CompilerStart}--noEmit --incremental false {ProjectFlags} test/t1.test.ts test/t2.test.ts test/t3.test.ts test/t4.test.ts test/t5.test.ts",
            Assert.Single(CompilerRuns(commands)));
        string retry = RetryPrompts(agent);
        Assert.Contains("test/t2.test.ts(1,1)", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("seventh", retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_file_the_step_lists_twice_is_named_to_the_compiler_once()
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors, files: ["src/a.ts", "test/a.test.ts", "./test/a.test.ts"]);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Equal($"{CompilerStart}--noEmit --incremental false {ProjectFlags} test/a.test.ts", Assert.Single(CompilerRuns(commands)));
    }

    [Fact]
    public async Task No_more_than_twenty_error_lines_of_the_test_files_go_back()
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        string errors = "Exit code: 2\n--- stdout ---\n" + string.Join('\n',
            Enumerable.Range(1, 25).Select(line => $"test/a.test.ts({line},1): error TS2322: Type 'string' is not assignable to type 'number'."));
        var (plan, agent, _, verify) = TestFilesPlan(errors);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        string retry = RetryPrompts(agent);
        Assert.Contains("test/a.test.ts(20,1)", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("test/a.test.ts(21,1)", retry, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CompilerPasses)]
    [InlineData(OtherFileErrors)]
    [InlineData("Exit code: 1\n--- stdout ---\nerror TS6053: File 'test/a.test.ts' not found.")]
    public async Task A_compiler_that_passes_or_names_no_test_file_gives_no_hint_and_never_fails_the_step(string compilerOutput)
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(compilerOutput);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Single(CompilerRuns(commands));
        Assert.Equal(0, TypecheckHints(plan.Id));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_compiler_that_cannot_run_gives_no_hint_and_never_fails_the_step()
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(compilerOutput: null);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Single(CompilerRuns(commands));
        Assert.Equal(0, TypecheckHints(plan.Id));
        Assert.DoesNotContain(TestFileTypeError, RetryPrompts(agent), StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_type_check_script_that_passes_does_not_hide_the_errors_of_the_test_files_it_does_not_read()
    {
        WritePackageScripts("\"typecheck\": \"tsc --noEmit\"");
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors, scriptOutput: CompilerPasses);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Contains(TypecheckCommand, commands);
        Assert.Single(CompilerRuns(commands));
        Assert.Contains(TestFileTypeError, RetryPrompts(agent), StringComparison.Ordinal);
        Assert.Equal(1, TypecheckHints(plan.Id));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_project_with_no_type_check_script_still_gets_the_errors_of_the_steps_test_files()
    {
        WritePackageScripts("\"test\": \"node --test\"");
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.DoesNotContain(TypecheckCommand, commands);
        Assert.Contains(TestFileTypeError, RetryPrompts(agent), StringComparison.Ordinal);
        Assert.Equal(1, TypecheckHints(plan.Id));
    }

    [Fact]
    public async Task A_step_whose_own_check_is_a_compiler_run_does_not_get_the_test_file_check_either()
    {
        WriteProjectFile("tsconfig.json", ProjectOptions);
        SeedFiles("src/a.ts", "test/a.test.ts");
        PlanRecord plan = Store.Approve(Store.Create("One step", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Listed("write a", "npx tsc --noEmit", "src/a.ts", "test/a.test.ts")]).Id)!;
        var commands = new List<string>();
        int writes = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("src/a.ts", writes == 0 ? "bad" : "good");
            if (writes++ > 0) WriteProjectFile("test/a.test.ts", "changed");
            return Task.FromResult("Done.");
        });
        string Verify(string command)
        {
            commands.Add(command);
            return File.ReadAllText(Path.Combine(ProjectDirectory, "src/a.ts")) == "good" ? Pass(command) : PriceTypeError;
        }

        await RepairRunner(agent, Verify).RunPlanAsync(plan.Id, default);

        Assert.Empty(CompilerRuns(commands));
        Assert.Equal(0, TypecheckHints(plan.Id));
    }

    [Fact]
    public async Task Both_hints_go_back_together_with_the_scripts_errors_first()
    {
        WritePackageScripts("\"typecheck\": \"tsc --noEmit\"");
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors, scriptOutput: TypecheckErrors);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        string retry = RetryPrompts(agent);
        int scriptErrors = retry.IndexOf("Duplicate identifier 'history'", StringComparison.Ordinal);
        int testFileErrors = retry.IndexOf(TestFileTypeError, StringComparison.Ordinal);
        Assert.True(scriptErrors >= 0 && testFileErrors > scriptErrors, "The script's errors come first, then the test files' errors.");
        Assert.Contains("The project's type check (`npm run typecheck`) rejects this step's files.", retry, StringComparison.Ordinal);
        Assert.Contains("The compiler, run on this step's test files with the project's own options", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("src/a.ts(2,7)", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("Cannot find name 'later'", retry, StringComparison.Ordinal);
        Assert.Equal(2, TypecheckHints(plan.Id));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    // The type check script may read the test files too (a tsconfig.json that includes them). Its errors for a test file are in its hint
    // already, so the compiler is not run on that file and the same error line is not sent twice.
    private const string ScriptReadsTheTest = "Exit code: 2\n--- stdout ---\ntest/a.test.ts(3,1): error TS2322: Type 'string' is not assignable to type 'number'.";

    [Fact]
    public async Task Errors_the_type_check_script_already_reported_for_a_test_file_are_not_sent_a_second_time()
    {
        WritePackageScripts("\"typecheck\": \"tsc --noEmit\"");
        WriteProjectFile("tsconfig.json", ProjectOptions);
        var (plan, agent, commands, verify) = TestFilesPlan(TestFileErrors, scriptOutput: ScriptReadsTheTest);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Empty(CompilerRuns(commands));
        string retry = RetryPrompts(agent);
        Assert.Contains("test/a.test.ts(3,1)", retry, StringComparison.Ordinal);
        Assert.DoesNotContain(TestFileTypeError, retry, StringComparison.Ordinal);
        Assert.DoesNotContain("The test run does not type check. The compiler, run on this step's test files", retry, StringComparison.Ordinal);
        Assert.Equal(1, TypecheckHints(plan.Id));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task Only_the_test_files_the_type_check_script_did_not_report_on_are_given_to_the_compiler()
    {
        WritePackageScripts("\"typecheck\": \"tsc --noEmit\"");
        WriteProjectFile("tsconfig.json", ProjectOptions);
        SeedFiles("test/b.test.ts");
        var (plan, agent, commands, verify) = TestFilesPlan(
            "Exit code: 2\n--- stdout ---\ntest/b.test.ts(7,3): error TS2339: Property 'addAlert' does not exist on type 'AlertStore'.",
            scriptOutput: ScriptReadsTheTest, files: ["src/a.ts", "test/a.test.ts", "test/b.test.ts"]);

        await RepairRunner(agent, verify).RunPlanAsync(plan.Id, default);

        Assert.Equal($"{CompilerStart}--noEmit --incremental false {ProjectFlags} test/b.test.ts", Assert.Single(CompilerRuns(commands)));
        string retry = RetryPrompts(agent);
        Assert.Contains("test/a.test.ts(3,1)", retry, StringComparison.Ordinal);
        Assert.Contains("test/b.test.ts(7,3)", retry, StringComparison.Ordinal);
        Assert.Equal(2, TypecheckHints(plan.Id));
    }
}
