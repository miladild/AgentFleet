namespace AgentFleet.Tests;

public sealed class CheckDefectsTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"fleet-defects-{Guid.NewGuid():N}");

    public CheckDefectsTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, "test"));
        File.WriteAllText(Path.Combine(_project, "package.json"), """{"scripts":{"dev":"tsx watch src/index.ts","test":"node --test"}}""");
    }

    public void Dispose() => Directory.Delete(_project, recursive: true);

    private static PlanStep StepNaming(params string[] files) =>
        new(1, "step", "detail", files, "node --test test/x.test.js", "standard", StepStatus.Pending, null, 0, null, null);

    [Fact]
    public void A_check_that_never_exits_is_a_defect_from_the_command_alone()
    {
        CheckDefect? defect = CheckDefects.FromCommand("npm run dev", _project);
        Assert.Equal(CheckDefectKind.NeverFinishes, defect?.Kind);
        Assert.Contains("does not exit", defect!.Evidence);
        Assert.Null(CheckDefects.FromCommand("npm test", _project));
    }

    [Theory]
    [InlineData("At line:1 char:15\n+ npm run build && npm test\nThe token '&&' is not a valid statement separator in this version.\n    + CategoryInfo : ParserError: (:) []")]
    [InlineData("The syntax of the command is incorrect.")]
    [InlineData("( was unexpected at this time.")]
    [InlineData("The filename, directory name, or volume label syntax is incorrect.")]
    [InlineData("sh: 1: Syntax error: \"&&\" unexpected")]
    [InlineData("bash: -c: line 1: syntax error near unexpected token `&&'")]
    public void A_command_the_shell_cannot_parse_is_a_defect(string output)
    {
        CheckDefect? defect = CheckDefects.FromOutput("npm run build && npm test", output, _project, [StepNaming()]);
        Assert.Equal(CheckDefectKind.ShellSyntax, defect?.Kind);
    }

    [Theory]
    [InlineData("SyntaxError: Unexpected token '}'")]
    [InlineData("src/app.ts(3,1): error TS1005: ';' expected.")]
    [InlineData("error CS1002: ; expected")]
    [InlineData("AssertionError [ERR_ASSERTION]: Expected values to be strictly equal")]
    [InlineData("# fail 2\nnot ok 1 - slug handles unicode")]
    [InlineData("Failed!  - Failed:     3, Passed:    40, Skipped:     0, Total:    43")]
    [InlineData("Tests: 3 failed, 40 passed, 43 total")]
    public void A_verdict_of_the_project_is_never_a_defect_of_the_check(string output) =>
        Assert.Null(CheckDefects.FromOutput("node --test", output, _project, [StepNaming()]));

    [Fact]
    public void A_program_of_the_check_that_is_not_there_is_a_defect_but_a_nested_one_or_a_runtime_is_not()
    {
        Assert.Equal(CheckDefectKind.ProgramMissing, CheckDefects.FromOutput("pytestt -q",
            "pytestt : The term 'pytestt' is not recognized as the name of a cmdlet, function, script file, or operable program.", _project, [StepNaming()])?.Kind);
        Assert.Equal(CheckDefectKind.ProgramMissing, CheckDefects.FromOutput("cd . && pytestt -q",
            "bash: line 1: pytestt: command not found", _project, [StepNaming()])?.Kind);
        Assert.Equal(CheckDefectKind.ProgramMissing, CheckDefects.FromOutput("pytestt -q",
            "'pytestt' is not recognized as an internal or external command,\noperable program or batch file.", _project, [StepNaming()])?.Kind);

        // jest is run by npm's script, not by the check: a missing dependency, for the doctor.
        Assert.Null(CheckDefects.FromOutput("npm test", "'jest' is not recognized as an internal or external command", _project, [StepNaming()]));
        // A missing runtime is a machine problem with its own handling.
        Assert.Null(CheckDefects.FromOutput("node x.js", "'node' is not recognized as an internal or external command", _project, [StepNaming()]));
    }

    [Fact]
    public void A_file_the_check_names_that_does_not_exist_and_no_step_creates_is_a_defect()
    {
        const string output = "Error: Cannot find module 'C:\\work\\test\\x.test.js'";
        CheckDefect? defect = CheckDefects.FromOutput("node --test test/x.test.js", output, _project, [StepNaming()]);
        Assert.Equal(CheckDefectKind.FileMissing, defect?.Kind);

        // Named by a step: the model was meant to write it, and has not yet.
        Assert.Null(CheckDefects.FromOutput("node --test test/x.test.js", output, _project, [StepNaming("test/x.test.js")]));

        // Present: the failure is something else.
        File.WriteAllText(Path.Combine(_project, "test", "x.test.js"), "");
        Assert.Null(CheckDefects.FromOutput("node --test test/x.test.js", output, _project, [StepNaming()]));
    }

    [Fact]
    public void A_check_stopped_at_its_time_limit_twice_with_the_same_output_is_a_sign()
    {
        const string first = "Error: command exceeded the 120s timeout and was killed.\n--- partial stdout ---\nlistening on 3000 (pid 4121)";
        const string second = "Error: command exceeded the 118.5s timeout and was killed.\n--- partial stdout ---\nlistening on 3000 (pid 5532)";
        Assert.Equal(CheckDefectKind.TimedOut, CheckDefects.TimedOutTwice(second, [first])?.Kind);
        Assert.Null(CheckDefects.TimedOutTwice(second, []));
        Assert.Null(CheckDefects.TimedOutTwice(second, ["Error: command exceeded the 120s timeout and was killed.\n--- partial stdout ---\nserver ready; waiting for tests"]));
        Assert.Null(CheckDefects.TimedOutTwice("Exit code: 1\nFAILED a", [first]));
        Assert.Equal(CheckDefectKind.TimedOut, CheckDefects.TimedOutTwice(
            "Worker workspace unavailable: verification or sync failed on worker-a: Operation has timed out.",
            ["Worker workspace unavailable: verification or sync failed on worker-a: Operation has timed out."])?.Kind);
    }

    [Fact]
    public void A_model_s_evidence_counts_only_when_a_line_of_it_is_in_the_output()
    {
        const string output = "Usage: tsc [options]\nerror:   unrecognised option\t--colour\nExit code: 1";
        Assert.True(CheckDefects.EvidenceMatches("unrecognised option --colour", output));
        Assert.True(CheckDefects.EvidenceMatches("my summary\nerror: unrecognised option --colour", output));
        Assert.False(CheckDefects.EvidenceMatches("this check is impossible to satisfy", output));
        Assert.False(CheckDefects.EvidenceMatches("error", output));
        Assert.False(CheckDefects.EvidenceMatches("", output));
        Assert.False(CheckDefects.EvidenceMatches(null, output));
    }

    [Fact]
    public void The_shell_is_described_the_way_the_machine_runs_it()
    {
        Assert.Contains("cmd.exe", CheckDefects.ShellDescription("windows", "npm run build && npm test"));
        Assert.Contains("PowerShell 5.1", CheckDefects.ShellDescription("windows", "npm test"));
        Assert.Contains("POSIX", CheckDefects.ShellDescription("linux", "npm test"));
        Assert.Contains("hub", CheckDefects.ShellDescription(null, "npm test"));
    }
}

