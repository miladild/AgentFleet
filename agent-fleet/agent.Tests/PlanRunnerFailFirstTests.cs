using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// Runner-level tests of the fail-first check. They sit in the runner's test class so they can use its fake worker manager,
// session and helpers. The mode is passed to the runner through its constructor, the way the host passes it, so these tests
// never read or set the process environment, so they need no collection that disables parallelization.
public sealed partial class PlanRunnerTests
{
    private const string PassedBeforeFeedback = "Your tests also pass on the code as it was before this step, so they do not test your change. " +
        "Add tests for what this step must make true; they must fail without your change.";

    private static PlanStepInput CodeAndTestsStep(string tier = "standard") =>
        new("Add the rate limit", "Limit requests per client", ["src/limit.cs", "tests/LimitTests.cs"], "dotnet test", tier);

    private static PlanStepInput CodeOnlyStep() => new("Change the limit", "Change the limit", ["src/limit.cs"], "dotnet test", "standard");

    // A git project committed in its state before the step: src/limit.cs holds the old code and there are no tests yet.
    private static string NewGitProject()
    {
        string project = NewFolderProject();
        FailFirstTests.Git(project, "init", "-q");
        FailFirstTests.Git(project, "add", "-A");
        FailFirstTests.Git(project, "commit", "-qm", "start");
        return project;
    }

    private static string NewFolderProject()
    {
        string project = Path.Combine(Path.GetTempPath(), "fleet-failfirst-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "limit.cs"), "old code\n");
        return project;
    }

    // A worker plan on the same fake worker as the sync tests, with each step given the worker.
    private (PlanRecord Plan, FleetOptions Options, FleetHealthMonitor Health) WorkerPlan(string project, params PlanStepInput[] steps)
    {
        var worker = new FleetNodeConfig("worker-a", "http://worker.example.test/v1", "m", "worker", "standard",
            Workspace: new FleetWorkerWorkspaceConfig("worker.example.test", "worker", "C:/fleet-test/worker-key.pem",
                "SHA256:synthetic-host-key", "/tmp/fleet-test/workspaces", "linux"));
        FleetOptions options = OptionsWithNodes(worker);
        var health = new FleetHealthMonitor(options, new HealthyNodeFactory());
        PlanRecord plan = NewPlan(steps);
        plan = Store.Update(plan.Id, current => current with { WorkingDirectory = project })!;
        for (int id = 1; id <= steps.Length; id++)
        {
            plan = Store.SelectMachine(plan.Id, id, "worker-a", out string? selectionError)!;
            Assert.Null(selectionError);
        }

        plan = Store.Approve(plan.Id, autoRetries: 0)!;
        return (plan, options, health);
    }

    // The runner reads git's state through the same command runner as the check, so git commands really run in the project
    // folder and the check itself is answered by the fake.
    private PlanRunner WorkerRunner(FakeStepAgent agent, FakeWorkerWorkspaceManager workspaces, FleetOptions options, FleetHealthMonitor health,
        FailFirstMode mode, Func<string, string>? verify = null) =>
        new(Store, new PlanTools(Store, Valid, (command, directory, cancellationToken) => command.StartsWith("git ", StringComparison.Ordinal)
                ? FailFirstTests.RunShell(command, directory, cancellationToken)
                : Task.FromResult((verify ?? Pass)(command))),
            agent, NullLogger.Instance, transientDelay: _ => TimeSpan.Zero, fleetOptions: options, healthMonitor: health,
            workerWorkspaces: workspaces, delayAsync: (_, _) => Task.CompletedTask, failFirst: mode);

    // The model's round: new code in src/limit.cs and tests in tests/LimitTests.cs, written into the hub project.
    private static FakeStepAgent WritesCodeAndTests(string project) => new((_, _, _) =>
    {
        File.WriteAllText(Path.Combine(project, "src", "limit.cs"), "new code\n");
        Directory.CreateDirectory(Path.Combine(project, "tests"));
        File.WriteAllText(Path.Combine(project, "tests", "LimitTests.cs"), "tests\n");
        return Task.FromResult("Done.");
    });

    private static FakeStepAgent WritesCodeOnly(string project) => new((_, _, _) =>
    {
        File.WriteAllText(Path.Combine(project, "src", "limit.cs"), "new code\n");
        return Task.FromResult("Done.");
    });

    private static FakeStepAgent WritesTestsOnly(string project) => new((_, _, _) =>
    {
        Directory.CreateDirectory(Path.Combine(project, "tests"));
        File.WriteAllText(Path.Combine(project, "tests", "LimitTests.cs"), "tests\n");
        return Task.FromResult("Done.");
    });

    private static IReadOnlyList<PlanRunEvent> FailFirstEvents(PlanRecord plan) =>
        (plan.Events ?? []).Where(runEvent => runEvent.Kind == RunEventKind.FailFirst).ToArray();

    private static string? OutcomeOf(PlanRunEvent runEvent) =>
        JsonDocument.Parse(runEvent.Detail).RootElement.GetProperty("outcome").GetString();

    private static string[] FailFirstKeys(PlanRunner runner, string planId)
    {
        FieldInfo field = typeof(PlanRunner).GetField("_failFirstSnapshots", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshots = (System.Collections.IDictionary)field.GetValue(runner)!;
        return snapshots.Keys.Cast<string>().Where(key => key.StartsWith(planId + ":", StringComparison.Ordinal)).ToArray();
    }

    [Fact]
    public async Task Tests_that_fail_on_the_old_code_are_accepted_and_the_evidence_says_so()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            FakeStepAgent agent = WritesCodeAndTests(project);

            await WorkerRunner(agent, workspaces, setup.Options, setup.Health, FailFirstMode.Warn).RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            PlanRunEvent evidence = Assert.Single(FailFirstEvents(after));
            Assert.Equal("failed-before", OutcomeOf(evidence));
            Assert.Equal(["StageAsync", "RunScratchCheckAsync"], workspaces.Methods);
            Assert.Same(workspaces.Sessions[0], workspaces.ScratchCalls[0].Workspace);
            Assert.Equal("new code\n", File.ReadAllText(Path.Combine(project, "src", "limit.cs")));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task A_scratch_check_whose_tests_also_pass_on_the_old_code_is_recorded_and_accepted_in_warn()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []))
            {
                ScratchBehaviour = _ => Task.FromResult(new FailFirstCheckResult(0, "Exit code: 0", 0.2))
            };
            FakeStepAgent agent = WritesCodeAndTests(project);

