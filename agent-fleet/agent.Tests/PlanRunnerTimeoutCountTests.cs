namespace AgentFleet.Tests;

public sealed partial class PlanRunnerTests
{
    // An attempt that times out has no reply, but the tool calls its model made before the timeout still count.
    private sealed class TimingOutAgent : IStepAgent
    {
        public StepToolActivity? Activity { get; set; }

        public int Sends { get; private set; }

        public Task<StepAgentReply> RunStepAsync(string prompt, string tier, CancellationToken cancellationToken) =>
            OpenSession().SendAsync(prompt, tier, null, cancellationToken);

        public IStepSession OpenSession() => new TimingOutSession(this);

        internal int NextSend() => ++Sends;
    }

    private sealed class TimingOutSession(TimingOutAgent owner) : IStepSession
    {
        public bool HasHistory { get; private set; }

        public void TrackActivity(StepToolActivity activity) => owner.Activity = activity;

        public async Task<StepAgentReply> SendAsync(string message, string tier, string? machine, CancellationToken cancellationToken)
        {
            if (owner.NextSend() > 1)
            {
                HasHistory = true;
                return new StepAgentReply("Done.", 1, true);
            }

            owner.Activity!.Record("write_file");
            owner.Activity.Record("read_file");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new StepAgentReply(string.Empty, 0, false);
        }
    }

    [Fact]
    public async Task A_timed_out_attempt_keeps_the_tool_calls_it_made_and_the_edit()
    {
        PlanRecord plan = ApprovedPlan(Step("build it"));

        await Runner(new TimingOutAgent(), Pass, timeout: TimeSpan.FromMilliseconds(300)).RunPlanAsync(plan.Id, default);

        PlanRunEvent round = Store.Get(plan.Id)!.Events!
            .First(runEvent => runEvent.Kind == RunEventKind.RoundClassified);
        Assert.Equal(2, round.ToolCalls);
        Assert.True(round.EditToolCalled);
    }
}