public sealed class EnvironmentDoctorTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"fleet-doctor-{Guid.NewGuid():N}");

    public EnvironmentDoctorTests() => Directory.CreateDirectory(_project);

    public void Dispose() => Directory.Delete(_project, recursive: true);

    private void Write(string name, string content)
    {
        string path = Path.Combine(_project, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string Jest = """{"scripts":{"test":"jest"},"devDependencies":{"jest":"^29.0.0","@types/node":"^22.0.0","typescript":"^5.0.0"},"dependencies":{"express":"^4.0.0"}}""";

    [Fact]
    public void A_declared_tool_that_is_not_installed_gets_npm_ci_with_a_lock_file_and_npm_install_without()
    {
        Write("package.json", Jest);
        EnvironmentRepair? withoutLock = EnvironmentDoctor.Diagnose("'jest' is not recognized as an internal or external command", _project);
        Assert.Equal("npm install", withoutLock?.Command);
        Assert.Equal("node-modules", withoutLock?.Cause);

        Write("package-lock.json", "{}");
        Assert.Equal("npm ci", EnvironmentDoctor.Diagnose("sh: 1: jest: not found", _project)?.Command);
        Assert.Equal("npm ci", EnvironmentDoctor.Diagnose("The term 'tsc' is not recognized as the name of a cmdlet", _project)?.Command);
    }

    [Fact]
    public void A_declared_module_that_is_not_installed_is_repaired_and_an_undeclared_one_is_not()
    {
        Write("package.json", Jest);
        Write("package-lock.json", "{}");
        Assert.Equal("npm ci", EnvironmentDoctor.Diagnose("Error: Cannot find module 'express'\nRequire stack:", _project)?.Command);
        Assert.Equal("npm ci", EnvironmentDoctor.Diagnose("Error [ERR_MODULE_NOT_FOUND]: Cannot find package 'express' imported from /w/src/a.js", _project)?.Command);
        Assert.Equal("npm ci", EnvironmentDoctor.Diagnose("Error: Cannot find module '@types/node/fs'", _project)?.Command);

        Assert.Null(EnvironmentDoctor.Diagnose("Error: Cannot find module 'left-pad'", _project));
        Assert.Null(EnvironmentDoctor.Diagnose("Error: Cannot find module './src/missing.js'", _project));
        Assert.Null(EnvironmentDoctor.Diagnose("'rimraf' is not recognized as an internal or external command", _project));
        Assert.Null(EnvironmentDoctor.Diagnose("FAILED widget::renders", _project));
    }

    [Fact]
    public void A_project_with_yarn_or_pnpm_is_not_repaired_by_npm_and_the_advice_says_what_to_do()
    {
        Write("package.json", Jest);
        Write("yarn.lock", "");
        EnvironmentRepair? repair = EnvironmentDoctor.Diagnose("'jest' is not recognized as an internal or external command", _project);
        Assert.NotNull(repair);
        Assert.Null(repair.Command);
        Assert.Contains("yarn or pnpm", repair.Advice);
    }

    [Fact]
    public void Python_requirements_go_into_the_projects_own_virtual_environment_or_not_at_all()
    {
        Write("requirements.txt", "# web\nRequests==2.31.0\nPyYAML>=6\n-r other.txt\n");
        const string missing = "Traceback (most recent call last):\nModuleNotFoundError: No module named 'requests'";

        EnvironmentRepair? withoutEnvironment = EnvironmentDoctor.Diagnose(missing, _project, venvPython: null);
        Assert.NotNull(withoutEnvironment);
        Assert.Null(withoutEnvironment.Command);
        Assert.Contains("virtual environment", withoutEnvironment.Advice);

        Assert.Null(EnvironmentDoctor.VirtualEnvironmentPython(_project));
        Write(".venv/bin/python", "");
        string? python = EnvironmentDoctor.VirtualEnvironmentPython(_project);
        Assert.Equal(".venv/bin/python", python);
        Assert.Equal(".venv/bin/python -m pip install -r requirements.txt", EnvironmentDoctor.Diagnose(missing, _project, python)?.Command);
        // yaml is imported by the name PyYAML is installed under.
        Assert.Equal("python-requirements", EnvironmentDoctor.Diagnose("ModuleNotFoundError: No module named 'yaml'", _project, python)?.Cause);

        // A module requirements.txt does not list is the model's to add.
        Assert.Null(EnvironmentDoctor.Diagnose("ModuleNotFoundError: No module named 'numpy'", _project, python));
    }

    [Fact]
    public void A_dotnet_project_that_needs_a_restore_gets_one_and_only_when_it_has_a_project()
    {
        const string output = "error NETSDK1004: Assets file 'C:\\w\\obj\\project.assets.json' not found. Run a NuGet package restore to generate this file.";
        Assert.Null(EnvironmentDoctor.Diagnose(output, _project));
        Write("App.csproj", "<Project />");
        EnvironmentRepair? repair = EnvironmentDoctor.Diagnose(output, _project);
        Assert.Equal("dotnet restore", repair?.Command);
        Assert.Equal("dotnet-restore", repair?.Cause);
    }

    [Fact]
    public void A_missing_env_file_is_explained_and_never_created_by_the_fleet()
    {
        Assert.Null(EnvironmentDoctor.Diagnose("Error: ENOENT: no such file or directory, open '/w/.env'", _project));
        Write(".env.example", "API_KEY=changeme");
        EnvironmentRepair? repair = EnvironmentDoctor.Diagnose("Error: ENOENT: no such file or directory, open '/w/.env'", _project);
        Assert.Equal("env-file", repair?.Cause);
        Assert.Null(repair?.Command);
        Assert.Contains("copy .env.example to .env", repair!.Advice);
        Assert.Equal("env-file", EnvironmentDoctor.Diagnose("The .env file is missing", _project)?.Cause);
        Assert.Null(EnvironmentDoctor.Diagnose("FAILED widget::renders", _project));

        Write(".env", "API_KEY=real");
        Assert.Null(EnvironmentDoctor.Diagnose("Error: ENOENT: no such file or directory, open '/w/.env'", _project));
    }

    [Fact]
    public void Every_command_the_doctor_can_give_is_a_project_local_install_from_its_own_list()
    {
        Write("package.json", Jest);
        Write("requirements.txt", "requests");
        Write("App.csproj", "<Project />");
        Write(".venv/Scripts/python.exe", "");
        string?[] commands =
        [
            EnvironmentDoctor.Diagnose("'jest' is not recognized as an internal or external command", _project)?.Command,
            EnvironmentDoctor.Diagnose("ModuleNotFoundError: No module named 'requests'", _project, EnvironmentDoctor.VirtualEnvironmentPython(_project))?.Command,
            EnvironmentDoctor.Diagnose("NETSDK1004", _project)?.Command
        ];
        Assert.All(commands, command =>
        {
            Assert.NotNull(command);
            Assert.Matches(@"^(npm ci|npm install|dotnet restore|\.venv[/\\]Scripts[/\\]python\.exe -m pip install -r requirements\.txt)$", command);
            Assert.Null(PlanRunnerToolPolicy.CommandRefusal(command));
            Assert.DoesNotContain("-g ", command + " ");
            Assert.DoesNotContain("--global", command);
        });
    }
}

public sealed class CheckAuditTests
{
    private static PlanRecord Plan(int steps)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new PlanRecord("id", "Ship it", "goal", PlanStatus.Running, @"C:\work", [], [], [], null, null,
            Enumerable.Range(1, steps).Select(id => new PlanStep(id, $"step {id}", "do the thing", ["src/a.ts"], "npm run dev", "standard",
                StepStatus.Pending, null, 0, null, null)).ToList(), now, now, now);
    }

    private static StepAgentReply Reply(params AgentToolCall[] calls) => new("", calls.Length, false, calls);

    private static AgentToolCall Call(string name, params (string Key, string? Value)[] arguments) =>
        new(name, arguments.ToDictionary(argument => argument.Key, argument => argument.Value));

    [Fact]
    public void The_prompt_says_what_the_check_is_where_it_runs_and_why_it_is_suspect()
    {
        PlanRecord plan = Plan(2);
        string prompt = CheckAudit.BuildPrompt(plan, plan.Steps[0], "npm run dev",
            new CheckDefect(CheckDefectKind.NeverFinishes, "`npm run dev` runs a watcher."), "Error: timed out", "the hub, a Windows PC", hint: null);
        Assert.Contains("`npm run dev`", prompt);
        Assert.Contains("the hub, a Windows PC", prompt);
        Assert.Contains("never finishes", prompt);
        Assert.Contains("Error: timed out", prompt);
        Assert.Contains("propose_check", prompt);
        Assert.DoesNotContain("last step", prompt);

        string last = CheckAudit.BuildPrompt(plan, plan.Steps[1], "npm run dev", new CheckDefect(CheckDefectKind.ShellSyntax, "x"), null, "shell",
            new ModelBlocker("check_cannot_pass", "unrecognised option"));
        Assert.Contains("last step", last);
        Assert.Contains("The model doing the step reported: check_cannot_pass: unrecognised option", last);
    }

    [Fact]
    public void The_proposal_is_read_from_the_reply_and_only_the_first_counts()
    {
        Assert.Null(CheckAudit.ProposalFrom(new StepAgentReply("no defect", 0, false)));
        Assert.Null(CheckAudit.ProposalFrom(Reply(Call("read_file", ("path", "a")))));
        Assert.Null(CheckAudit.ProposalFrom(Reply(Call("propose_check", ("check", "  "), ("why", "x")))));

        (string check, string why) = CheckAudit.ProposalFrom(Reply(
            Call("read_file", ("path", "a")),
            Call("propose_check", ("check", " npm run build "), ("why", "dev never exits")),
            Call("propose_check", ("check", "echo ok"), ("why", "later"))))!.Value;
        Assert.Equal("npm run build", check);
        Assert.Equal("dev never exits", why);
        Assert.Equal("no reason given", CheckAudit.ProposalFrom(Reply(Call("propose_check", ("check", "npm test"))))!.Value.Why);
    }

    [Fact]
    public void A_reported_blocker_has_a_known_kind_and_only_the_first_report_counts()
    {
        Assert.Null(CheckAudit.BlockerFrom(new StepAgentReply("done", 1, true)));
        ModelBlocker first = CheckAudit.BlockerFrom(Reply(
            Call("report_blocker", ("kind", "Check_Cannot_Pass"), ("evidence", " the shell says no ")),
            Call("report_blocker", ("kind", "environment"), ("evidence", "second"))))!;
        Assert.Equal("check_cannot_pass", first.Kind);
        Assert.Equal("the shell says no", first.Evidence);
        Assert.Equal("other", CheckAudit.BlockerFrom(Reply(Call("report_blocker", ("kind", "my tests are hard"), ("evidence", "x"))))!.Kind);
        Assert.Equal(string.Empty, CheckAudit.BlockerFrom(Reply(Call("report_blocker", ("kind", "environment"))))!.Evidence);
    }
}

