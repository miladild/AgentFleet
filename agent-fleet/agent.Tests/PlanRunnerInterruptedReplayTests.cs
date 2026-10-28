namespace AgentFleet.Tests;

// A step that is not marked safe to replay, interrupted on a worker, is replayed after a restart only when the worker files are
// unchanged and the run log shows that the interrupted round made no changing tool call. A round the log does not cover is blocked.
public sealed partial class PlanRunnerTests
{
    private PlanRecord UnsafeInterruptedWorker(bool logged, string? changingTool = null)
    {
        PlanRecord plan = NewPlan(new PlanStepInput("recover worker work", "detail", ["a.cs"], "dotnet build", "standard", RetrySafe: false));
        plan = Store.SelectMachine(plan.Id, 1, "worker-a", out string? selectionError)!;
        Assert.Null(selectionError);
        plan = Store.Approve(plan.Id, autoRetries: 0)!;
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select(step => step with { Status = StepStatus.Running, Attempts = 1 }).ToList()
        });
        Store.SaveWorkerBaseline(plan.Id, 1, 1, "worker-a", new Dictionary<string, string> { ["a.cs"] = "baseline" });
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.AttemptStarted, "standard", "worker-a", "Interrupted attempt.",
            modelNode: "worker-a", workspaceNode: "worker-a");
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.WorkspaceStaged, "standard", "worker-a", "Staged the project.",
            modelNode: "worker-a", workspaceNode: "worker-a");
        if (logged)
        {
            Store.AddEvent(plan.Id, 1, 1, PlanRunner.ToolLoggedEvent, "standard", "worker-a", "Tool calls of this round are logged.",
                modelNode: "worker-a", workspaceNode: "worker-a");
        }

        if (changingTool is not null)
        {
            Store.AddEvent(plan.Id, 1, 1, PlanRunner.ChangingToolEvent, "standard", "worker-a", changingTool,
                modelNode: "worker-a", workspaceNode: "worker-a");
        }

        return Store.Get(plan.Id)!;
    }

    [Fact]
    public async Task An_unsafe_step_whose_logged_round_made_no_changing_call_is_replayed_when_no_file_changed()
    {
        PlanRecord plan = UnsafeInterruptedWorker(logged: true);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(1, after.Steps[0].Attempts);
        Assert.Single(agent.Calls);
    }

    [Fact]
    public async Task An_unsafe_step_whose_worker_files_changed_is_not_replayed()
    {
        PlanRecord plan = UnsafeInterruptedWorker(logged: true);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis(["a.cs"], ["a.cs"], []));
        FakeStepAgent agent = Agent();

        await Runner(agent, Fail, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Empty(agent.Calls);
        Assert.Contains("not marked safe to replay", after.Steps[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unsafe_step_whose_round_is_not_in_the_run_log_stays_blocked()
    {
        PlanRecord plan = UnsafeInterruptedWorker(logged: false);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Empty(agent.Calls);
        Assert.Contains("has no worker edits to recover, but it is not marked safe", after.Steps[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unsafe_step_whose_logged_round_made_a_changing_call_stays_blocked()
    {
        PlanRecord plan = UnsafeInterruptedWorker(logged: true, changingTool: "run_command");
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Empty(agent.Calls);
        Assert.Contains("has no worker edits to recover, but it is not marked safe", after.Steps[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_round_logs_its_changing_tool_calls_and_not_its_reads()
    {
        PlanRecord plan = NewPlan(new PlanStepInput("recover worker work", "detail", null, "dotnet build", "standard"));
        plan = Store.SelectMachine(plan.Id, 1, "worker-a", out string? selectionError)!;
        Assert.Null(selectionError);
        plan = Store.Approve(plan.Id, autoRetries: 0)!;
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
        var agent = new ScriptedAgent((activity, _) =>
        {
            activity.Record("read_file");
            activity.Record("run_command");
            return Task.FromResult(new StepAgentReply("Done.", 2, false));
        });

        await Runner(agent, Pass, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Single(after.Events!, runEvent => runEvent.Kind == PlanRunner.ToolLoggedEvent && runEvent.Attempt == 1);
        PlanRunEvent changing = Assert.Single(after.Events!, runEvent => runEvent.Kind == PlanRunner.ChangingToolEvent);
        Assert.Equal("run_command", changing.Detail);
        Assert.Equal(1, changing.Attempt);
    }
}
