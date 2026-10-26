using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// The caller of the implementation-step choice: which frozen set each step is held to, through the runner's own mode and plan.
public sealed partial class PlanRunnerTests
{
    private const string FrozenCommand = "node --import tsx --test test/x.test.ts";

    private static PlanStepInput FrozenTestsStep() =>
        new("Write x tests", "Cover x", ["test/x.test.ts"], FrozenCommand, "standard", Kind: "tests");

    private static PlanStepInput CodeStep(string file, string verify) =>
        new($"Change {file}", "Detail", [file], verify, "standard");

    private static PlanStepInput FinalStep() => new("Run the project checks", "Detail", [], "npm test", "standard");

    private static PlanFrozenTestSet XFrozenSet(int stepId) =>
        new(stepId, FrozenCommand, [new PlanFrozenTestFile("test/x.test.ts", "00")]);

    private PlanRunner ImplementationRunner(bool enabled)
    {
        var worker = new FleetNodeConfig("worker-a", "http://worker.example.test/v1", "m", "worker", "standard",
            Workspace: new FleetWorkerWorkspaceConfig("worker.example.test", "worker", "C:/fleet-test/worker-key.pem",
                "SHA256:synthetic-host-key", "/tmp/fleet-test/workspaces", "linux"));
        FleetOptions options = OptionsWithNodes(worker);
        var health = new FleetHealthMonitor(options, new HealthyNodeFactory());
        return TestFirstRunner(new FakeStepAgent((_, _, _) => Task.FromResult("")), null, options, health, () => TestsPassOutput, enabled);
    }

    [Fact]
    public void Test_first_Only_the_step_whose_check_runs_the_frozen_tests_is_held_to_them()
    {
        PlanRecord plan = NewPlan(FrozenTestsStep(), CodeStep("src/unrelated.ts", "npm run lint"), CodeStep("src/app.ts", FrozenCommand),
            FinalStep());
        PlanFrozenTestSet set = XFrozenSet(plan.Steps[0].Id);
        plan = plan with { FrozenTests = [set] };
        PlanRunner runner = ImplementationRunner(enabled: true);

        Assert.Same(set, Assert.Single(runner.ImplementationSetsFor(plan, plan.Steps[2])));
        Assert.Empty(runner.ImplementationSetsFor(plan, plan.Steps[1]));
        Assert.Empty(runner.ImplementationSetsFor(plan, plan.Steps[0]));
        Assert.Empty(runner.ImplementationSetsFor(plan, plan.Steps[3]));
    }

    [Fact]
    public void Test_first_With_test_first_off_no_step_is_held_to_the_frozen_tests()
    {
        PlanRecord plan = NewPlan(FrozenTestsStep(), CodeStep("src/app.ts", FrozenCommand), FinalStep());
        PlanFrozenTestSet set = XFrozenSet(plan.Steps[0].Id);
        plan = plan with { FrozenTests = [set] };
        PlanRunner runner = ImplementationRunner(enabled: false);

        foreach (PlanStep step in plan.Steps)
        {
            Assert.Empty(runner.ImplementationSetsFor(plan, step));
        }
    }

    [Fact]
    public void Test_first_A_plan_without_frozen_tests_holds_no_step_to_them()
    {
        PlanRecord plan = NewPlan(FrozenTestsStep(), CodeStep("src/app.ts", FrozenCommand), FinalStep());
        PlanRunner runner = ImplementationRunner(enabled: true);

        Assert.Null(plan.FrozenTests);
        foreach (PlanStep step in plan.Steps)
        {
            Assert.Empty(runner.ImplementationSetsFor(plan, step));
        }
    }

    [Fact]
    public void Test_first_Without_a_matching_check_the_first_code_step_is_held_to_the_set_and_later_ones_are_not()
    {
        PlanRecord plan = NewPlan(FrozenTestsStep(), CodeStep("src/a.ts", "npm run lint"), CodeStep("src/b.ts", "npm run build"),
            FinalStep());
        PlanFrozenTestSet set = XFrozenSet(plan.Steps[0].Id);
        plan = plan with { FrozenTests = [set] };
        PlanRunner runner = ImplementationRunner(enabled: true);

        Assert.Same(set, Assert.Single(runner.ImplementationSetsFor(plan, plan.Steps[1])));
        Assert.Empty(runner.ImplementationSetsFor(plan, plan.Steps[2]));
        Assert.Empty(runner.ImplementationSetsFor(plan, plan.Steps[3]));
    }
}