public sealed class CheckHealingToolPolicyTests
{
    [Theory]
    // A model carrying out a step sees report_blocker, never propose_check; the auditor the reverse; chat neither.
    [InlineData("report_blocker", true, null, true)]
    [InlineData("propose_check", true, null, false)]
    [InlineData("report_blocker", true, PlanRunnerToolPolicy.CheckAuditRole, false)]
    [InlineData("propose_check", true, PlanRunnerToolPolicy.CheckAuditRole, true)]
    [InlineData("report_blocker", false, null, false)]
    [InlineData("propose_check", false, null, false)]
    // The auditor reads and proposes: nothing that runs or changes anything.
    [InlineData("read_file", true, PlanRunnerToolPolicy.CheckAuditRole, true)]
    [InlineData("search_files", true, PlanRunnerToolPolicy.CheckAuditRole, true)]
    [InlineData("write_file", true, PlanRunnerToolPolicy.CheckAuditRole, false)]
    [InlineData("edit_file", true, PlanRunnerToolPolicy.CheckAuditRole, false)]
    [InlineData("run_command", true, PlanRunnerToolPolicy.CheckAuditRole, false)]
    [InlineData("web_fetch", true, PlanRunnerToolPolicy.CheckAuditRole, false)]
    [InlineData("write_file", true, null, true)]
    [InlineData("run_command", false, null, true)]
    public void Tools_are_offered_by_who_is_asking(string tool, bool fromRunner, string? role, bool offered) =>
        Assert.Equal(offered, PlanRunnerToolPolicy.Offers(tool, fromRunner, role));

