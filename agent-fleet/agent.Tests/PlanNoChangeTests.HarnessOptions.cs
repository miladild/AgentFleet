using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>A run's log names the hardening options it used, so two runs can be compared knowing which parts were on.</summary>
public sealed partial class PlanNoChangeTests
{
    [Fact]
    public async Task The_run_log_names_the_hardening_options_at_the_start_of_the_run()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "old a");
        PlanRecord plan = OneStepPlan(StepListing("edit a", "a.txt"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(ProjectDirectory, "a.txt"), "new a");
            return Task.FromResult("Changed a.");
        });
        var runner = new PlanRunner(Store, RepairTools(Pass), agent, NullLogger.Instance, transientDelay: _ => TimeSpan.Zero,
            delayAsync: (_, _) => Task.CompletedTask, harness: new HarnessOptions(EnableFileGuard: false, MaxCallsRepair: 12));

        await runner.RunPlanAsync(plan.Id, default);

        PlanRunEvent logged = Assert.Single(Store.Get(plan.Id)!.Events!, runEvent => runEvent.Kind == "harness-options");
        Assert.Contains("EnableFileGuard=off", logged.Detail, StringComparison.Ordinal);
        Assert.Contains("MaxCallsRepair=12", logged.Detail, StringComparison.Ordinal);
        Assert.Contains("EnableArbiter=on", logged.Detail, StringComparison.Ordinal);
    }
}
