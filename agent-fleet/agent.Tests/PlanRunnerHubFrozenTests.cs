using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// Runner-level tests of frozen test files on the hub: a step that runs without a worker workspace gets the same refusal and the same
// restore as a worker step, the hub copy is healed before a worker is staged, and the accepted bytes are kept in the plan's storage.
// Every runner test here runs under a time limit, and each fake model stops answering after a fixed number of calls.
public sealed partial class PlanRunnerTests
{
    private static readonly TimeSpan HubFrozenTimeLimit = TimeSpan.FromSeconds(60);
    private const int HubFrozenCallLimit = 30;
    private const string AcceptedTestText = "public class LimitTests { }\n";
    private const string ChangedTestText = "public class LimitTests { /* changed by a command */ }\n";

    private static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // A plan that runs in the hub's project folder, with no worker workspace.
    private PlanRecord HubPlan(string project, params PlanStepInput[] steps)
    {
        PlanRecord plan = NewPlan(steps);
        plan = Store.Update(plan.Id, current => current with { WorkingDirectory = project })!;
        return Store.Approve(plan.Id, autoRetries: 0)!;
    }

    private PlanRunner HubFrozenRunner(IStepAgent agent, Func<string, string> run, IWorkerWorkspaceManager? workspaces = null,
        bool enabled = true, FleetOptions? options = null, FleetHealthMonitor? health = null) =>
        new(Store, new PlanTools(Store, Valid, (command, directory, token) => command.StartsWith("git ", StringComparison.Ordinal)
                ? FailFirstTests.RunShell(command, directory, token) : Task.FromResult(run(command))),
            agent, NullLogger.Instance, fleetOptions: options, healthMonitor: health, workerWorkspaces: workspaces,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, roundsPerRung: 2,
            failFirst: FailFirstMode.Off, testFirst: enabled ? TestFirstMode.On : TestFirstMode.Off);

    // A stand-in for the model that stops after a fixed number of calls, so a loop cannot run for ever.
    private static FakeStepAgent CappedAgent(Func<int, Task<string>> reply)
    {
        int calls = 0;
        return new FakeStepAgent((_, _, _) => calls++ < HubFrozenCallLimit ? reply(calls - 1) : Task.FromResult("Done."));
    }

    // Passes the wrapped manager's calls on, and reports the hub copy of the given file as it is when the worker is staged.
    private sealed class StagingProbe(IWorkerWorkspaceManager inner, Action onStage) : IWorkerWorkspaceManager
    {
        public async Task<IWorkerWorkspaceSession> StageAsync(PlanRecord plan, PlanStep step, string machine, CancellationToken cancellationToken)
        {
            onStage();
            return await inner.StageAsync(plan, step, machine, cancellationToken);
        }

        public Task<WorkerWorkspaceResume> ResumeInterruptedAsync(PlanRecord plan, PlanStep step, WorkerWorkspaceBaseline baseline,
            CancellationToken cancellationToken) => inner.ResumeInterruptedAsync(plan, step, baseline, cancellationToken);

        public Task<FailFirstCheckResult> RunScratchCheckAsync(PlanRecord plan, PlanStep step, string machine, IWorkerWorkspaceSession workspace,
            IReadOnlyDictionary<string, byte[]?> overlay, string command, CancellationToken cancellationToken) =>
            inner.RunScratchCheckAsync(plan, step, machine, workspace, overlay, command, cancellationToken);
    }