    [Fact]
    public void The_auditor_is_refused_anything_but_reading_and_proposing_even_if_it_calls_it_anyway()
    {
        Assert.Null(PlanRunnerToolPolicy.Refusal("read_file", role: PlanRunnerToolPolicy.CheckAuditRole));
        Assert.Null(PlanRunnerToolPolicy.Refusal("propose_check", role: PlanRunnerToolPolicy.CheckAuditRole));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal("run_command", "npm test", PlanRunnerToolPolicy.CheckAuditRole));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal("write_file", role: PlanRunnerToolPolicy.CheckAuditRole));

        // A step's model may report but not propose.
        Assert.Null(PlanRunnerToolPolicy.Refusal("report_blocker"));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal("propose_check"));
    }
}

public sealed class CheckHealingStoreTests : PlanTestBase
{
    [Fact]
    public void The_broken_check_setting_defaults_to_automatic_and_a_later_approval_keeps_it_unless_told_otherwise()
    {
        PlanRecord plan = NewPlan();
        Assert.Null(plan.HealChecks);
        Assert.True(PlanHealChecks.IsAuto(plan.HealChecks));

        Assert.Equal(PlanHealChecks.Auto, Store.Approve(plan.Id)!.HealChecks);

        PlanRecord asking = NewPlan();
        Assert.Equal(PlanHealChecks.Ask, Store.Approve(asking.Id, healChecks: " ASK ")!.HealChecks);
        Store.Update(asking.Id, current => current with { Status = PlanStatus.Blocked });
        Assert.Equal(PlanHealChecks.Ask, Store.Approve(asking.Id)!.HealChecks);
        Store.Update(asking.Id, current => current with { Status = PlanStatus.Blocked });
        Assert.Equal(PlanHealChecks.Auto, Store.Approve(asking.Id, healChecks: "auto")!.HealChecks);

        PlanRecord nonsense = NewPlan();
        Assert.Equal(PlanHealChecks.Auto, Store.Approve(nonsense.Id, healChecks: "whatever")!.HealChecks);
    }

