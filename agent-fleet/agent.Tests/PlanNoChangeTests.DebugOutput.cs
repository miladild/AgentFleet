using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A passing check does not accept a step whose changed files still print debugging output: a print or log call that names DEBUG,
/// or a debugger statement. The worker gets one more round in the same conversation to remove those lines, once per attempt. The
/// step is accepted after that round even if lines remain, and the step-done note names them.
/// </summary>
public sealed partial class PlanNoChangeTests
{
    private const string DebugKind = "debug-output";

    private int DebugRounds(string planId) => Store.Get(planId)?.Events?.Count(runEvent => runEvent.Kind == DebugKind) ?? 0;

    [Fact]
    public async Task A_passing_step_that_left_a_debug_print_gets_one_more_round_that_names_the_file_and_line()
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            WriteProjectFile("app.js", round == 1 ? "const x = 1;\nconsole.log(\"DEBUG: x\", x);\n" : "const x = 1;\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Single(agent.Sessions);
        Assert.Equal(1, DebugRounds(plan.Id));
        string extra = agent.Sessions[0].Messages[1].Text;
        Assert.Contains("these lines print debugging output: app.js:2 console.log(\"DEBUG: x\", x);", extra, StringComparison.Ordinal);
        Assert.Contains("Remove them with edit_file", extra, StringComparison.Ordinal);
        Assert.Contains(after.Events!, e => e.Kind == DebugKind && e.Detail.Contains("app.js:2", StringComparison.Ordinal));
        Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.StepDone && e.Detail.Contains("Debug output still in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_debug_line_the_worker_keeps_after_the_round_is_accepted_and_the_step_done_note_names_it()
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            WriteProjectFile("app.js", round == 1
                ? "const x = 1;\nconsole.log(\"DEBUG: x\", x);\n"
                : "const x = 2;\nconsole.log(\"DEBUG: x\", x);\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, DebugRounds(plan.Id));
        Assert.Contains(after.Events!, e => e.Kind == RunEventKind.StepDone && e.Detail.Contains("Debug output still in: app.js:2.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_debug_round_is_given_once_per_attempt_and_lines_added_after_it_are_only_named_in_the_note()
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            WriteProjectFile("app.js", round == 1
                ? "const x = 1;\nconsole.log(\"DEBUG: x\", x);\n"
                : "const x = 1;\nconsole.log(\"DEBUG: x\", x);\nconsole.log(\"DEBUG: y\");\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, DebugRounds(plan.Id));
        Assert.Contains(after.Events!, e => e.Kind == RunEventKind.StepDone && e.Detail.Contains("Debug output still in: app.js:2, app.js:3.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("console.log(\"DEBUG: x\", x);")]
    [InlineData("console.debug(\"DEBUG\");")]
    [InlineData("console.error(\"DEBUG failed\");")]
    [InlineData("print(\"DEBUG: x\")")]
    [InlineData("println(\"DEBUG: x\")")]
    [InlineData("Console.WriteLine(\"DEBUG: x\");")]
    [InlineData("Debug.WriteLine(\"DEBUG: x\");")]
    [InlineData("logger.info(\"DEBUG: x\");")]
    [InlineData("    debugger;")]
    [InlineData("debugger")]
    public async Task A_print_or_log_call_naming_DEBUG_or_a_debugger_statement_gets_one_more_round(string line)
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            WriteProjectFile("app.js", round == 1 ? $"const x = 1;\n{line}\n" : "const x = 2;\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, DebugRounds(plan.Id));
        Assert.Contains($"app.js:2 {line.Trim()}", agent.Sessions[0].Messages[1].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("console.error(\"request failed\");")]
    [InlineData("logger.info(\"started\");")]
    [InlineData("Console.WriteLine(\"started\");")]
    [InlineData("println(\"started\")")]
    [InlineData("console.log")]
    [InlineData("const DEBUG = true;")]
    [InlineData("// DEBUG: temporary note")]
    [InlineData("debugger_mode = false;")]
    public async Task A_line_without_a_print_call_naming_the_uppercase_word_DEBUG_gets_no_round(string line)
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("app.js", $"const x = 2;\n{line}\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    // Measured: a worker left console.log("DELETE handler called") lines in a route file; none of them named DEBUG, so the check let them pass.
    [Theory]
    [InlineData("console.log(\"DELETE handler called, id:\", id);")]
    [InlineData("console.log(\"started\");")]
    [InlineData("print(\"here\")")]
    [InlineData("if (ok) console.log(x);")]
    public async Task A_print_call_the_step_added_to_a_file_that_existed_before_it_gets_one_more_round(string line)
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            WriteProjectFile("app.js", round == 1 ? $"const x = 1;\n{line}\n" : "const x = 2;\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, DebugRounds(plan.Id));
        Assert.Contains($"app.js:2 {line.Trim()}", agent.Sessions[0].Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_print_call_the_file_already_had_when_the_step_began_gets_no_round()
    {
        WriteProjectFile("app.js", "console.log(\"listening\");\nconst x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("app.js", "console.log(\"listening\");\nconst x = 2;\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task A_print_call_in_a_file_the_step_created_gets_no_round()
    {
        PlanRecord plan = OneStepPlan(StepListing("add the server", "server.js"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("server.js", "console.log(\"listening\");\n");
            return Task.FromResult("Created server.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task A_print_call_added_to_a_file_the_step_does_not_list_is_judged_against_that_files_earlier_version()
    {
        WriteProjectFile("other.js", "console.log(\"old\");\nconst y = 1;\n");
        WriteProjectFile("a.js", "const a = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            WriteProjectFile("a.js", $"const a = {round + 1};\n");
            WriteProjectFile("other.js", round == 1 ? "console.log(\"old\");\nconsole.log(\"new\");\nconst y = 1;\n" : "console.log(\"old\");\nconst y = 1;\n");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(1, DebugRounds(plan.Id));
        string extra = agent.Sessions[0].Messages[1].Text;
        Assert.Contains("other.js:2 console.log(\"new\");", extra, StringComparison.Ordinal);
        Assert.DoesNotContain("other.js:1", extra, StringComparison.Ordinal);
    }

    // A project whose git status leaves one file out (it was clean when the step began), so its earlier version comes from the commit.
    private PlanRunner RunnerWithACleanFile(FakeStepAgent agent, string cleanFile, Func<string, string> show, Func<bool> changedByNow)
    {
        string project = ProjectDirectory;
        var tools = new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(command switch
        {
            "git rev-parse --show-toplevel" => $"Exit code: 0\n--- stdout ---\n{project}",
            "git rev-parse HEAD" => "Exit code: 0\n--- stdout ---\nabc123",
            _ when command.Contains("--untracked-files=all", StringComparison.Ordinal) =>
                "Exit code: 0\n--- stdout ---\n" + string.Join("\n",
                    Directory.GetFiles(project, "*", SearchOption.AllDirectories)
                        .Select(file => Path.GetRelativePath(project, file).Replace('\\', '/'))
                        .Where(file => file != cleanFile || changedByNow())
                        .Select(file => "?? " + file)),
            _ when command.StartsWith("git show ", StringComparison.Ordinal) => show(command),
            _ when command.StartsWith("git ", StringComparison.Ordinal) => "Exit code: 0\n--- stdout ---\n",
            _ => Pass("check")
        }));
        return new PlanRunner(Store, tools, agent, NullLogger.Instance, transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task A_file_that_was_clean_when_the_step_began_is_judged_against_the_commit()
    {
        WriteProjectFile("other.js", "console.log(\"old\");\nconst y = 1;\n");
        WriteProjectFile("a.js", "const a = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.js"));
        var commands = new List<string>();
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            WriteProjectFile("a.js", $"const a = {round + 1};\n");
            WriteProjectFile("other.js", round == 1 ? "console.log(\"old\");\nconsole.log(\"new\");\nconst y = 1;\n" : "console.log(\"old\");\nconst y = 1;\n");
            return Task.FromResult("Changed a.");
        });

        await RunnerWithACleanFile(agent, "other.js", command =>
        {
            commands.Add(command);
            return "Exit code: 0\n--- stdout ---\nconsole.log(\"old\");\nconst y = 1;\n";
        }, () => round > 0).RunPlanAsync(plan.Id, default);

        Assert.Contains("git show abc123:\"other.js\"", commands);
        Assert.Equal(1, DebugRounds(plan.Id));
        string extra = agent.Sessions[0].Messages[1].Text;
        Assert.Contains("other.js:2 console.log(\"new\");", extra, StringComparison.Ordinal);
        Assert.DoesNotContain("other.js:1", extra, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_whose_earlier_version_cannot_be_found_is_not_checked_for_new_print_calls_and_the_log_says_so()
    {
        WriteProjectFile("other.js", "const y = 1;\n");
        WriteProjectFile("a.js", "const a = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.js"));
        bool changed = false;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            changed = true;
            WriteProjectFile("a.js", "const a = 2;\n");
            WriteProjectFile("other.js", "console.log(\"new\");\nconst y = 1;\n");
            return Task.FromResult("Changed a.");
        });

        await RunnerWithACleanFile(agent, "other.js", _ => "Exit code: 128\n--- stderr ---\nfatal: path does not exist", () => changed)
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(0, DebugRounds(plan.Id));
        Assert.Contains(after.Events!, e => e.Kind == "debug-output-baseline-missing" && e.Detail.Contains("other.js", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("abc123", "src/a.js", true)]
    [InlineData("zzzz", "src/a.js", false)]
    [InlineData("abc123", "src/a.js\"; rm -rf x", false)]
    [InlineData("abc123", "a`b.js", false)]
    [InlineData("abc123", "", false)]
    public async Task A_file_is_read_at_a_commit_only_for_a_plain_commit_name_and_path(string commit, string path, bool runs)
    {
        var commands = new List<string>();
        var tools = new PlanTools(Store, Valid, (command, _, _) =>
        {
            commands.Add(command);
            return Task.FromResult("Exit code: 0\n--- stdout ---\nthe text\n");
        });
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.js"));

        string? text = await tools.ReadAtCommitAsync(plan, commit, path, default);

        Assert.Equal(runs ? "the text" : null, text);
        Assert.Equal(runs ? [$"git show {commit}:\"{path}\""] : [], commands);
    }

    [Fact]
    public void The_new_print_check_is_on_unless_a_setting_turns_it_off()
    {
        Assert.True(new HarnessOptions().EnableNewPrintCheck);
        Assert.False(HarnessOptions.FromSettings(name => name == "FLEET_HARNESS_ENABLE_NEW_PRINT_CHECK" ? "false" : null).EnableNewPrintCheck);
        Assert.Contains("EnableNewPrintCheck=on", new HarnessOptions().Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("add request logging to the route")]
    [InlineData("print the result to the console")]
    [InlineData("Add a Logger")]
    public async Task A_step_that_asks_for_logging_or_printing_gets_no_round_for_a_new_print_call(string title)
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing(title, "app.js"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("app.js", "const x = 1;\nconsole.log(\"request\");\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task A_step_that_only_mentions_output_still_gets_the_round()
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("return JSON output from the route", "app.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            WriteProjectFile("app.js", round == 1 ? "const x = 1;\nconsole.log(\"here\");\n" : "const x = 2;\n");
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(1, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task With_the_new_print_check_switched_off_only_the_DEBUG_rule_applies()
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("app.js", "const x = 1;\nconsole.log(\"here\");\n");
            return Task.FromResult("Changed app.");
        });
        var runner = new PlanRunner(Store, RepairTools(Pass), agent, NullLogger.Instance, transientDelay: _ => TimeSpan.Zero,
            delayAsync: (_, _) => Task.CompletedTask, harness: new HarnessOptions(EnableNewPrintCheck: false));

        await runner.RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task A_test_file_that_prints_debug_output_gets_no_round()
    {
        WriteProjectFile("app.test.js", "test('x', () => {});\n");
        PlanRecord plan = OneStepPlan(StepListing("edit test", "app.test.js"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("app.test.js", "test('x', () => { console.log(\"DEBUG: x\"); });\n");
            return Task.FromResult("Changed test.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task A_changed_markdown_file_that_shows_debug_output_in_a_code_sample_gets_no_round()
    {
        WriteProjectFile("README.md", "# Notes\n");
        PlanRecord plan = OneStepPlan(StepListing("document logging", "README.md"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("README.md", "# Notes\n\n```js\nconsole.log(\"DEBUG: x\");\n```\n");
            return Task.FromResult("Documented.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task A_file_the_step_did_not_change_is_not_read_for_debug_output()
    {
        WriteProjectFile("other.js", "console.log(\"DEBUG: old\");\n");
        WriteProjectFile("a.js", "const a = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.js"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("a.js", "const a = 2;\n");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task A_changed_file_inside_a_dependency_folder_is_not_read_for_debug_output()
    {
        WriteProjectFile("a.js", "const a = 1;\n");
        WriteProjectFile("node_modules/pkg/index.js", "module.exports = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.js"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("a.js", "const a = 2;\n");
            WriteProjectFile("node_modules/pkg/index.js", "console.log(\"DEBUG: vendored\");\n");
            return Task.FromResult("Changed a.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task Only_the_first_forty_changed_files_are_read_for_debug_output()
    {
        string[] files = Enumerable.Range(1, 45).Select(number => $"f{number:00}.js").ToArray();
        foreach (string file in files) WriteProjectFile(file, "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit all", files));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            foreach (string file in files)
            {
                WriteProjectFile(file, round == 1 ? "console.log(\"DEBUG: x\");\n" : "const x = 2;\n");
            }

            return Task.FromResult("Changed all.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Contains("; and 30 more.", agent.Sessions[0].Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_ten_debug_lines_name_ten_and_count_the_rest()
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            var text = new System.Text.StringBuilder("const x = 1;\n");
            if (round == 1)
            {
                for (int number = 1; number <= 12; number++)
                {
                    text.Append($"console.log(\"DEBUG: {number}\");\n");
                }
            }
            else
            {
                text.Append("const x = 2;\n");
            }

            WriteProjectFile("app.js", text.ToString());
            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        string extra = agent.Sessions[0].Messages[1].Text;
        Assert.Contains("app.js:11 console.log(\"DEBUG: 10\");", extra, StringComparison.Ordinal);
        Assert.Contains("; and 2 more.", extra, StringComparison.Ordinal);
        Assert.DoesNotContain("app.js:12 ", extra, StringComparison.Ordinal);
        Assert.DoesNotContain("app.js:13 ", extra, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_final_check_of_a_longer_plan_gets_the_debug_round()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "setup.json"), "");
        WriteProjectFile("validate.js", "const v = 1;\n");
        PlanRecord plan = Store.Approve(Store.Create("Two-step plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [
                StepNamed("setup", "setup.json"),
                StepListing("validate", "validate.js")
            ]).Id)!;
        int validating = 0;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("(1 of 2)", StringComparison.Ordinal))
            {
                WriteProjectFile("setup.json", "{}");
                return Task.FromResult("Set it up.");
            }

            validating++;
            WriteProjectFile("validate.js", validating == 1
                ? "const v = 1;\nconsole.log(\"DEBUG: v\");\n"
                : "const v = 2;\n");
            return Task.FromResult("Validated.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Equal(1, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task The_tests_step_of_test_first_mode_gets_no_debug_round()
    {
        PlanRecord plan = OneStepPlan(new PlanStepInput("Write limits tests", "Reject zero", ["tests/LimitTests.cs"], "dotnet test", "standard",
            Kind: "tests"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            WriteProjectFile("tests/LimitTests.cs", "Console.WriteLine(\"DEBUG: limit\");\n");
            return Task.FromResult("Wrote the tests.");
        });
        var runner = new PlanRunner(Store, RepairTools(_ => TestsFailing), new TestWriterAgent(agent), NullLogger.Instance,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, testFirst: TestFirstMode.On);

        await runner.RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        Assert.Single(agent.Calls);
        Assert.Equal(0, DebugRounds(plan.Id));
    }

    [Fact]
    public async Task The_unlisted_rule_goes_first_and_the_debug_rule_applies_on_the_next_passing_check()
    {
        WriteProjectFile("app.js", "const x = 1;\n");
        PlanRecord plan = OneStepPlan(StepListing("edit app", "app.js"));
        int round = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            if (round == 1)
            {
                WriteProjectFile("app.js", "const x = 2;\nconsole.log(\"DEBUG: x\", x);\n");
                WriteProjectFile("scratch.js");
            }
            else if (round == 2)
            {
                WriteProjectFile("app.js", "const x = 3;\nconsole.log(\"DEBUG: x\", x);\n");
                File.Delete(Path.Combine(ProjectDirectory, "scratch.js"));
            }
            else
            {
                WriteProjectFile("app.js", "const x = 4;\n");
            }

            return Task.FromResult("Changed app.");
        });

        await RepairRunner(agent, _ => Pass("check")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Equal(1, UnlistedRounds(plan.Id));
        Assert.Equal(1, DebugRounds(plan.Id));
        Assert.Contains("were created and are not part of this step: scratch.js", agent.Sessions[0].Messages[1].Text, StringComparison.Ordinal);
        Assert.Contains("these lines print debugging output: app.js:2", agent.Sessions[0].Messages[2].Text, StringComparison.Ordinal);
    }
}