    [Fact]
    public async Task On_the_hub_a_frozen_test_that_a_command_changed_is_put_back_before_the_check_and_the_next_round_names_it()
    {
        string project = NewGitProject();
        try
        {
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            var setup = HubPlan(project, TestsStep(), ImplementationStep());
            var seenAtCheck = new List<string>();
            var scopeAtCheck = new List<bool>();
            int testRuns = 0, buildRuns = 0;
            var fake = CappedAgent(call => Task.FromResult(call switch
            {
                0 => WriteAndReturn(project, "tests/LimitTests.cs", AcceptedTestText),
                1 => WriteAndReturn(test, ChangedTestText),
                _ => "Done.",
            }));
            var runner = HubFrozenRunner(new WriterAgent(fake), command =>
            {
                if (command == "dotnet test") return ++testRuns == 1 ? TestsFailOutput : TestsPassOutput;
                seenAtCheck.Add(File.ReadAllText(test));
                scopeAtCheck.Add(HubFrozenTestScope.Current is not null);
                return ++buildRuns == 1 ? BuildFailOutput : BuildPassOutput;
            });

            await runner.RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            PlanRecord after = Store.Get(setup.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(AcceptedTestText, seenAtCheck[0]);
            Assert.DoesNotContain(true, scopeAtCheck);
            Assert.Null(HubFrozenTestScope.Current);
            Assert.Contains("These fixed test files were changed and have been put back; change the code, not the tests: tests/LimitTests.cs",
                fake.Calls[2].Prompt);
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.FrozenTests && e.Detail.Contains("tests/LimitTests.cs"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    private static string WriteAndReturn(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return "Done.";
    }

    private static string WriteAndReturn(string project, string relative, string content) => WriteAndReturn(Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    [Fact]
    public async Task On_the_hub_the_implementation_proof_runs_against_the_restored_frozen_test()
    {
        string project = NewGitProject();
        try
        {
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            var setup = HubPlan(project, TestsStep(), ImplementationStep());
            var testsSeenByProof = new List<string>();
            int testRuns = 0;
            var fake = CappedAgent(call => Task.FromResult(call == 0 ? WriteAndReturn(project, "tests/LimitTests.cs", AcceptedTestText) : "Done."));
            var runner = HubFrozenRunner(new WriterAgent(fake), command =>
            {
                if (command == "dotnet test")
                {
                    if (++testRuns == 1) return TestsFailOutput;
                    testsSeenByProof.Add(File.ReadAllText(test));
                    return TestsPassOutput;
                }

                // A command that the check runs changes the frozen test after the check has looked at it.
                File.WriteAllText(test, ChangedTestText);
                return BuildPassOutput;
            });

            await runner.RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            PlanRecord after = Store.Get(setup.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(AcceptedTestText, Assert.Single(testsSeenByProof));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task On_the_hub_a_write_to_a_frozen_test_is_refused_inside_the_model_attempt_and_the_file_stays_accepted()
    {
        string project = NewGitProject();
        try
        {
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            var setup = HubPlan(project, TestsStep(), ImplementationStep());
            var refusals = new List<string>();
            var seenAtCheck = new List<string>();
            int testRuns = 0;
            var fake = CappedAgent(call =>
            {
                if (call == 0) return Task.FromResult(WriteAndReturn(project, "tests/LimitTests.cs", AcceptedTestText));
                refusals.Add(WorkspaceTools.WriteFileAsync(test, ChangedTestText, default).GetAwaiter().GetResult());
                refusals.Add(WorkspaceTools.EditFileAsync(test, "public", "private", null, default).GetAwaiter().GetResult());
                refusals.Add(WorkspaceTools.DeleteFile(test));
                return Task.FromResult("Done.");
            });
            var runner = HubFrozenRunner(new WriterAgent(fake), command =>
            {
                if (command == "dotnet test") return ++testRuns == 1 ? TestsFailOutput : TestsPassOutput;
                seenAtCheck.Add(File.ReadAllText(test));
                return BuildPassOutput;
            });

            await runner.RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            PlanRecord after = Store.Get(setup.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal([FrozenTestFiles.RefusalMessage, FrozenTestFiles.RefusalMessage, FrozenTestFiles.RefusalMessage], refusals);
            Assert.Equal(AcceptedTestText, Assert.Single(seenAtCheck));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task On_the_hub_a_frozen_test_deleted_by_a_command_comes_back_before_the_check()
    {
        // The accepted test is committed and named by the step, so neither the git restore of deleted files nor the snapshot
        // of the attempt's own work brings it back: only the frozen restore can.
        string project = NewGitProject();
        try
        {
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            WriteProjectFile(project, "tests/LimitTests.cs", AcceptedTestText);
            FailFirstTests.Git(project, "add", "-A");
            FailFirstTests.Git(project, "commit", "-qm", "accepted test");
            var setup = HubPlan(project, new PlanStepInput("Implement limits", "Reject zero", ["src/limit.cs", "tests/LimitTests.cs"], "dotnet build", "standard"));
            Store.AddFrozenTests(setup.Id, new PlanFrozenTestSet(0, "dotnet test", [new PlanFrozenTestFile("tests/LimitTests.cs", Sha256Hex(AcceptedTestText))]));
            Store.SaveFrozenContent(setup.Id, Encoding.UTF8.GetBytes(AcceptedTestText));
            var seenAtCheck = new List<string>();
            var fake = CappedAgent(call =>
            {
                if (call != 0) return Task.FromResult("Done.");
                WriteProjectFile(project, "src/limit.cs", "code\n");
                return Task.FromResult(DeleteAndReturn(test));
            });
            await HubFrozenRunner(new WriterAgent(fake), command =>
            {
                if (command == "dotnet test") return TestsPassOutput;
                seenAtCheck.Add(File.Exists(test) ? File.ReadAllText(test) : "missing");
                return BuildPassOutput;
            }).RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            // The step names the test but the restored test is unchanged, so the step is not accepted; what this test shows is what
            // the check saw, and that the restore was recorded.
            PlanRecord after = Store.Get(setup.Id)!;
            Assert.NotEmpty(seenAtCheck);
            Assert.All(seenAtCheck, seen => Assert.Equal(AcceptedTestText, seen));
            Assert.Contains(after.Events!, e => e.Kind == RunEventKind.FrozenTests && e.Detail.Contains("\"restored\":1"));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    private static string DeleteAndReturn(string path)
    {
        File.Delete(path);
        return "Done.";
    }

    [Fact]
    public async Task On_the_hub_a_test_file_the_tests_step_writes_itself_is_not_refused_before_it_is_frozen()
    {
        string project = NewGitProject();
        try
        {
            string other = Path.Combine(project, "tests", "Other.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllText(other, "other\n");
            PlanRecord setup = HubPlan(project, TestsStep(), ImplementationStep());
            Store.AddFrozenTests(setup.Id, new PlanFrozenTestSet(0, "dotnet test --other",
                [new PlanFrozenTestFile("tests/Other.cs", Sha256Hex("other\n"))]));
            string written = "";
            var fake = CappedAgent(call =>
            {
                if (call != 0) return Task.FromResult("Done.");
                written = WorkspaceTools.WriteFileAsync(Path.Combine(project, "tests", "LimitTests.cs"), AcceptedTestText, default).GetAwaiter().GetResult();
                return Task.FromResult("Done.");
            });
            int testRuns = 0;
            await HubFrozenRunner(new WriterAgent(fake), command => command switch
            {
                "dotnet test" => ++testRuns == 1 ? TestsFailOutput : TestsPassOutput,
                "dotnet test --other" => TestsPassOutput,
                _ => BuildPassOutput,
            }).RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            PlanRecord after = Store.Get(setup.Id)!;
            Assert.StartsWith("Wrote", written, StringComparison.Ordinal);
            Assert.Contains(after.FrozenTests!, set => set.StepId == 1);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task On_the_hub_the_accepted_test_bytes_are_kept_in_the_plan_storage_under_their_hash()
    {
        string project = NewGitProject();
        try
        {
            var setup = HubPlan(project, TestsStep(), ImplementationStep());
            var fake = CappedAgent(call => Task.FromResult(call == 0 ? WriteAndReturn(project, "tests/LimitTests.cs", AcceptedTestText) : "Done."));
            int testRuns = 0;
            await HubFrozenRunner(new WriterAgent(fake), command => command == "dotnet test" ? (++testRuns == 1 ? TestsFailOutput : TestsPassOutput) : BuildPassOutput)
                .RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            PlanRecord after = Store.Get(setup.Id)!;
            PlanFrozenTestFile file = Assert.Single(Assert.Single(after.FrozenTests!).Files);
            Assert.Equal(AcceptedTestText, Encoding.UTF8.GetString(Store.ReadFrozenContent(setup.Id, file.Sha256)!));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task On_the_hub_a_changed_frozen_test_with_no_verified_accepted_copy_fails_the_round_plainly(bool storedButCorrupt)
    {
        string project = NewGitProject();
        try
        {
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(test)!);
            File.WriteAllText(test, ChangedTestText);
            var setup = HubPlan(project, ImplementationStep());
            string hash = Sha256Hex(AcceptedTestText);
            if (storedButCorrupt)
            {
                string stored = Store.SaveFrozenContent(setup.Id, Encoding.UTF8.GetBytes(AcceptedTestText));
                File.WriteAllText(Path.Combine(PlansDirectory, setup.Id + ".frozen", stored), "corrupted");
            }
            Store.AddFrozenTests(setup.Id, new PlanFrozenTestSet(0, "dotnet test", [new PlanFrozenTestFile("tests/LimitTests.cs", hash)]));
            int buildRuns = 0;
            var fake = CappedAgent(_ => Task.FromResult("Done."));
            await HubFrozenRunner(new WriterAgent(fake), command => { buildRuns++; return BuildPassOutput; })
                .RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            PlanRecord after = Store.Get(setup.Id)!;
            Assert.NotEqual(PlanStatus.Done, after.Status);
            Assert.Equal(0, buildRuns);
            Assert.Contains(fake.Calls, call => call.Prompt.Contains("tests/LimitTests.cs was changed and could not be put back", StringComparison.Ordinal));
            Assert.Equal(ChangedTestText, File.ReadAllText(test));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task On_the_hub_a_folder_where_a_frozen_test_belongs_fails_the_round_plainly()
    {
        string project = NewGitProject();
        try
        {
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            Directory.CreateDirectory(test);
            var setup = HubPlan(project, ImplementationStep());
            Store.SaveFrozenContent(setup.Id, Encoding.UTF8.GetBytes(AcceptedTestText));
            Store.AddFrozenTests(setup.Id, new PlanFrozenTestSet(0, "dotnet test", [new PlanFrozenTestFile("tests/LimitTests.cs", Sha256Hex(AcceptedTestText))]));
            int buildRuns = 0;
            var fake = CappedAgent(_ => Task.FromResult("Done."));
            await HubFrozenRunner(new WriterAgent(fake), command => { buildRuns++; return BuildPassOutput; })
                .RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            PlanRecord after = Store.Get(setup.Id)!;
            Assert.NotEqual(PlanStatus.Done, after.Status);
            Assert.Equal(0, buildRuns);
            Assert.Contains(fake.Calls, call => call.Prompt.Contains("tests/LimitTests.cs was changed and could not be put back", StringComparison.Ordinal));
            Assert.True(Directory.Exists(test));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task On_the_hub_a_frozen_path_that_leaves_the_project_is_never_written()
    {
        string project = NewGitProject();
        string folder = "fleet-outside-" + Guid.NewGuid().ToString("N");
        string outside = Path.Combine(Path.GetDirectoryName(project)!, folder, "LimitTests.cs");
        try
        {
            var setup = HubPlan(project, ImplementationStep());
            Store.SaveFrozenContent(setup.Id, Encoding.UTF8.GetBytes(AcceptedTestText));
            Store.AddFrozenTests(setup.Id, new PlanFrozenTestSet(0, "dotnet test", [new PlanFrozenTestFile($"../{folder}/LimitTests.cs", Sha256Hex(AcceptedTestText))]));
            var fake = CappedAgent(_ => Task.FromResult("Done."));
            await HubFrozenRunner(new WriterAgent(fake), command => BuildPassOutput)
                .RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            Assert.False(File.Exists(outside));
        }
        finally
        {
            FailFirstTests.DeleteTree(project);
            if (Directory.Exists(Path.GetDirectoryName(outside))) Directory.Delete(Path.GetDirectoryName(outside)!, recursive: true);
        }
    }

    [Fact]
    public async Task On_the_hub_a_frozen_test_behind_a_link_is_not_written_through_it()
    {
        string project = NewGitProject();
        string target = Path.Combine(Path.GetDirectoryName(project)!, "fleet-link-target-" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            File.WriteAllText(target, ChangedTestText);
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(test)!);
            try
            {
                File.CreateSymbolicLink(test, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Creating a link needs a permission this machine does not give; there is no link to test then.
                return;
            }

            var setup = HubPlan(project, ImplementationStep());
            Store.SaveFrozenContent(setup.Id, Encoding.UTF8.GetBytes(AcceptedTestText));
            Store.AddFrozenTests(setup.Id, new PlanFrozenTestSet(0, "dotnet test", [new PlanFrozenTestFile("tests/LimitTests.cs", Sha256Hex(AcceptedTestText))]));
            var fake = CappedAgent(_ => Task.FromResult("Done."));
            await HubFrozenRunner(new WriterAgent(fake), command => BuildPassOutput)
                .RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            Assert.Equal(ChangedTestText, File.ReadAllText(target));
            Assert.NotEqual(PlanStatus.Done, Store.Get(setup.Id)!.Status);
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
            FailFirstTests.DeleteTree(project);
        }
    }

    [Theory]
    [InlineData(true, AcceptedTestText)]
    [InlineData(false, ChangedTestText)]
    public async Task Before_a_worker_is_staged_the_hub_copy_of_a_frozen_test_is_healed_from_the_accepted_bytes(bool stored, string expectedAtStage)
    {
        string project = NewGitProject();
        try
        {
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(test)!);
            File.WriteAllText(test, ChangedTestText);
            var setup = WorkerPlan(project, ImplementationStep());
            string hash = Sha256Hex(AcceptedTestText);
            if (stored) Store.SaveFrozenContent(setup.Plan.Id, Encoding.UTF8.GetBytes(AcceptedTestText));
            Store.AddFrozenTests(setup.Plan.Id, new PlanFrozenTestSet(0, "dotnet test", [new PlanFrozenTestFile("tests/LimitTests.cs", hash)]));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            string? seenAtStage = null;
            var probe = new StagingProbe(workspaces, () => seenAtStage ??= File.ReadAllText(test));
            var fake = CappedAgent(_ => Task.FromResult("Done."));
            await HubFrozenRunner(new WriterAgent(fake), command => command == "dotnet test" ? TestsPassOutput : BuildPassOutput,
                probe, options: setup.Options, health: setup.Health).RunPlanAsync(setup.Plan.Id, default).WaitAsync(HubFrozenTimeLimit);

            Assert.Equal(expectedAtStage, seenAtStage);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task With_the_setting_off_a_hub_write_to_a_frozen_test_is_allowed_and_nothing_is_stored_or_restored()
    {
        string project = NewGitProject();
        try
        {
            string test = Path.Combine(project, "tests", "LimitTests.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(test)!);
            File.WriteAllText(test, AcceptedTestText);
            var setup = HubPlan(project, ImplementationStep());
            Store.AddFrozenTests(setup.Id, new PlanFrozenTestSet(0, "dotnet test", [new PlanFrozenTestFile("tests/LimitTests.cs", Sha256Hex(AcceptedTestText))]));
            string written = "";
            var fake = CappedAgent(_ =>
            {
                written = WorkspaceTools.WriteFileAsync(test, ChangedTestText, default).GetAwaiter().GetResult();
                return Task.FromResult("Done.");
            });
            string? seenAtCheck = null;
            await HubFrozenRunner(new WriterAgent(fake), command =>
            {
                if (command == "dotnet test") return TestsPassOutput;
                seenAtCheck ??= File.ReadAllText(test);
                return BuildPassOutput;
            }, enabled: false).RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            PlanRecord after = Store.Get(setup.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.StartsWith("Wrote", written, StringComparison.Ordinal);
            Assert.Equal(ChangedTestText, seenAtCheck);
            Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.FrozenTests);
            Assert.False(Directory.Exists(Path.Combine(PlansDirectory, setup.Id + ".frozen")));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task With_the_setting_off_an_accepted_tests_step_stores_no_frozen_bytes()
    {
        string project = NewGitProject();
        try
        {
            var setup = HubPlan(project, TestsStep());
            var fake = CappedAgent(_ => Task.FromResult(WriteAndReturn(project, "tests/LimitTests.cs", AcceptedTestText)));
            await HubFrozenRunner(new WriterAgent(fake), command => command == "dotnet test" ? TestsFailOutput : BuildPassOutput, enabled: false)
                .RunPlanAsync(setup.Id, default).WaitAsync(HubFrozenTimeLimit);

            Assert.False(Directory.Exists(Path.Combine(PlansDirectory, setup.Id + ".frozen")));
            Assert.Null(Store.Get(setup.Id)!.FrozenTests);
        }
        finally { FailFirstTests.DeleteTree(project); }
    }

    [Fact]
    public async Task A_correction_by_a_ruling_keeps_the_corrected_bytes_in_the_plan_storage()
    {
        string project = NewGitProject();
        try
        {
            var setup = DisputePlan(project, TestsStep(), ImplementationStep("dotnet test"));
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            var budget = new RulingBudget(1, _ => new StepAgentReply(TestWrongRuling, 0, false));
            var fake = DisputingImplementer(project, message => message == 0, budget);
            await FrozenRunner(new RulingAgent(fake), workspaces, setup.Options, setup.Health, CheckReadsTheTestFile(project, "CORRECTED"))
                .RunPlanAsync(setup.Plan.Id, default).WaitAsync(DisputeTimeLimit);

            PlanRecord after = Store.Get(setup.Plan.Id)!;
            PlanFrozenTestFile file = Assert.Single(Assert.Single(after.FrozenTests!).Files);
            Assert.Equal(Sha256Upper(CorrectedTest), file.Sha256);
            Assert.Equal(CorrectedTest, Encoding.UTF8.GetString(Store.ReadFrozenContent(setup.Plan.Id, file.Sha256)!));
        }
        finally { FailFirstTests.DeleteTree(project); }
    }
}
