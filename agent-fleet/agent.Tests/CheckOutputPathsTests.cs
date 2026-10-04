namespace AgentFleet.Tests;

public sealed class CheckOutputPathsTests : PlanTestBase
{
    private const char Backslash = (char)92;

    [Fact]
    public async Task A_failing_checks_output_reaches_the_model_with_the_projects_folder_taken_off_its_paths()
    {
        string project = Path.Combine(Path.GetTempPath(), "fleet-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            PlanRecord plan = Store.Create("Fix", "Fix the parser", project, [], [], [], null, null,
                [new PlanStepInput("Fix the parser", "Make parse work", ["src/a.js"], "node --test", "light")]);
            Store.Approve(plan.Id, exportToProject: false);
            string worker = $"C:{Backslash}Work{Backslash}plans{Backslash}{plan.Id}{Backslash}worker-a{Backslash}project{Backslash}src{Backslash}a.js:7:3";
            var tools = new PlanTools(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)),
                (command, _, _) => Task.FromResult($"Exit code: 1\n--- stdout ---\nTypeError: boom\n    at parse ({worker})\n    at run ({project}{Backslash}src{Backslash}b.js:9:1)"));

            StepCompletion result = await tools.TryCompleteStepAsync(plan.Id, 1, null, default);

            Assert.False(result.Done);
            Assert.Contains($"at parse (src{Backslash}a.js:7:3)", result.Output);
            Assert.Contains($"at run (src{Backslash}b.js:9:1)", result.Output);
            Assert.DoesNotContain("plans", result.Output);
            Assert.DoesNotContain(project, result.Output);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }
}
