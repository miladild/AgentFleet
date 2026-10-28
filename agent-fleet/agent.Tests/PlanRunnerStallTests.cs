namespace AgentFleet.Tests;

// A model call that stops answering is cut off by the runner, not waited for: the round is a failed round with a plain reason,
// and the repair ladder runs the next round. A call that keeps calling tools is left alone however long it takes the stall limit.
public sealed partial class PlanRunnerTests
{
    // Answers each send with what the test scripts; the activity is the one the runner watches for tool calls.
    private sealed class ScriptedAgent(Func<StepToolActivity, CancellationToken, Task<StepAgentReply>> script) : IStepAgent
    {
        public int Sends;

        public Task<StepAgentReply> RunStepAsync(string prompt, string tier, CancellationToken cancellationToken) =>
            OpenSession().SendAsync(prompt, tier, null, cancellationToken);

        public IStepSession OpenSession() => new ScriptedSession(this);

        internal Task<StepAgentReply> Send(StepToolActivity activity, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Sends);
            return script(activity, cancellationToken);
        }
    }

    private sealed class ScriptedSession(ScriptedAgent owner) : IStepSession
    {
        private StepToolActivity? _activity;

        public bool HasHistory { get; private set; }

        public void TrackActivity(StepToolActivity activity) => _activity = activity;

        public async Task<StepAgentReply> SendAsync(string message, string tier, string? machine, CancellationToken cancellationToken)
        {
            StepAgentReply reply = await owner.Send(_activity!, cancellationToken);
            HasHistory = true;
            return reply;
        }
    }

    [Fact]
    public async Task A_model_call_that_stops_answering_is_a_failed_round_and_the_next_round_runs()
    {
        // The first call never returns, and it ignores its cancellation: the runner must stop waiting for it anyway.
        var silent = new TaskCompletionSource<StepAgentReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int checks = 0;
        var agent = new ScriptedAgent((_, _) => Interlocked.Increment(ref calls) == 1
            ? silent.Task
            : Task.FromResult(new StepAgentReply("Done.", 1, true)));
        PlanRecord plan = ApprovedPlan(Step("build it"));
        PlanRunner runner = Runner(agent, command => ++checks == 1 ? Fail(command) : Pass(command));
        runner.StallLimit = TimeSpan.FromMilliseconds(300);
        runner.StallGrace = TimeSpan.FromMilliseconds(100);

        await runner.RunPlanAsync(plan.Id, default).WaitAsync(ReplayTimeLimit);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, agent.Sends);
        PlanRunEvent stalled = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.TimedOut);
        Assert.Contains("stopped answering", stalled.Detail, StringComparison.Ordinal);
        Assert.Contains("this round is counted as failed", stalled.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.PlanBlocked);
    }

    [Fact]
    public async Task A_model_call_that_keeps_calling_tools_is_not_cut_short_by_the_stall_limit()
    {
        // Each tool call comes well inside the limit, but the whole round takes several times it.
        var agent = new ScriptedAgent(async (activity, cancellationToken) =>
        {
            for (int call = 0; call < 14; call++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                activity.Record("read_file");
            }

            return new StepAgentReply("Done.", 14, false);
        });
        PlanRecord plan = ApprovedPlan(Step("build it"));
        PlanRunner runner = Runner(agent, Pass);
        runner.StallLimit = TimeSpan.FromMilliseconds(500);

        await runner.RunPlanAsync(plan.Id, default).WaitAsync(ReplayTimeLimit);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(1, agent.Sends);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.TimedOut);
    }
}
