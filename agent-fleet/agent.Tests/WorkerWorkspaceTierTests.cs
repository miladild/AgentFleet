using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// Which machines may hold a step's worker workspace, by tier. A machine must match the step's tier, unless the step's tier has
// no worker with a workspace of its own: then the nearest tier's worker holds the step's files and checks. These tests stop
// before any connection: a machine that passes the rules reaches the SSH key check, and the key file does not exist.
public sealed class WorkerWorkspaceTierTests : PlanTestBase
{
    private const string PassedMachineRules = "SSH key configured for worker";

    private string MissingKeyPath => Path.Combine(PlansDirectory, "missing-worker-key.pem");

    private FleetNodeConfig Worker(string name, string tier, bool vision = false) =>
        new(name, $"http://{name}.example.test/v1", "m", "worker", tier, Vision: vision,
            Workspace: new FleetWorkerWorkspaceConfig($"{name}.example.test", "worker", MissingKeyPath,
                "SHA256:synthetic-host-key", "/srv/fleet-test", "linux"));

    private static FleetNodeConfig Hub => new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);

    // The config store refuses a hub or vision machine that has a workspace, so the list is replaced without that check:
    // the staging rule must refuse those machines on its own, whatever the saved config allowed.
    private FleetOptions Options(params FleetNodeConfig[] nodes)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONFIG_PATH"] = Path.Combine(PlansDirectory, "fleet.config.json")
            })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        FleetOptions options = FleetOptions.Load(configuration, configStore);
        options.ReplaceNodes(configStore.Current with { Nodes = nodes });
        return options;
    }

    private (FleetOptions Options, PlanRecord Plan) Setup(string tier, params FleetNodeConfig[] nodes)
    {
        string project = Path.Combine(PlansDirectory, "project");
        Directory.CreateDirectory(project);
        PlanRecord plan = Store.Create("Tier", "goal", project, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("edit a", "detail", ["a.txt"], "check", tier)]);
        return (Options(nodes), plan);
    }

    private async Task<Exception> StageRefusedOrFailedAsync(string tier, string machine, params FleetNodeConfig[] nodes)
    {
        (FleetOptions options, PlanRecord plan) = Setup(tier, nodes);
        var manager = new WorkerWorkspaceManager(options, NullLogger.Instance, TimeSpan.FromSeconds(60));
        return await Assert.ThrowsAnyAsync<Exception>(async () =>
            (await manager.StageAsync(plan, plan.Steps[0], machine, CancellationToken.None)).Dispose());
    }

    private static void AssertPassedMachineRules(Exception exception)
    {
        Assert.IsType<FileNotFoundException>(exception);
        Assert.Contains(PassedMachineRules, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_heavy_step_may_stage_on_a_standard_worker_when_no_heavy_worker_has_a_workspace()
    {
        Exception exception = await StageRefusedOrFailedAsync("heavy", "standard-worker", Hub, Worker("standard-worker", "standard"));

        AssertPassedMachineRules(exception);
    }

    [Fact]
    public async Task A_heavy_step_is_still_refused_on_a_standard_worker_when_a_heavy_worker_has_a_workspace()
    {
        Exception exception = await StageRefusedOrFailedAsync("heavy", "standard-worker",
            Hub, Worker("heavy-worker", "heavy"), Worker("standard-worker", "standard"));

        InvalidOperationException refused = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("Worker workspace unavailable: 'standard-worker' is configured for the standard tier, not this step's approved heavy tier.",
            refused.Message);
    }

    [Fact]
    public async Task A_standard_step_may_stage_on_a_standard_worker()
    {
        Exception exception = await StageRefusedOrFailedAsync("standard", "standard-worker",
            Hub, Worker("standard-worker", "standard"), Worker("heavy-worker", "heavy"));

        AssertPassedMachineRules(exception);
    }

    [Fact]
    public async Task A_standard_step_is_refused_on_a_heavy_worker_because_its_own_tier_has_a_worker()
    {
        Exception exception = await StageRefusedOrFailedAsync("standard", "heavy-worker",
            Hub, Worker("heavy-worker", "heavy"), Worker("standard-worker", "standard"));

        InvalidOperationException refused = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("Worker workspace unavailable: 'heavy-worker' is configured for the heavy tier, not this step's approved standard tier.",
            refused.Message);
    }

    [Fact]
    public async Task The_hub_is_refused_even_when_a_step_could_borrow_another_tier()
    {
        // The hub is given a workspace and a tier other than the step's, so only the hub rule can refuse it.
        Exception exception = await StageRefusedOrFailedAsync("heavy", "hub", Worker("hub", "standard"));

        InvalidOperationException refused = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("is not a coding worker", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vision_machine_is_refused_even_when_a_step_could_borrow_another_tier()
    {
        Exception exception = await StageRefusedOrFailedAsync("heavy", "vision-worker", Hub, Worker("vision-worker", "standard", vision: true));

        InvalidOperationException refused = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("is not a coding worker", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_without_a_workspace_is_refused_even_when_a_step_could_borrow_another_tier()
    {
        var plain = new FleetNodeConfig("plain-worker", "http://plain-worker.example.test/v1", "m", "worker", "standard");

        Exception exception = await StageRefusedOrFailedAsync("heavy", "plain-worker", Hub, plain);

        InvalidOperationException refused = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("has no worker workspace configured", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_interrupted_step_is_recovered_on_the_borrowed_worker_it_was_staged_on()
    {
        (FleetOptions options, PlanRecord plan) = Setup("heavy", Hub, Worker("standard-worker", "standard"));
        var manager = new WorkerWorkspaceManager(options, NullLogger.Instance, TimeSpan.FromSeconds(60));
        var baseline = new WorkerWorkspaceBaseline("standard-worker", new Dictionary<string, string>());

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ResumeInterruptedAsync(plan, plan.Steps[0], baseline, CancellationToken.None));

        AssertPassedMachineRules(exception);
    }
}