            await WorkerRunner(agent, workspaces, setup.Options, setup.Health, FailFirstMode.Warn).RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Single(agent.Calls);
            Assert.Equal("passed-before", OutcomeOf(Assert.Single(FailFirstEvents(after))));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task In_enforce_tests_that_also_pass_on_the_old_code_are_sent_back_with_the_feedback_and_the_round_counts()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            int scratchChecks = 0;
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []))
            {
                // The first check passes on the old code, the repaired one fails on it.
                ScratchBehaviour = _ => Task.FromResult(++scratchChecks == 1
                    ? new FailFirstCheckResult(0, "Exit code: 0", 0.2)
                    : new FailFirstCheckResult(1, "Exit code: 1", 0.2))
            };
            FakeStepAgent agent = WritesCodeAndTests(project);

            await WorkerRunner(agent, workspaces, setup.Options, setup.Health, FailFirstMode.Enforce).RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(2, agent.Calls.Count);
            Assert.Contains(PassedBeforeFeedback, agent.Calls[1].Prompt, StringComparison.Ordinal);
            Assert.All(agent.Calls, call => Assert.Equal("standard", call.Tier));
            Assert.Equal(["passed-before", "failed-before"], FailFirstEvents(after).Select(OutcomeOf));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task A_step_that_changed_no_test_file_runs_no_scratch_check_and_is_accepted()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeOnlyStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));

            await WorkerRunner(WritesCodeOnly(project), workspaces, setup.Options, setup.Health, FailFirstMode.Enforce)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Empty(workspaces.ScratchCalls);
            Assert.Equal("no-test-changes", OutcomeOf(Assert.Single(FailFirstEvents(after))));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task A_step_that_only_changed_test_files_runs_no_scratch_check_and_is_accepted()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));

            await WorkerRunner(WritesTestsOnly(project), workspaces, setup.Options, setup.Health, FailFirstMode.Enforce)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Empty(workspaces.ScratchCalls);
            Assert.Equal("tests-only", OutcomeOf(Assert.Single(FailFirstEvents(after))));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task Without_a_step_start_snapshot_the_outcome_is_unavailable_and_the_step_is_accepted()
    {
        // A folder that is not a git project has no snapshot, so there is nothing to restore the old code from.
        string project = NewFolderProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));

            await WorkerRunner(WritesCodeAndTests(project), workspaces, setup.Options, setup.Health, FailFirstMode.Enforce)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Empty(workspaces.ScratchCalls);
            Assert.Equal("unavailable", OutcomeOf(Assert.Single(FailFirstEvents(after))));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task A_scratch_check_that_throws_is_unavailable_and_the_step_is_accepted()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []))
            {
                ScratchBehaviour = _ => throw new InvalidOperationException("Synthetic scratch failure.")
            };

            await WorkerRunner(WritesCodeAndTests(project), workspaces, setup.Options, setup.Health, FailFirstMode.Enforce)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Single(workspaces.ScratchCalls);
            Assert.Equal("unavailable", OutcomeOf(Assert.Single(FailFirstEvents(after))));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task A_scratch_check_that_reports_a_timeout_is_timed_out_and_the_step_is_accepted()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []))
            {
                ScratchBehaviour = _ => Task.FromResult(new FailFirstCheckResult(-1, "Timed out", 120, TimedOut: true))
            };

            await WorkerRunner(WritesCodeAndTests(project), workspaces, setup.Options, setup.Health, FailFirstMode.Enforce)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal("timed-out", OutcomeOf(Assert.Single(FailFirstEvents(after))));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task A_scratch_check_that_throws_a_worker_timeout_is_timed_out_and_the_step_is_accepted()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []))
            {
                ScratchBehaviour = _ => throw new WorkerCommandTimeoutException("Synthetic timeout.", new TimeoutException())
            };

            await WorkerRunner(WritesCodeAndTests(project), workspaces, setup.Options, setup.Health, FailFirstMode.Enforce)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal("timed-out", OutcomeOf(Assert.Single(FailFirstEvents(after))));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task With_the_mode_off_no_scratch_check_runs_and_no_evidence_is_written()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));

            await WorkerRunner(WritesCodeAndTests(project), workspaces, setup.Options, setup.Health, FailFirstMode.Off)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Empty(workspaces.ScratchCalls);
            Assert.Equal(["StageAsync"], workspaces.Methods);
            Assert.Empty(FailFirstEvents(after));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task The_whole_project_final_step_of_a_multi_step_plan_is_not_checked_on_the_old_code()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep(), new PlanStepInput("Whole project check", "Check the whole project",
                ["src/limit.cs"], "dotnet test", "standard"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));

            await WorkerRunner(WritesCodeAndTests(project), workspaces, setup.Options, setup.Health, FailFirstMode.Enforce)
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            // Only the first step's check is compared with the old code; the final step never is.
            Assert.Equal([1], workspaces.ScratchCalls.Select(call => call.StepId).Distinct());
            Assert.DoesNotContain(FailFirstEvents(after), runEvent => runEvent.StepId == 2);
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task The_overlay_comes_from_the_steps_first_round_not_from_the_work_a_later_round_starts_from()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            FakeStepAgent agent = WritesCodeAndTests(project);
            int checks = 0;

            // Round 1 writes the code and the tests and its check fails; round 2 starts from that work and its check passes.
            await WorkerRunner(agent, workspaces, setup.Options, setup.Health, FailFirstMode.Warn,
                    verify: command => ++checks == 1 ? Fail(command) : Pass(command))
                .RunPlanAsync(setup.Plan.Id, default);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(2, agent.Calls.Count);
            FakeScratchCall scratch = Assert.Single(workspaces.ScratchCalls);
            Assert.Equal("old code\n", System.Text.Encoding.UTF8.GetString(scratch.Overlay["src/limit.cs"]!));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task An_enforce_runner_refuses_passed_before_without_any_environment_variable()
    {
        string project = NewGitProject();
        try
        {
            var setup = WorkerPlan(project, CodeAndTestsStep());
            int scratchChecks = 0;
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []))
            {
                ScratchBehaviour = _ => Task.FromResult(++scratchChecks == 1
                    ? new FailFirstCheckResult(0, "Exit code: 0", 0.2)
                    : new FailFirstCheckResult(1, "Exit code: 1", 0.2))
            };
            FakeStepAgent agent = WritesCodeAndTests(project);

            await WorkerRunner(agent, workspaces, setup.Options, setup.Health, FailFirstMode.Enforce).RunPlanAsync(setup.Plan.Id, default);

            Assert.Equal(2, agent.Calls.Count);
            Assert.Contains(PassedBeforeFeedback, agent.Calls[1].Prompt, StringComparison.Ordinal);
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }

    [Fact]
    public async Task A_finished_plans_fail_first_snapshots_are_removed_from_the_runner()
    {
        string project = NewGitProject();
        try
        {
            PlanRecord plan = Store.Update(NewPlan(CodeOnlyStep()).Id, current => current with { WorkingDirectory = project })!;
            plan = Store.Approve(plan.Id, autoRetries: 0)!;
            PlanRunner? runner = null;
            var seenDuringRun = new List<string>();
            // No worker here: the snapshot is kept for any git project, and the check runs after the model's round.
            PlanRunner created = new(Store, new PlanTools(Store, Valid, (command, directory, cancellationToken) =>
                {
                    if (command.StartsWith("git ", StringComparison.Ordinal)) return FailFirstTests.RunShell(command, directory, cancellationToken);
                    seenDuringRun.AddRange(FailFirstKeys(runner!, plan.Id));
                    return Task.FromResult(Pass(command));
                }),
                WritesCodeOnly(project), NullLogger.Instance, transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask);
            runner = created;

            await created.RunPlanAsync(plan.Id, default);

            Assert.Contains($"{plan.Id}:1", seenDuringRun);
            Assert.Empty(FailFirstKeys(created, plan.Id));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
        }
    }
}
