namespace AgentFleet.Tests;

/// <summary>
/// Healing what is not the work: a check that is broken (rather than failing) is healed by an audit and a guard, and a
/// machine without the project's own dependencies gets them installed, both without spending a round. Fake agent, fake hub
/// (the auditor is the agent's own scripted answer), real project folder.
/// </summary>
public sealed partial class PlanRunnerTests
{
    private const string BuildOnly = """{"scripts":{"dev":"tsx watch src/index.ts","build":"tsc"}}""";
    private const string BuildAndTest = """{"scripts":{"dev":"tsx watch src/index.ts","build":"tsc","test":"node --test"}}""";

    private const string ParserError =
        "Exit code: 1\n--- stdout ---\n\n--- stderr ---\nAt line:1 char:15\n+ npm run build && npm test\n+               ~~\n" +
        "The token '&&' is not a valid statement separator in this version.\n    + CategoryInfo          : ParserError: (:) [], ParentContainsErrorRecordException";

    private void WriteProject(string packageJson, params string[] otherFiles)
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "package.json"), packageJson);
        foreach (string file in otherFiles)
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, file), "x");
        }
    }

    private PlanRecord HealPlan(string verify, string? healChecks = null, string scope = PlanRecoveryScope.AllowHubRescue, string? machine = null,
        string[]? files = null)
    {
        PlanRecord plan = Store.Create("Heal", "Make the project work", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("Run the project", "Make it work", files ?? [], verify, "standard")]);
        if (machine is not null)
        {
            plan = Store.SelectMachine(plan.Id, 1, machine, out string? error)!;
            Assert.Null(error);
        }

        return Store.Approve(plan.Id, recoveryScope: scope, healChecks: healChecks)!;
    }

    private static FakeStepAgent Healer(Func<FakeAuditCall, StepAgentReply> auditor) =>
        new((_, _, _) => Task.FromResult("Did the work."))
        {
            Auditor = auditor
        };

    [Fact]
    public async Task A_saved_check_that_never_finishes_is_healed_before_the_step_starts_and_costs_no_attempt()
    {
        WriteProject(BuildOnly);
        PlanRecord plan = HealPlan("npm run dev");
        FakeStepAgent agent = Healer(_ => FakeStepAgent.Proposes("npm run build", "npm run dev starts a server and never exits"));
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        List<string> commands = [];

        await RepairRunner(agent, command =>
        {
            commands.Add(command);
            return command == "npm run build" ? Pass(command) : FailWith("npm run dev never ends");
        }, options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("npm run build", after.Steps[0].Verify);
        Assert.Equal("npm run dev", after.Steps[0].OriginalVerify);
        Assert.Equal(1, after.Steps[0].Attempts);
        Assert.Equal(["npm run build"], commands);
        PlanRunEvent healed = Assert.Single(after.Events!, e => e.Kind == RunEventKind.CheckHealed);
        Assert.Equal("npm run dev", healed.CheckBefore);
        Assert.Equal("npm run build", healed.CheckAfter);
        Assert.Contains("never finishes", healed.Detail);
        Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.CheckFailed);
        Assert.DoesNotContain(after.Events!, e => RepairLadder.IsVerdictRound(e));

        // The hub audited, with the check, why it is suspect and where it runs; the step's own model got one message.
        FakeAuditCall audit = Assert.Single(agent.Audits);
        Assert.Equal(PlanRunnerToolPolicy.CheckAuditRole, audit.Role);
        Assert.Equal("heavy", audit.Tier);
        Assert.Equal("hub", audit.Machine);
        Assert.Contains("`npm run dev`", audit.Message);
        Assert.Contains("does not exit on its own", audit.Message);
        Assert.Contains("It runs on:", audit.Message);
        Assert.Single(agent.Calls);
    }

    [Fact]
    public async Task A_check_the_shell_cannot_parse_is_rewritten_with_the_same_program_and_arguments_in_the_same_round()
    {
        WriteProject(BuildAndTest);
        PlanRecord plan = HealPlan("npm run build && npm test");
        FakeStepAgent agent = Healer(_ => FakeStepAgent.Proposes("npm run build; if ($?) { npm test }", "PowerShell 5.1 has no &&"));
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        List<string> commands = [];

        await RepairRunner(agent, command =>
        {
            commands.Add(command);
            return command.Contains("&&", StringComparison.Ordinal) ? ParserError : Pass(command);
        }, options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("npm run build; if ($?) { npm test }", after.Steps[0].Verify);
        Assert.Equal("npm run build && npm test", after.Steps[0].OriginalVerify);
        Assert.Equal(["npm run build && npm test", "npm run build; if ($?) { npm test }"], commands);
        PlanRunEvent healed = Assert.Single(after.Events!, e => e.Kind == RunEventKind.CheckHealed);
        Assert.Contains("Same programs and arguments", healed.Detail);
        Assert.Equal(1, after.Steps[0].Attempts);
        Assert.Single(agent.Calls);
        Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.CheckFailed);
        Assert.Contains("ParserError", Assert.Single(agent.Audits).Message);
    }

    [Fact]
    public async Task A_proposal_that_passes_without_the_work_is_refused_once_with_the_reason_and_the_step_parks()
    {
        WriteProject(BuildOnly);
        PlanRecord plan = HealPlan("npm run dev");
        FakeStepAgent agent = Healer(_ => FakeStepAgent.Proposes("echo ok"));
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        await RepairRunner(agent, Pass, options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
        Assert.Equal("npm run dev", after.Steps[0].Verify);
        Assert.Null(after.Steps[0].OriginalVerify);
        Assert.Empty(agent.Calls);
        Assert.Equal(2, agent.Audits.Count);
        Assert.Contains("The fleet refused `echo ok`", agent.Audits[1].Message);
        Assert.Equal(2, after.Events!.Count(e => e.Kind == RunEventKind.CheckAudit && e.FailureClass == "Refused"));
        Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.CheckHealed);
        PlanRunEvent blocked = Assert.Single(after.Events!, e => e.Kind == RunEventKind.PlanBlocked);
        Assert.Contains("could not change it by itself", blocked.Detail);
        Assert.Contains("echo ok", blocked.Detail);
    }

    [Fact]
    public async Task A_refused_proposal_gets_one_more_try_and_a_good_second_proposal_is_applied()
    {
        WriteProject(BuildOnly);
        PlanRecord plan = HealPlan("npm run dev");
        FakeStepAgent agent = Healer(call => call.Number == 1 ? FakeStepAgent.Proposes("echo ok") : FakeStepAgent.Proposes("npm run build"));
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        await RepairRunner(agent, command => command == "npm run build" ? Pass(command) : FailWith("no"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("npm run build", after.Steps[0].Verify);
        Assert.Equal(2, agent.Audits.Count);
        Assert.Contains("only prints, waits", agent.Audits[1].Message);
    }

    [Fact]
    public async Task A_check_that_failed_on_its_merits_is_never_audited_or_edited()
    {
        PlanRecord plan = RepairPlan(machine: null, PlanRecoveryScope.AllowHubRescue);
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        int calls = 0;
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, ++calls == 1 ? "first try" : "fixed");
            return Task.FromResult("Worked on it.");
        })
        {
            Auditor = _ => FakeStepAgent.Proposes("echo ok")
        };

        // Output full of the words a broken check would have, none of which come from the shell: the project's own failures.
        await RepairRunner(agent, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : "Exit code: 1\n--- stdout ---\nSyntaxError: Unexpected token '}'\nerror TS1005: ';' expected.\nAssertionError [ERR_ASSERTION]: expected 3 to equal 4\n" +
              "FAILED widget::renders\nCannot find module './missing' (a require in the model's own code)", options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Empty(agent.Audits);
        Assert.Equal("dotnet build", after.Steps[0].Verify);
        Assert.Null(after.Steps[0].OriginalVerify);
        Assert.DoesNotContain(after.Events!, e => e.Kind is RunEventKind.CheckHealed or RunEventKind.CheckAudit or RunEventKind.EnvironmentRepaired);
    }

    [Fact]
    public async Task A_plan_set_to_ask_parks_the_step_with_the_proposal_and_heals_when_approved_to_fix_automatically()
    {
        WriteProject(BuildOnly);
        PlanRecord plan = HealPlan("npm run dev", healChecks: PlanHealChecks.Ask);
        FakeStepAgent agent = Healer(_ => FakeStepAgent.Proposes("npm run build", "npm run dev never exits"));
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        Func<string, string> verify = command => command == "npm run build" ? Pass(command) : FailWith("no");

        await RepairRunner(agent, verify, options, health).RunPlanAsync(plan.Id, default);

        PlanRecord parked = Store.Get(plan.Id)!;
        Assert.Equal(PlanHealChecks.Ask, parked.HealChecks);
        Assert.Equal(PlanStatus.Blocked, parked.Status);
        Assert.Equal("npm run dev", parked.Steps[0].Verify);
        Assert.Empty(agent.Calls);
        PlanRunEvent asked = Assert.Single(parked.Events!, e => e.Kind == RunEventKind.CheckAudit && e.FailureClass == "Asked");
        Assert.Contains("`npm run build`", asked.Detail);
        Assert.Contains("npm run build", parked.Steps[0].Note);
        Assert.DoesNotContain(parked.Events!, e => e.Kind == RunEventKind.CheckHealed);

        // The user answers by approving again with the fix-automatically setting.
        Store.Approve(plan.Id, healChecks: PlanHealChecks.Auto);
        await RepairRunner(agent, verify, options, health).RunPlanAsync(plan.Id, default);

        PlanRecord done = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, done.Status);
        Assert.Equal("npm run build", done.Steps[0].Verify);
        Assert.Equal("npm run dev", done.Steps[0].OriginalVerify);
    }

    [Fact]
    public async Task A_step_whose_check_was_already_changed_twice_stops_instead_of_changing_it_again()
    {
        WriteProject(BuildAndTest);
        PlanRecord plan = HealPlan("npm run build && npm test");
        string[] proposals = ["npm run build; npm test", "npm run build; if ($?) { npm test }", "cmd /c \"npm run build && npm test\""];
        FakeStepAgent agent = Healer(call => FakeStepAgent.Proposes(proposals[agentAuditNumber++]));
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        // Every form of the check is one the shell rejects, so each heal is followed by another defect.
        await RepairRunner(agent, command => command.Contains("&&", StringComparison.Ordinal) || command.Contains(';') ? ParserError : Pass(command),
            options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(2, agent.Audits.Count);
        Assert.Equal(2, after.Events!.Count(e => e.Kind == RunEventKind.CheckHealed));
        Assert.Equal("npm run build; if ($?) { npm test }", after.Steps[0].Verify);
        Assert.Equal("npm run build && npm test", after.Steps[0].OriginalVerify);
        PlanRunEvent blocked = Assert.Single(after.Events!, e => e.Kind == RunEventKind.PlanBlocked);
        Assert.Contains("already changed 2 times", blocked.Detail);
    }

    private int agentAuditNumber;

    [Fact]
    public async Task With_worker_only_scope_the_audit_goes_to_the_worker_that_did_the_work_and_never_the_hub()
    {
        WriteProject(BuildOnly);
        PlanRecord plan = HealPlan("npm run dev", scope: PlanRecoveryScope.WorkerOnly, machine: "worker-a");
        FakeStepAgent agent = Healer(_ => FakeStepAgent.Proposes("npm run build"));
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        await RepairRunner(agent, command => command == "npm run build" ? Pass(command) : FailWith("no"), options, health).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
        FakeAuditCall audit = Assert.Single(agent.Audits);
        Assert.Equal("standard", audit.Tier);
        Assert.Equal("worker-a", audit.Machine);
    }

    [Fact]
    public async Task An_audit_that_cannot_run_parks_a_check_that_can_never_pass_and_says_so()
    {
        WriteProject(BuildOnly);
        PlanRecord plan = HealPlan("npm run dev");
        FakeStepAgent agent = Healer(_ => throw new HttpRequestException("the hub is down"));
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        await RepairRunner(agent, Pass, options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal("npm run dev", after.Steps[0].Verify);
        Assert.Contains(after.Events!, e => e.Kind == RunEventKind.CheckAudit && e.FailureClass == "Unavailable" && e.Detail.Contains("the hub is down"));
        Assert.Contains("the hub is down", Assert.Single(after.Events!, e => e.Kind == RunEventKind.PlanBlocked).Detail);
    }

    [Fact]
    public async Task A_model_that_reports_the_check_cannot_pass_triggers_an_audit_only_when_the_output_says_so()
    {
        WriteProject(BuildOnly);
        PlanRecord plan = HealPlan("npm run build -- --colour", files: ["a.cs"]);
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, "work");
            return Task.FromResult("Did the work.");
        })
        {
            CallsOf = _ =>
            [
                new AgentToolCall("report_blocker", new Dictionary<string, string?> { ["kind"] = "check_cannot_pass", ["evidence"] = "unrecognised option --colour" }),
                new AgentToolCall("report_blocker", new Dictionary<string, string?> { ["kind"] = "environment", ["evidence"] = "a second report in the same turn" })
            ],
            Auditor = _ => FakeStepAgent.Proposes("npm run build", "the build has no --colour option")
        };
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        await RepairRunner(agent, command => command == "npm run build"
            ? Pass(command)
            : "Exit code: 1\n--- stdout ---\nUsage: tsc [options]\nerror: unrecognised option --colour", options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("npm run build", after.Steps[0].Verify);
        PlanRunEvent reported = Assert.Single(after.Events!, e => e.Kind == RunEventKind.BlockerReported);
        Assert.Contains("counts as a hint", reported.Detail);
        Assert.Contains("The model doing the step reported: check_cannot_pass", Assert.Single(agent.Audits).Message);
        Assert.Equal(1, after.Steps[0].Attempts);
    }

    [Fact]
    public async Task A_report_the_output_does_not_support_is_recorded_and_not_acted_on_and_the_round_counts()
    {
        PlanRecord plan = RepairPlan(machine: null, PlanRecoveryScope.AllowHubRescue);
        int calls = 0;
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, ++calls == 1 ? "first try" : "fixed");
            return Task.FromResult("Worked on it.");
        })
        {
            CallsOf = _ => [new AgentToolCall("report_blocker", new Dictionary<string, string?> { ["kind"] = "check_cannot_pass", ["evidence"] = "this check is impossible to satisfy" })],
            Auditor = _ => FakeStepAgent.Proposes("echo ok")
        };
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        await RepairRunner(agent, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Empty(agent.Audits);
        Assert.Contains("was not acted on", Assert.Single(after.Events!, e => e.Kind == RunEventKind.BlockerReported).Detail);
        Assert.Contains(after.Events!, e => RepairLadder.IsVerdictRound(e));
    }

    [Fact]
    public async Task A_check_that_timed_out_twice_with_the_same_output_is_audited_but_never_more_than_twice_when_nothing_is_wrong()
    {
        PlanRecord plan = RepairPlan(machine: null, PlanRecoveryScope.AllowHubRescue);
        int calls = 0;
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, $"try {++calls}");
            return Task.FromResult("Worked on it.");
        });
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        await RepairRunner(agent, _ => "Error: command exceeded the 120s timeout and was killed.\n--- partial stdout ---\nlistening on 3000\n--- partial stderr ---\n",
            options, health, roundsPerRung: 4).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(2, agent.Audits.Count);
        Assert.Equal(2, after.Events!.Count(e => e.Kind == RunEventKind.CheckAudit && e.FailureClass == "NoDefect"));
        Assert.Equal("dotnet build", after.Steps[0].Verify);
        // The step went on for more rounds than that: the signal was simply no longer worth another call to the hub.
        Assert.True(after.Events!.Count(e => e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed) >= 4);
    }

    [Fact]
    public async Task A_check_that_timed_out_twice_is_healed_when_the_audit_finds_a_replacement_that_keeps_the_tests()
    {
        PlanRecord plan = RepairPlan(machine: null, PlanRecoveryScope.AllowHubRescue);
        int calls = 0;
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, $"try {++calls}");
            return Task.FromResult("Worked on it.");
        })
        {
            Auditor = _ => FakeStepAgent.Proposes("dotnet build --no-incremental", "a stuck incremental build")
        };
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        await RepairRunner(agent, command => command == "dotnet build --no-incremental"
            ? Pass(command)
            : "Error: command exceeded the 120s timeout and was killed.\n--- partial stdout ---\nBuilding...\n", options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("dotnet build --no-incremental", after.Steps[0].Verify);
        Assert.Equal("dotnet build", after.Steps[0].OriginalVerify);
    }

    [Fact]
    public async Task The_doctor_installs_what_the_project_declares_once_and_the_check_runs_again_without_costing_a_round()
    {
        WriteProject("""{"scripts":{"test":"jest"},"devDependencies":{"jest":"^29.0.0"}}""", "package-lock.json");
        PlanRecord plan = HealPlan("npm test");
        FakeStepAgent agent = Healer(_ => throw new InvalidOperationException("no audit is needed"));
        bool installed = false;
        List<string> commands = [];

        await RepairRunner(agent, command =>
        {
            commands.Add(command);
            if (command == "npm ci")
            {
                installed = true;
                return Pass(command);
            }

            return installed
                ? Pass(command)
                : "Exit code: 1\n--- stdout ---\n> jest\n'jest' is not recognized as an internal or external command,\noperable program or batch file.";
        }).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["npm test", "npm ci", "npm test"], commands);
        Assert.DoesNotContain(commands, command => command.Contains("-g", StringComparison.Ordinal) || command.Contains("--global", StringComparison.Ordinal) ||
                                                   command.Contains("sudo", StringComparison.Ordinal));
        Assert.Equal(1, after.Steps[0].Attempts);
        PlanRunEvent repaired = Assert.Single(after.Events!, e => e.Kind == RunEventKind.EnvironmentRepaired);
        Assert.Equal("Repaired", repaired.FailureClass);
        Assert.Equal("node-modules", repaired.FailureSignature);
        Assert.Contains("`npm ci`", repaired.Detail);
        Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.CheckFailed);
        Assert.Empty(agent.Audits);
    }

    [Fact]
    public async Task Without_a_lock_file_the_doctor_uses_npm_install_and_a_repair_is_made_only_once_per_cause()
    {
        WriteProject("""{"scripts":{"test":"jest"},"devDependencies":{"jest":"^29.0.0"}}""");
        PlanRecord plan = HealPlan("npm test", scope: PlanRecoveryScope.WorkerOnly, files: ["a.cs"]);
        int calls = 0;
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, $"try {++calls}");
            return Task.FromResult("Worked on it.");
        });
        List<string> commands = [];

        // The install does not help: the failure stays. It is the model's to fix from here on.
        await RepairRunner(agent, command =>
        {
            commands.Add(command);
            return command == "npm install"
                ? Pass(command)
                : "Exit code: 1\n--- stdout ---\n'jest' is not recognized as an internal or external command,\noperable program or batch file.";
        }, roundsPerRung: 2).RunPlanAsync(plan.Id, default);

        Assert.Equal(1, commands.Count(command => command == "npm install"));
        Assert.DoesNotContain("npm ci", commands);
        Assert.Equal(PlanStatus.Blocked, Store.Get(plan.Id)!.Status);
        Assert.Equal(1, Store.Get(plan.Id)!.Events!.Count(e => e.Kind == RunEventKind.EnvironmentRepaired));
    }

    [Fact]
    public async Task A_module_the_project_does_not_declare_is_not_installed_by_the_fleet()
    {
        WriteProject("""{"scripts":{"test":"node --test"},"devDependencies":{"jest":"^29.0.0"}}""", "package-lock.json");
        PlanRecord plan = HealPlan("npm test", files: ["a.cs"]);
        int calls = 0;
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, $"try {++calls}");
            return Task.FromResult("Worked on it.");
        });
        List<string> commands = [];

        await RepairRunner(agent, command =>
        {
            commands.Add(command);
            return "Exit code: 1\n--- stdout ---\nError: Cannot find module 'left-pad'\nRequire stack:\n- src/index.js";
        }, roundsPerRung: 1).RunPlanAsync(plan.Id, default);

        Assert.All(commands, command => Assert.Equal("npm test", command));
        Assert.DoesNotContain(Store.Get(plan.Id)!.Events!, e => e.Kind == RunEventKind.EnvironmentRepaired);
    }

    [Fact]
    public async Task A_project_that_uses_another_package_manager_is_not_repaired_and_the_model_is_told_exactly_what_is_missing()
    {
        WriteProject("""{"scripts":{"test":"jest"},"devDependencies":{"jest":"^29.0.0"}}""", "yarn.lock");
        PlanRecord plan = HealPlan("npm test", files: ["a.cs"]);
        int calls = 0;
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, $"try {++calls}");
            return Task.FromResult("Worked on it.");
        });
        List<string> commands = [];

        await RepairRunner(agent, command =>
        {
            commands.Add(command);
            return "Exit code: 1\n--- stdout ---\n'jest' is not recognized as an internal or external command,\noperable program or batch file.";
        }).RunPlanAsync(plan.Id, default);

        Assert.All(commands, command => Assert.Equal("npm test", command));
        PlanRunEvent note = Assert.Single(Store.Get(plan.Id)!.Events!, e => e.Kind == RunEventKind.EnvironmentRepaired);
        Assert.Equal("NotRepaired", note.FailureClass);
        Assert.Contains("yarn or pnpm", note.Detail);
        Assert.Contains(agent.Sessions[0].Messages.Skip(1), message => message.Text.Contains("Fleet note:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_without_a_check_whose_file_was_never_created_fails_normally_and_is_not_audited()
    {
        PlanRecord plan = Store.Approve(Store.Create("No check", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("Write a.cs", "Write it", ["a.cs"], null, "standard")]).Id)!;
        FakeStepAgent agent = Healer(_ => throw new InvalidOperationException("no audit is needed"));
        agent = new FakeStepAgent((_, _, _) => Task.FromResult("I did nothing.")) { Auditor = agent.Auditor };

        await RepairRunner(agent, Pass, roundsPerRung: 1).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Empty(agent.Audits);
        Assert.Contains(after.Events!, e => e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed);
    }

    [Fact]
    public async Task A_false_alarm_costs_at_most_two_audits_even_for_a_sign_only_a_shell_can_give()
    {
        PlanRecord plan = RepairPlan(machine: null, PlanRecoveryScope.AllowHubRescue);
        int calls = 0;
        FakeStepAgent agent = new((_, _, _) =>
        {
            File.WriteAllText(SourceFile, $"try {++calls}");
            return Task.FromResult("Worked on it.");
        });
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();

        // The words are a shell's, but the check ran: a test of the project printed them. The auditor sees a sound check.
        await RepairRunner(agent, _ => "Exit code: 1\n--- stdout ---\nFAILED widget::a\nThe syntax of the command is incorrect.", options, health, roundsPerRung: 4)
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(2, agent.Audits.Count);
        Assert.Equal(2, after.Events!.Count(e => e.Kind == RunEventKind.CheckAudit && e.FailureClass == "Started"));
        Assert.Equal("dotnet build", after.Steps[0].Verify);
        Assert.True(after.Events!.Count(e => e.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed) >= 4);
    }
}
