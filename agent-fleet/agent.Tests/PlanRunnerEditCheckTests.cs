using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// Runner-level tests of the edit check on a worker: a real worker session over an in-memory file layer, a scripted model that
// edits the file through the file tools, and a check whose answers are scripted in order (the last one repeats). The worker
// records what it would send to the hub at each sync, so a test can see the files the hub would receive.
public sealed partial class PlanRunnerTests
{
    private const string EditCheckCommand = "npm test";
    private const string EditCheckStart = "const hours = 0;\n";
    private const string EditCheckPassing = "Exit code: 0\n--- stdout ---\nok";
    private static readonly TimeSpan EditCheckTimeLimit = TimeSpan.FromSeconds(60);

    private static string EditCheckFailing(int count) =>
        "Exit code: 1\n--- stdout ---\n" + string.Join('\n', Enumerable.Range(1, count).Select(index => $"✖ shop hours case {index} (1ms)"));

    private static string EditCheckNotLoading => PlanCheckOutputFixtures.Round01;

    private sealed class EditCheckLayer : IWorkerWorkspaceFileAccess
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public bool Exists(string path) => Files.ContainsKey(path);

        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Files[path]);

        public void CreateDirectoryTree(string path) { }

        public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            Files[path] = content;
            return Task.CompletedTask;
        }
    }

    /// <summary>The real session, with the sync to the hub recorded: each entry is the worker's files at the moment of a sync.</summary>
    private sealed class EditCheckWorker(WorkerWorkspaceSession session, EditCheckLayer layer) : IWorkerWorkspaceSession, IEditCheckFiles
    {
        public List<Dictionary<string, string>> Synced { get; } = [];

        public string Machine => session.Machine;
        public string Platform => session.Platform;
        public TimeSpan CommandTimeout => session.CommandTimeout;
        public Task<bool> AnswersAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public string SnapshotId() => session.SnapshotId();
        public Dictionary<string, string> BaselineHashes() => session.BaselineHashes();
        public IDisposable Enter() => session.Enter();

        public Task SyncToHubAsync(CancellationToken cancellationToken)
        {
            Synced.Add(new Dictionary<string, string>(layer.Files, StringComparer.Ordinal));
            return Task.CompletedTask;
        }

        public Task RefreshFromHubAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> RestoreFrozenTestsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken) => session.ReadBytesAsync(path, cancellationToken);
        public Task WriteBytesAsync(string path, byte[]? contents, CancellationToken cancellationToken) => session.WriteBytesAsync(path, contents, cancellationToken);
        public string Canonical(string path) => session.Canonical(path);
        public void Dispose() => session.Dispose();
    }

    private sealed class EditCheckWorkers(EditCheckLayer layer, string project, IReadOnlyDictionary<string, string> seed) : IWorkerWorkspaceManager
    {
        public List<EditCheckWorker> Workers { get; } = [];

        public Task<IWorkerWorkspaceSession> StageAsync(PlanRecord plan, PlanStep step, string machine, CancellationToken cancellationToken)
        {
            var session = new WorkerWorkspaceSession(machine,
                new FleetWorkerWorkspaceConfig("worker.example.test", "fleet-test", "C:/fleet-test/key", "SHA256:synthetic-host-key",
                    "/tmp/fleet-test/workspaces", "linux"),
                project, NullLogger.Instance, TimeSpan.FromSeconds(1), fileAccess: layer);
            foreach ((string relative, string text) in seed)
            {
                layer.Files[session.RemoteRoot + "/" + relative] = text;
            }

            var worker = new EditCheckWorker(session, layer);
            Workers.Add(worker);
            return Task.FromResult<IWorkerWorkspaceSession>(worker);
        }

        public Task<WorkerWorkspaceResume> ResumeInterruptedAsync(PlanRecord plan, PlanStep step, WorkerWorkspaceBaseline baseline,
            CancellationToken cancellationToken) => Task.FromException<WorkerWorkspaceResume>(new NotSupportedException("No interrupted worker in these tests."));
    }

    /// <summary>Answers the step's check in order and repeats the last answer. Other commands are passing and are not counted.</summary>
    private sealed class EditCheckScript(params string[] answers)
    {
        private int _next;

        public int Calls { get; private set; }

        public string Answer(string command)
        {
            if (command != EditCheckCommand) return EditCheckPassing;
            Calls++;
            return answers[Math.Min(_next++, answers.Length - 1)];
        }
    }

    private sealed class EditCheckLog : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private (PlanRecord Plan, FleetOptions Options, FleetHealthMonitor Health, string Project) EditCheckPlan(string? kind = null)
    {
        string project = Path.Combine(Path.GetTempPath(), "fleet-edit-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "shop.ts"), EditCheckStart);
        var worker = new FleetNodeConfig("worker-a", "http://worker.example.test/v1", "m", "worker", "standard",
            Workspace: new FleetWorkerWorkspaceConfig("worker.example.test", "worker", "C:/fleet-test/worker-key.pem",
                "SHA256:synthetic-host-key", "/tmp/fleet-test/workspaces", "linux"));
        FleetOptions options = OptionsWithNodes(worker);
        var health = new FleetHealthMonitor(options, new HealthyNodeFactory());
        PlanRecord plan = NewPlan(new PlanStepInput("Keep the shop hours right", "Edit the shop hours", ["src/shop.ts"], EditCheckCommand, "standard",
            Kind: kind));
        plan = Store.Update(plan.Id, current => current with { WorkingDirectory = project })!;
        plan = Store.SelectMachine(plan.Id, 1, "worker-a", out string? selectionError)!;
        Assert.Null(selectionError);
        plan = Store.Approve(plan.Id, autoRetries: 0)!;
        return (plan, options, health, project);
    }

    private PlanRunner EditCheckRunner(IStepAgent agent, Func<string, string> check, IWorkerWorkspaceManager workers, (PlanRecord Plan, FleetOptions Options, FleetHealthMonitor Health, string Project) setup,
        EditCheckSettings? settings = null, TimeSpan? timeout = null, ILogger? logger = null) =>
        new(Store, new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(check(command))), agent, logger ?? NullLogger.Instance, timeout,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, fleetOptions: setup.Options, healthMonitor: setup.Health,
            workerWorkspaces: workers, editCheck: settings);

    /// <summary>A model that makes the given edits to the step's file in its first round, through the file tools, then answers.</summary>
    private static FakeStepAgent EditingAgent(List<string> replies, params (string Old, string New)[] edits)
    {
        int round = 0;
        return new FakeStepAgent(async (_, _, cancellationToken) =>
        {
            if (round++ > 0) return "Done.";
            foreach ((string oldText, string newText) in edits)
            {
                replies.Add(await WorkspaceTools.EditFileAsync("src/shop.ts", oldText, newText, null, cancellationToken));
            }

            return "Done.";
        });
    }

    private static (string Old, string New) HoursEdit(int from) => ($"hours = {from};", $"hours = {from + 1};");

    private static string SyncedShop(Dictionary<string, string> files) =>
        Assert.Single(files.Where(file => file.Key.EndsWith("/src/shop.ts", StringComparison.Ordinal))).Value;

    [Fact]
    public async Task A_run_that_stops_improving_ends_at_its_best_state_in_the_worker_before_the_sync()
    {
        var setup = EditCheckPlan();
        var layer = new EditCheckLayer();
        var workers = new EditCheckWorkers(layer, setup.Project, new Dictionary<string, string> { ["src/shop.ts"] = EditCheckStart });
        var script = new EditCheckScript(EditCheckFailing(3), EditCheckFailing(2), EditCheckFailing(5), EditCheckFailing(5), EditCheckFailing(5),
            EditCheckFailing(5), EditCheckPassing);
        var replies = new List<string>();
        FakeStepAgent agent = EditingAgent(replies, Enumerable.Range(0, 5).Select(index => HoursEdit(index)).ToArray());

        await EditCheckRunner(agent, script.Answer, workers, setup).RunPlanAsync(setup.Plan.Id, default).WaitAsync(EditCheckTimeLimit);

        // The fifth edit is the one that makes the run stop improving for the fourth time in a row: the files go back to the best state.
        Assert.Contains("Restored: the last 4 edits did not help. The files are back at the best state so far (2 failing)", replies[4]);
        Assert.Equal("const hours = 1;\n", SyncedShop(Assert.Single(workers.Workers).Synced[0]));
        PlanRecord after = Store.Get(setup.Plan.Id)!;
        Assert.EndsWith("Edit check: 6 checks, 0 refused, 1 restored, best 2 failing, not switched off.",
            Assert.Single(after.Events!, e => e.Kind == RunEventKind.AttemptEnded && e.Detail.Contains("Edit check:", StringComparison.Ordinal)).Detail);
    }

    [Fact]
    public async Task A_breaking_edit_in_the_worker_is_refused_and_the_file_is_unchanged_before_the_sync()
    {
        var setup = EditCheckPlan();
        var layer = new EditCheckLayer();
        var workers = new EditCheckWorkers(layer, setup.Project, new Dictionary<string, string> { ["src/shop.ts"] = EditCheckStart });
        var script = new EditCheckScript(EditCheckFailing(3), EditCheckNotLoading, EditCheckPassing);
        var replies = new List<string>();
        FakeStepAgent agent = EditingAgent(replies, ("hours = 0;", "hours (= 0;"));

        await EditCheckRunner(agent, script.Answer, workers, setup).RunPlanAsync(setup.Plan.Id, default).WaitAsync(EditCheckTimeLimit);

        Assert.StartsWith("Error: nothing was changed in src/shop.ts. After this edit the project no longer loads:", replies[0]);
        Assert.Equal(EditCheckStart, SyncedShop(Assert.Single(workers.Workers).Synced[0]));
        Assert.Equal(PlanStatus.Done, Store.Get(setup.Plan.Id)!.Status);
    }

    [Fact]
    public async Task With_the_switch_off_no_check_runs_after_an_edit_and_the_edits_stand()
    {
        var setup = EditCheckPlan();
        var layer = new EditCheckLayer();
        var workers = new EditCheckWorkers(layer, setup.Project, new Dictionary<string, string> { ["src/shop.ts"] = EditCheckStart });
        var script = new EditCheckScript(EditCheckPassing);
        var replies = new List<string>();
        FakeStepAgent agent = EditingAgent(replies, HoursEdit(0), HoursEdit(1));

        await EditCheckRunner(agent, script.Answer, workers, setup, settings: new EditCheckSettings { Enabled = false })
            .RunPlanAsync(setup.Plan.Id, default).WaitAsync(EditCheckTimeLimit);

        Assert.All(replies, reply => Assert.DoesNotContain("[Check after this edit", reply));
        Assert.Equal(1, script.Calls);
        Assert.Equal("const hours = 2;\n", SyncedShop(Assert.Single(workers.Workers).Synced[0]));
        Assert.DoesNotContain(Store.Get(setup.Plan.Id)!.Events!, e => e.Detail.Contains("Edit check:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tests_step_is_never_checked_after_its_edits()
    {
        var setup = EditCheckPlan(kind: "tests");
        var layer = new EditCheckLayer();
        var workers = new EditCheckWorkers(layer, setup.Project, new Dictionary<string, string> { ["src/shop.ts"] = EditCheckStart });
        var script = new EditCheckScript(EditCheckPassing);
        var replies = new List<string>();
        FakeStepAgent agent = EditingAgent(replies, HoursEdit(0));

        await EditCheckRunner(agent, script.Answer, workers, setup).RunPlanAsync(setup.Plan.Id, default).WaitAsync(EditCheckTimeLimit);

        Assert.DoesNotContain(replies, reply => reply.Contains("[Check after this edit", StringComparison.Ordinal));
        Assert.Equal(1, script.Calls);
        Assert.DoesNotContain(Store.Get(setup.Plan.Id)!.Events!, e => e.Detail.Contains("Edit check:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_worker_without_byte_access_gets_no_edit_check()
    {
        var setup = EditCheckPlan();
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
        var script = new EditCheckScript(EditCheckPassing);

        await EditCheckRunner(Agent(), script.Answer, workspaces, setup).RunPlanAsync(setup.Plan.Id, default).WaitAsync(EditCheckTimeLimit);

        Assert.Equal(1, script.Calls);
        Assert.DoesNotContain(Store.Get(setup.Plan.Id)!.Events!, e => e.Detail.Contains("Edit check:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timed_out_attempt_puts_the_best_state_back_once_before_the_sync()
    {
        var setup = EditCheckPlan();
        var layer = new EditCheckLayer();
        var workers = new EditCheckWorkers(layer, setup.Project, new Dictionary<string, string> { ["src/shop.ts"] = EditCheckStart });
        var script = new EditCheckScript(EditCheckFailing(3), EditCheckFailing(2), EditCheckFailing(5), EditCheckPassing);
        var log = new EditCheckLog();
        int round = 0;
        var agent = new FakeStepAgent(async (_, _, cancellationToken) =>
        {
            if (round++ > 0) return "Done.";
            await WorkspaceTools.EditFileAsync("src/shop.ts", "hours = 0;", "hours = 1;", null, cancellationToken);
            await WorkspaceTools.EditFileAsync("src/shop.ts", "hours = 1;", "hours = 2;", null, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "Done.";
        });

        await EditCheckRunner(agent, script.Answer, workers, setup, timeout: TimeSpan.FromSeconds(3), logger: log)
            .RunPlanAsync(setup.Plan.Id, default).WaitAsync(EditCheckTimeLimit);

        // The timed-out round is the first attempt; its worker is the first one staged, and it is synced before anything else.
        Assert.Equal("const hours = 1;\n", SyncedShop(workers.Workers[0].Synced[0]));
        Assert.Equal(1, log.Lines.Count(line => line.Contains("attempt 1: edit check finished", StringComparison.Ordinal)));
    }
}
