using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// Runner-level tests of machine choice for a step whose tier has no worker workspace: the step uses the nearest tier that has a
// ready worker, the run log says so, and a step whose own tier has a worker is chosen exactly as before.
public sealed partial class PlanRunnerTests
{
    private const string TierFallbackNoWorkspace = "no worker has a configured workspace for the heavy tier";

    private static FleetNodeConfig TierWorker(string name, string tier) =>
        new(name, $"http://{name}.example.test/v1", "m", "worker", tier,
            Workspace: new FleetWorkerWorkspaceConfig($"{name}.example.test", "worker", "C:/fleet-test/worker-key.pem",
                "SHA256:synthetic-host-key", "/tmp/fleet-test/workspaces", "linux"));

    private static readonly FleetNodeConfig TierHub = new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);

    // Answers every probe as healthy, except for the hosts named as unready, which refuse every request.
    private sealed class TierWorkerNodeHandler(IReadOnlySet<string> unready) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (unready.Contains(request.RequestUri!.Host))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            string body = request.Method == HttpMethod.Post
                ? "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}"
                : "{\"models\":[{\"name\":\"m:latest\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TierWorkerFactory(IReadOnlySet<string> unready) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TierWorkerNodeHandler(unready)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // Runs one unpinned step at the given tier on a fleet with the given nodes, and returns the plan after the run and the
    // workspace manager that was asked to stage it.
    // A step whose only workers are not ready is waited on, not refused, so the run is stopped at its first wait.
    private async Task<(PlanRecord After, FakeWorkerWorkspaceManager Workspaces)> RunUnpinnedTierStepAsync(string tier,
        FleetNodeConfig[] nodes, string[] unready, bool stopAtFirstWait = false)
    {
        string project = Path.Combine(Path.GetTempPath(), "fleet-tier-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            File.WriteAllText(Path.Combine(project, "a.cs"), "class Sample { }\n");
            FleetOptions options = OptionsWithNodes(nodes);
            var health = new FleetHealthMonitor(options, new TierWorkerFactory(new HashSet<string>(unready.Select(name => name + ".example.test"))));
            PlanRecord plan = NewPlan(Step("build the feature", tier: tier));
            plan = Store.Update(plan.Id, current => current with { WorkingDirectory = project })!;
            plan = Store.Approve(plan.Id, autoRetries: 0)!;
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));
            PlanRunner? runner = null;
            Func<TimeSpan, CancellationToken, Task>? stopOnWait = stopAtFirstWait
                ? (_, _) => { runner!.Stop(plan.Id); return Task.CompletedTask; }
                : null;
            runner = Runner(Agent(), Pass, workerWorkspaces: workspaces, fleetOptions: options, healthMonitor: health, delayAsync: stopOnWait);

            try
            {
                await runner.RunPlanAsync(plan.Id, default);
            }
            catch (OperationCanceledException) when (stopAtFirstWait)
            {
            }

            return (Store.Get(plan.Id)!, workspaces);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    private static PlanRunEvent MachineSelectedEvent(PlanRecord plan) =>
        Assert.Single(plan.Events!, runEvent => runEvent.Kind == RunEventKind.MachineSelected);

    [Fact]
    public async Task A_heavy_step_with_no_heavy_workspace_uses_the_standard_worker_and_says_so()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("heavy",
            [TierHub, TierWorker("standard-worker", "standard"), TierWorker("light-worker", "light")], unready: []);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["standard-worker"], workspaces.StagedMachines);
        PlanRunEvent selected = MachineSelectedEvent(after);
        Assert.Equal("Selected standard-worker: no heavy worker has a configured workspace, so the nearest tier (standard) is used; task tier remains heavy.",
            selected.Detail);
        Assert.Equal("standard-worker", selected.WorkspaceNode);
        Assert.Equal("heavy", selected.Tier);
    }

    [Fact]
    public async Task A_heavy_step_on_a_fleet_whose_only_workspace_worker_is_standard_reaches_its_model_call()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("heavy", [TierHub, TierWorker("standard-worker", "standard")], unready: []);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["standard-worker"], workspaces.StagedMachines);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.AttemptStarted && runEvent.WorkspaceNode == "standard-worker");
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.WorkspaceFailed);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepParked);
    }

    [Fact]
    public async Task A_heavy_step_with_only_a_light_workspace_uses_the_light_worker()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("heavy", [TierHub, TierWorker("light-worker", "light")], unready: []);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["light-worker"], workspaces.StagedMachines);
        Assert.Equal("Selected light-worker: no heavy worker has a configured workspace, so the nearest tier (light) is used; task tier remains heavy.",
            MachineSelectedEvent(after).Detail);
    }

    [Fact]
    public async Task A_standard_step_with_no_standard_workspace_prefers_the_heavy_worker_over_the_light_one()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("standard",
            [TierWorker("light-worker", "light"), TierWorker("heavy-worker", "heavy")], unready: []);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["heavy-worker"], workspaces.StagedMachines);
        Assert.Equal("Selected heavy-worker: no standard worker has a configured workspace, so the nearest tier (heavy) is used; task tier remains standard.",
            MachineSelectedEvent(after).Detail);
    }

    [Fact]
    public async Task A_light_step_with_no_light_workspace_prefers_the_standard_worker_over_the_heavy_one()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("light",
            [TierWorker("heavy-worker", "heavy"), TierWorker("standard-worker", "standard")], unready: []);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["standard-worker"], workspaces.StagedMachines);
        Assert.Equal("Selected standard-worker: no light worker has a configured workspace, so the nearest tier (standard) is used; task tier remains light.",
            MachineSelectedEvent(after).Detail);
    }

    [Fact]
    public async Task A_fallback_skips_a_nearer_tier_whose_worker_is_not_ready()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("heavy",
            [TierWorker("standard-worker", "standard"), TierWorker("light-worker", "light")], unready: ["standard-worker"]);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["light-worker"], workspaces.StagedMachines);
        Assert.Equal("light-worker", MachineSelectedEvent(after).WorkspaceNode);
    }

    [Fact]
    public async Task A_step_whose_tier_has_a_worker_that_is_not_ready_is_refused_without_a_fallback()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("heavy",
            [TierWorker("heavy-worker", "heavy"), TierWorker("standard-worker", "standard")], unready: ["heavy-worker"], stopAtFirstWait: true);

        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.Empty(workspaces.StagedMachines);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.MachineSelected);
        Assert.Contains(after.Events!, runEvent => runEvent.Detail.Contains("Worker workspace unavailable: no configured heavy worker is ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_with_no_configured_workspace_anywhere_keeps_the_existing_refusal()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("heavy", [TierHub], unready: []);

        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.Empty(workspaces.StagedMachines);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.MachineSelected);
        Assert.Contains(after.Events!, runEvent => runEvent.Detail.Contains(TierFallbackNoWorkspace, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_with_no_configured_workspace_and_no_ready_worker_keeps_the_existing_refusal()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("heavy",
            [TierHub, TierWorker("light-worker", "light")], unready: ["light-worker"]);

        Assert.NotEqual(PlanStatus.Done, after.Status);
        Assert.Empty(workspaces.StagedMachines);
        Assert.Contains(after.Events!, runEvent => runEvent.Detail.Contains(TierFallbackNoWorkspace, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_pinned_to_a_machine_is_not_moved_by_the_tier_fallback()
    {
        string project = Path.Combine(Path.GetTempPath(), "fleet-tier-pinned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            File.WriteAllText(Path.Combine(project, "a.cs"), "class Sample { }\n");
            FleetOptions options = OptionsWithNodes(TierHub, TierWorker("standard-worker", "standard"), TierWorker("light-worker", "light"));
            var health = new FleetHealthMonitor(options, new TierWorkerFactory(new HashSet<string>()));
            PlanRecord plan = NewPlan(Step("build the feature", tier: "heavy"));
            plan = Store.Update(plan.Id, current => current with { WorkingDirectory = project })!;
            plan = Store.SelectMachine(plan.Id, 1, "light-worker", out string? selectionError)!;
            Assert.Null(selectionError);
            plan = Store.Approve(plan.Id, autoRetries: 0)!;
            var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], []));

            await Runner(Agent(), Pass, workerWorkspaces: workspaces, fleetOptions: options, healthMonitor: health)
                .RunPlanAsync(plan.Id, default);

            PlanRecord after = Store.Get(plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            Assert.Equal(["light-worker"], workspaces.StagedMachines);
            Assert.DoesNotContain(after.Events!, runEvent => runEvent.Detail.Contains("nearest tier", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task A_step_whose_tier_has_a_ready_worker_keeps_the_existing_selection_text()
    {
        var (after, workspaces) = await RunUnpinnedTierStepAsync("heavy",
            [TierHub, TierWorker("heavy-worker", "heavy"), TierWorker("standard-worker", "standard")], unready: []);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["heavy-worker"], workspaces.StagedMachines);
        Assert.Equal("Selected heavy-worker: its worker workspace is configured and ready; task tier remains heavy.",
            MachineSelectedEvent(after).Detail);
    }
}
