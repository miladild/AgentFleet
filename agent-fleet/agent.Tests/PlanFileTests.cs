using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class PlanFileTests : PlanTestBase
{
    private readonly string _project;

    public PlanFileTests()
    {
        // Inside the test base's folder, which it deletes afterwards.
        _project = Path.Combine(PlansDirectory, "project");
        Directory.CreateDirectory(Path.Combine(_project, "src"));
        File.WriteAllText(Path.Combine(_project, "package.json"), "{}");
    }

    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private string Sample(string workingDirectory) => $$"""
        # Plan: a reliable, tested board

        Working directory: `{{workingDirectory}}`

        Goal: close the gaps the architecture lists.
        The board learns holidays.

        Decisions:
        - Tests use node:test through tsx: no new
          dependencies.
        - Imports stay without extensions.

        ## Step 1: Add a test runner

        - Tier: light
        - Files: `package.json`, `test/smoke.test.ts`
        - Check: `npm test`

        Add a "test" script. Keep the other scripts.

        - Detail bullets stay part of the instructions.

        ## Step 2: Holidays

        - Tier: heavy
        - Parallel group: core
        - Files: `src/server/marketHours.ts`, `test/marketHours.test.ts`
        - Check: `node --import tsx --test test/marketHours.test.ts`

        Good Friday 2026-04-03 is a holiday.
        Friday 2027-12-31 is not.

        ## Step 3: Watchlist

        - Tier: Light
        - Parallel group: core
        - Files: src/server/watchlist.ts, test/watchlist.test.ts
        - Check: node --import tsx --test test/watchlist.test.ts

        Validate it.

        ## Notes

        Not a step.
        """;

    [Fact]
    public void A_plan_file_keeps_every_step_and_its_instructions_as_written()
    {
        PlanFileContent plan = PlanFile.Parse(Sample(@"C:\src\board").Replace("\n", "\r\n"), defaultWorkingDirectory: null);

        Assert.Equal("A reliable, tested board", plan.Title);
        Assert.Equal(@"C:\src\board", plan.WorkingDirectory);
        Assert.Equal("close the gaps the architecture lists. The board learns holidays.", plan.Goal);
        Assert.Equal(["Tests use node:test through tsx: no new dependencies.", "Imports stay without extensions."], plan.Assumptions);
        Assert.Equal(3, plan.Steps.Count);

        PlanStepInput first = plan.Steps[0];
        Assert.Equal("Add a test runner", first.Title);
        Assert.Equal("light", first.Tier);
        Assert.Null(first.ParallelGroup);
        Assert.Equal(["package.json", "test/smoke.test.ts"], first.Files!);
        Assert.Equal("npm test", first.Verify);
        Assert.Equal("Add a \"test\" script. Keep the other scripts.\n\n- Detail bullets stay part of the instructions.", first.Detail);

        PlanStepInput second = plan.Steps[1];
        Assert.Equal(("heavy", "core"), (second.Tier, second.ParallelGroup));
        Assert.Equal("node --import tsx --test test/marketHours.test.ts", second.Verify);
        Assert.Equal("Good Friday 2026-04-03 is a holiday.\nFriday 2027-12-31 is not.", second.Detail);

        PlanStepInput third = plan.Steps[2];
        Assert.Equal("light", third.Tier);
        Assert.Equal(["src/server/watchlist.ts", "test/watchlist.test.ts"], third.Files!);
        Assert.Equal("Validate it.", third.Detail);
    }

    [Fact]
    public void A_plan_file_without_steps_is_refused_and_without_a_working_directory_uses_its_folder()
    {
        Assert.Contains("no \"## Step 1: title\" sections", Assert.Throws<FormatException>(() => PlanFile.Parse("# Plan\n\nJust prose.", null)).Message);

        PlanFileContent plan = PlanFile.Parse("## Step 1: Only step\n\n- Check: npm test\n\nDo it.", @"C:\src\app", "PLAN");
        Assert.Equal(("PLAN", "PLAN", @"C:\src\app"), (plan.Title, plan.Goal, plan.WorkingDirectory));
    }

    [Fact]
    public async Task Propose_plan_with_a_plan_file_saves_the_steps_with_their_full_instructions()
    {
        string file = Path.Combine(_project, "PLAN.md");
        File.WriteAllText(file, Sample(_project));
        var tools = new PlanTools(Store, Valid, (_, _, _) => Task.FromResult("Exit code: 0"), programExists: program => program is "npm" or "node");

        string result = await tools.ProposePlanFromFileAsync(file, default);

        Assert.StartsWith("Plan saved", result);
        PlanRecord plan = Store.Get(FleetPlanStore.FindMarkerId(result)!)!;
        Assert.Equal(PlanStatus.AwaitingApproval, plan.Status);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal("Good Friday 2026-04-03 is a holiday.\nFriday 2027-12-31 is not.", plan.Steps[1].Detail);
        Assert.Equal(["core", "core"], plan.Steps.Skip(1).Select(step => step.ParallelGroup));
    }

    [Fact]
    public void A_planner_retyping_a_plan_file_it_was_pointed_to_gets_the_file_instead()
    {
        string file = Path.Combine(_project, "PLAN.md");
        File.WriteAllText(file, Sample(_project));
        var threeSteps = new Dictionary<string, object?>
        {
            ["title"] = "Board",
            ["steps"] = JsonSerializer.SerializeToElement(new[] { new { title = "a" }, new { title = "b" }, new { title = "c" } })
        };

        // Named by the user.
        List<ChatMessage> named = [new(ChatRole.User, $"Carry out the plan in {file}, please.")];
        Assert.Equal(file, PlanFile.FindTranscribed(named, threeSteps));

        // Read by the planner.
        List<ChatMessage> read =
        [
            new(ChatRole.User, "carry out the plan in the project"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = file })])
        ];
        Assert.Equal(file, PlanFile.FindTranscribed(read, threeSteps));

        // A different number of steps is the planner's own plan; a planFile it gave is kept; a URL is not a file.
        var twoSteps = new Dictionary<string, object?> { ["steps"] = JsonSerializer.SerializeToElement(new[] { new { title = "a" }, new { title = "b" } }) };
        Assert.Null(PlanFile.FindTranscribed(named, twoSteps));
        Assert.Null(PlanFile.FindTranscribed(named, new Dictionary<string, object?>(threeSteps) { ["planFile"] = file }));
        Assert.Null(PlanFile.FindTranscribed([new(ChatRole.User, "see https://example.com/PLAN.md")], threeSteps));
    }

    [Fact]
    public void A_plan_file_the_latest_request_names_is_pointed_out_until_the_planner_proposes_it()
    {
        string file = Path.Combine(_project, "PLAN.md");
        File.WriteAllText(file, Sample(_project));
        string notes = Path.Combine(_project, "NOTES.md");
        File.WriteAllText(notes, "# Notes\n\nNo steps here.");

        List<ChatMessage> asked = [new(ChatRole.User, $"Look at {notes} and carry out the plan in {file}.")];
        Assert.Equal((file, 3), PlanFile.NamedInLatestRequest(asked));
        Assert.Contains($"names {file}, a plan file in the fleet's format with 3 steps", PlanGate.PlanFileNote(file, 3));

        // Reading around does not end it; proposing does, and a later request without the file does not bring it back.
        List<ChatMessage> reading =
        [
            .. asked,
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = file })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "...")])
        ];
        Assert.Equal((file, 3), PlanFile.NamedInLatestRequest(reading));
        List<ChatMessage> proposed =
        [
            .. reading,
            new(ChatRole.Assistant, [new FunctionCallContent("c2", "propose_plan", new Dictionary<string, object?> { ["planFile"] = file })])
        ];
        Assert.Null(PlanFile.NamedInLatestRequest(proposed));
        Assert.Null(PlanFile.NamedInLatestRequest([.. proposed, new(ChatRole.User, "make step 2 standard instead")]));
        Assert.Null(PlanFile.NamedInLatestRequest([new(ChatRole.User, $"Summarise {notes}")]));
    }

    [Fact]
    public void A_plan_of_its_own_proposed_in_answer_to_a_named_plan_file_gets_the_files_steps()
    {
        string file = Path.Combine(_project, "PLAN.md");
        File.WriteAllText(file, Sample(_project));
        var twoSteps = new Dictionary<string, object?> { ["title"] = "Add test runner", ["steps"] = JsonSerializer.SerializeToElement(new[] { new { title = "a" }, new { title = "b" } }) };
        List<ChatMessage> turn =
        [
            new(ChatRole.User, $"Carry out the plan in {file}"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = file })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "...")]),
            new(ChatRole.Assistant, [new FunctionCallContent("c2", "propose_plan", twoSteps)])
        ];

        Assert.Equal(file, PlanFile.NamedByLatestRequest(turn, twoSteps));

        // A planFile the planner gave is its own; a later request that names no file is about the plan as proposed.
        Assert.Null(PlanFile.NamedByLatestRequest(turn, new Dictionary<string, object?>(twoSteps) { ["planFile"] = file }));
        Assert.Null(PlanFile.NamedByLatestRequest([.. turn, new(ChatRole.User, "make step 2 heavy")], twoSteps));
    }

    [Fact]
    public async Task A_plan_file_that_would_fail_is_not_saved_and_the_user_is_told_what_to_change()
    {
        string file = Path.Combine(_project, "PLAN.md");
        File.WriteAllText(file, "# Plan\n\nWorking directory: `" + _project + "`\n\n## Step 1: Guess\n\n- Files: `src/a.js`\n- Check: make sure it works\n\nDo it.");
        var tools = new PlanTools(Store, Valid, (_, _, _) => Task.FromResult("Exit code: 0"), programExists: program => program is "npm" or "node");

        string result = await tools.ProposePlanFromFileAsync(file, default);

        Assert.StartsWith("The plan was NOT saved", result);
        Assert.Contains("tell the user what to change in the plan file", result);
        Assert.Empty(Store.List());
        Assert.StartsWith("The plan file could not be read", await tools.ProposePlanFromFileAsync(Path.Combine(_project, "missing.md"), default));
        Assert.StartsWith("The plan file could not be read", await tools.ProposePlanFromFileAsync("PLAN.md", default));
    }
}