    [Fact]
    public void The_plan_and_the_report_show_what_the_fleet_changed_with_the_original_kept()
    {
        PlanRecord plan = NewPlan(Step("one", "npm run dev"));
        Store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, 1, step => step with { Verify = "npm run build", OriginalVerify = "npm run dev" }));
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.CheckHealed, "standard", detail: "The check was changed because the check never finishes: dev starts a server",
            checkBefore: "npm run dev", checkAfter: "npm run build");
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.EnvironmentRepaired, "standard", detail: "Install the project's declared dependencies. `npm ci` succeeded: added 120 packages",
            failureClass: "Repaired", failureSignature: "node-modules");
        PlanRecord after = Store.Get(plan.Id)!;

        string markdown = FleetPlanStore.ToMarkdown(after);
        Assert.Contains("Runner final validation: `npm run build`", markdown);
        Assert.Contains("Check changed by the fleet: it was `npm run dev`", markdown);

        string report = FleetPlanStore.ToReportMarkdown(after);
        Assert.Contains("## Checks changed by Fleet", report);
        Assert.Contains("`npm run dev` became `npm run build`", report);
        Assert.Contains("## Environment repairs", report);
        Assert.Contains("`npm ci` succeeded", report);
        Assert.Contains("Broken checks: fixed automatically", report);
        Assert.Contains("check: `npm run dev` -> `npm run build`", report);

        PlanRecord untouched = NewPlan();
        Assert.DoesNotContain("Checks changed by Fleet", FleetPlanStore.ToReportMarkdown(untouched));
    }

    [Fact]
    public void A_long_check_in_an_event_is_cut_and_the_step_keeps_the_whole_original()
    {
        PlanRecord plan = NewPlan();
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.CheckHealed, detail: "x", checkBefore: new string('a', 2000), checkAfter: "short");
        PlanRunEvent healed = Assert.Single(Store.Get(plan.Id)!.Events!);
        Assert.True(healed.CheckBefore!.Length < 700);
        Assert.Equal("short", healed.CheckAfter);
    }
}
