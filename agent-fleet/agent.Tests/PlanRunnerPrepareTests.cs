namespace AgentFleet.Tests;

// A fresh worker copy has no installed packages. Measured: the model's first test run failed with "Cannot find package", the
// edit check had no baseline, and the model installed packages itself and added one the project never asked for.
public sealed partial class PlanRunnerTests
{
    private async Task<(List<string> Log, PlanRecord Plan)> RunOnFreshWorker(string? lockFile)
    {
        string project = NewFolderProject();
        try
        {
            File.WriteAllText(Path.Combine(project, "package.json"), """{"devDependencies":{"tsx":"^4.0.0"}}""");
            if (lockFile is not null)
            {
                File.WriteAllText(Path.Combine(project, lockFile), "{}");
            }

            var setup = WorkerPlan(project, CodeOnlyStep(), CodeOnlyStep());
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            List<string> log = [];
            int writes = 0;
            var agent = new FakeStepAgent((_, _, _) =>
            {
                log.Add("model");
                File.WriteAllText(Path.Combine(project, "src", "limit.cs"), $"new code {++writes}\n");
                return Task.FromResult("Done.");
            });

            await WorkerRunner(agent, workspaces, setup.Options, setup.Health, FailFirstMode.Off, command =>
            {
                log.Add(command);
                return Pass(command);
            }).RunPlanAsync(setup.Plan.Id, default);

            return (log, Store.Get(setup.Plan.Id)!);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("npm-shrinkwrap.json")]
    public async Task A_fresh_worker_copy_gets_the_projects_packages_once_before_the_model_starts(string lockFile)
    {
        (List<string> log, PlanRecord plan) = await RunOnFreshWorker(lockFile);

        Assert.Equal(PlanStatus.Done, plan.Status);
        Assert.Equal("npm ci", log[0]);
        Assert.Equal(1, log.Count(entry => entry == "npm ci"));
        Assert.Equal(2, log.Count(entry => entry == "model"));
        PlanRunEvent prepared = Assert.Single(plan.Events!, runEvent => runEvent.Kind == RunEventKind.EnvironmentRepaired);
        Assert.Equal("Repaired", prepared.FailureClass);
        Assert.Equal("worker-a", prepared.WorkspaceNode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("yarn.lock")]
    public async Task A_project_the_fleet_cannot_install_exactly_is_not_prepared(string? lockFile)
    {
        (List<string> log, PlanRecord plan) = await RunOnFreshWorker(lockFile);

        Assert.Equal(PlanStatus.Done, plan.Status);
        Assert.DoesNotContain(log, entry => entry.StartsWith("npm", StringComparison.Ordinal));
    }
}
