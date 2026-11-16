namespace AgentFleet.Tests;

public sealed partial class PlanRunnerTests
{
    [Fact]
    public async Task A_worker_check_that_hits_the_time_limit_is_logged_as_a_time_limit_with_how_long_it_ran()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = true };
        var agent = Agent("I will make the check finish.");
        Exception timeout = new WorkerCommandTimeoutException("Operation has timed out.",
            new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out."));
        int checks = 0;

        string Verify(string command)
        {
            if (command == "dotnet build" && checks++ == 0) throw timeout;
            return Pass(command);
        }

        await Runner(agent, Verify, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRunEvent failed = Store.Get(plan.Id)!.Events!.First(runEvent => runEvent.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed);
        Assert.Equal("time-limit", failed.OutcomeClass);
        Assert.NotNull(failed.Seconds);
    }
}
