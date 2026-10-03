using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// Runs only when FLEET_LIVE_WORKER names a real worker ("name|host|user|keyPath|hostKey|root|platform"); otherwise skipped,
/// so CI and a plain `dotnet test` never touch a machine. It uses a plan folder of its own on the worker and removes it.
/// </summary>
internal sealed class LiveWorkerFactAttribute : FactAttribute
{
    public const string Variable = "FLEET_LIVE_WORKER";

    public LiveWorkerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} (name|host|user|keyPath|hostKey|root|platform) to run this against a real worker.";
        }
    }
}

public sealed class WorkerWorkspaceLiveTests : PlanTestBase
{
    [LiveWorkerFact]
    public async Task A_hub_side_change_after_a_sync_is_not_taken_for_unsynced_worker_work_but_real_unsynced_work_still_is()
    {
        string[] parts = Environment.GetEnvironmentVariable(LiveWorkerFactAttribute.Variable)!.Split('|');
        string machine = parts[0];
        string platform = parts[6];
        var node = new FleetNodeConfig(machine, "http://worker.example.test/v1", "m", "worker", "standard",
            Workspace: new FleetWorkerWorkspaceConfig(parts[1], parts[2], parts[3], parts[4], parts[5], platform));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(PlansDirectory, "fleet.config.json") })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        configStore.Save(configStore.Current with { Nodes = [node] });
        FleetOptions options = FleetOptions.Load(configuration, configStore);

        string project = Path.Combine(PlansDirectory, "project");
        Directory.CreateDirectory(project);
        string hubFile = Path.Combine(project, "a.txt");
        File.WriteAllText(hubFile, "one");
        File.WriteAllText(Path.Combine(project, "b.txt"), "keep");
        PlanRecord plan = Store.Create("Live", "goal", project, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("edit a", "detail", ["a.txt"], "check", "standard")]);
        PlanStep step = plan.Steps[0];
        var manager = new WorkerWorkspaceManager(options, NullLogger.Instance, TimeSpan.FromSeconds(60));
        CancellationToken ct = CancellationToken.None;
        WorkerWorkspaceSession? last = null;
        try
        {
            // Round 1: the model changes a.txt on the worker; the runner syncs it to the hub.
            var first = (WorkerWorkspaceSession)await manager.StageAsync(plan, step, machine, ct);
            await first.WriteFileAsync("a.txt", "two", ct);
            await first.SyncToHubAsync(ct);
            Assert.Equal("two", File.ReadAllText(hubFile));
            first.Dispose();

            // The runner rolls the round back: the hub is as it was before, the worker still holds the later content.
            File.WriteAllText(hubFile, "one");

            // Round 2 stages again. Before the fix this threw "differs from the hub checkout" and parked the step.
            var second = (WorkerWorkspaceSession)await manager.StageAsync(plan, step, machine, ct);
            Assert.Equal("one", (await second.ReadFileAsync("a.txt", null, null, ct)).Trim());

            // Real unsynced work: the worker changes the file after the last sync and the hub changes it too.
            await second.WriteFileAsync("a.txt", "three", ct);
            second.Dispose();
            File.WriteAllText(hubFile, "four");
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => (await manager.StageAsync(plan, step, machine, ct)).Dispose());
            Assert.Contains("differs from the hub checkout", refused.Message);

            // The user takes the worker's version: staging goes through again (and gives the cleanup a session).
            File.WriteAllText(hubFile, "three");
            last = (WorkerWorkspaceSession)await manager.StageAsync(plan, step, machine, ct);
        }
        finally
        {
            if (last is not null)
            {
                // The working folder of a session is .../plans/<planId>/<machine>/project: three levels up is the plans folder.
                string remove = platform == "linux"
                    ? $"cd ../../.. && rm -rf {plan.Id}"
                    : $"Set-Location ..\\..\\..; Remove-Item -Recurse -Force {plan.Id}";
                await last.RunCommandAsync(remove, null, ct);
                last.Dispose();
            }
        }
    }
}
