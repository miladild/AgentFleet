namespace AgentFleet.Tests;

// The implementation step of a frozen test set: the step whose own check runs the frozen tests, when there is one, so that a
// step unrelated to the tests is not held to them. Without such a step, the first ordinary code step is the implementation step.
public sealed class TestFirstImplementationStepTests
{
    private const string TestCommand = "node --import tsx --test test/session.test.ts";

    private static PlanStep Step(int id, string? kind, string[] files, string? verify) =>
        new(id, $"Step {id}", "Detail", files, verify, "standard", "pending", null, 0, null, null, Kind: kind);

    private static PlanStep TestsStep(int id, string command) => Step(id, "tests", ["test/session.test.ts"], command);

    private static PlanFrozenTestSet SetFor(int stepId, string command) =>
        new(stepId, command, [new PlanFrozenTestFile("test/session.test.ts", "00")]);

    [Fact]
    public void Test_first_Implementation_step_is_the_later_step_whose_check_runs_the_frozen_tests()
    {
        PlanStep[] steps =
        [
            TestsStep(1, TestCommand),
            Step(2, null, ["src/server/marketHours.ts"], "node --import tsx --test test/marketHours.test.ts"),
            Step(3, null, ["src/server/app.ts"], TestCommand),
            Step(4, null, [], "npm test && npm run typecheck"),
        ];

        PlanStep? implementation = PlanRunner.ImplementationStepFor(steps, SetFor(1, TestCommand));

        Assert.Equal(3, implementation?.Id);
    }

    [Fact]
    public void Test_first_Without_a_matching_check_the_first_code_step_is_the_implementation_step()
    {
        PlanStep[] steps =
        [
            TestsStep(1, TestCommand),
            Step(2, null, ["src/a.ts"], "node --test test/a.test.ts"),
            Step(3, null, ["src/b.ts"], "node --test test/b.test.ts"),
        ];

        PlanStep? implementation = PlanRunner.ImplementationStepFor(steps, SetFor(1, TestCommand));

        Assert.Equal(2, implementation?.Id);
    }

    [Fact]
    public void Test_first_Without_a_code_step_there_is_no_implementation_step()
    {
        PlanStep[] steps =
        [
            TestsStep(1, TestCommand),
            Step(2, null, [], TestCommand),
            Step(3, null, ["test/other.test.ts"], "npm test"),
        ];

        Assert.Null(PlanRunner.ImplementationStepFor(steps, SetFor(1, TestCommand)));
    }

    [Fact]
    public void Test_first_Each_tests_step_set_goes_to_the_later_step_with_its_own_command()
    {
        const string secondCommand = "node --import tsx --test test/other.test.ts";
        PlanStep[] steps =
        [
            TestsStep(1, TestCommand),
            TestsStep(2, secondCommand),
            Step(3, null, ["src/a.ts"], TestCommand),
            Step(4, null, ["src/b.ts"], secondCommand),
        ];

        Assert.Equal(3, PlanRunner.ImplementationStepFor(steps, SetFor(1, TestCommand))?.Id);
        Assert.Equal(4, PlanRunner.ImplementationStepFor(steps, SetFor(2, secondCommand))?.Id);
    }

    [Fact]
    public void Test_first_When_the_first_code_step_runs_the_frozen_tests_it_stays_the_implementation_step()
    {
        PlanStep[] steps =
        [
            TestsStep(1, TestCommand),
            Step(2, null, ["src/a.ts"], TestCommand),
            Step(3, null, ["src/b.ts"], "npm test"),
        ];

        Assert.Equal(2, PlanRunner.ImplementationStepFor(steps, SetFor(1, TestCommand))?.Id);
    }

    [Fact]
    public void Test_first_Of_several_steps_with_the_same_check_the_first_one_is_the_implementation_step()
    {
        PlanStep[] steps =
        [
            TestsStep(1, TestCommand),
            Step(2, null, ["src/a.ts"], "npm run lint"),
            Step(3, null, ["src/b.ts"], TestCommand),
            Step(4, null, ["src/c.ts"], TestCommand),
        ];

        Assert.Equal(3, PlanRunner.ImplementationStepFor(steps, SetFor(1, TestCommand))?.Id);
    }

    [Fact]
    public void Test_first_Surrounding_whitespace_does_not_stop_a_check_from_matching_the_frozen_command()
    {
        PlanStep[] steps =
        [
            TestsStep(1, $"  {TestCommand}\n"),
            Step(2, null, ["src/a.ts"], "npm run lint"),
            Step(3, null, ["src/b.ts"], $"\t{TestCommand} "),
        ];

        Assert.Equal(3, PlanRunner.ImplementationStepFor(steps, SetFor(1, TestCommand))?.Id);
    }

    [Fact]
    public void Test_first_A_check_that_only_contains_the_frozen_command_does_not_match_it()
    {
        PlanStep[] steps =
        [
            TestsStep(1, TestCommand),
            Step(2, null, ["src/a.ts"], $"{TestCommand} --reporter spec"),
            Step(3, null, ["src/b.ts"], TestCommand),
        ];

        Assert.Equal(3, PlanRunner.ImplementationStepFor(steps, SetFor(1, TestCommand))?.Id);
    }

    [Fact]
    public void Test_first_A_matching_check_before_the_tests_step_is_not_the_implementation_step()
    {
        PlanStep[] steps =
        [
            Step(1, null, ["src/a.ts"], TestCommand),
            TestsStep(2, TestCommand),
            Step(3, null, ["src/b.ts"], "npm run lint"),
        ];

        Assert.Equal(3, PlanRunner.ImplementationStepFor(steps, SetFor(2, TestCommand))?.Id);
    }
}
