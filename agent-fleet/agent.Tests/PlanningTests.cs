using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

internal sealed class FakeValidator(Func<string, DiagramCheck> check) : IDiagramValidator
{
    public Task<DiagramCheck> CheckAsync(string code, CancellationToken cancellationToken) => Task.FromResult(check(code));
}

public abstract class PlanTestBase : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-plan-tests-" + Guid.NewGuid().ToString("N"));

    protected PlanTestBase()
    {
        Directory.CreateDirectory(_dir);
        Store = new FleetPlanStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = _dir })
            .Build());
    }

    internal FleetPlanStore Store { get; }

    protected string PlansDirectory => _dir;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal static PlanStepInput Step(string title, string? verify = "dotnet build", string? tier = null) =>
        new(title, "detail of " + title, ["a.cs"], verify, tier);

    internal PlanRecord NewPlan(params PlanStepInput[] steps) =>
        Store.Create("Add a rate limiter", "Limit requests", @"C:\work", ["a"], ["q?"], ["r"], null, null, steps.Length > 0 ? steps : [Step("one")]);
}

public sealed class FleetPlanStoreTests : PlanTestBase
{
    [Fact]
    public void Steps_are_numbered_from_one_with_defaults()
    {
        PlanRecord plan = NewPlan(Step("a", tier: "HEAVY"), Step("b", tier: "nonsense"), Step("c", verify: " "));

        Assert.Equal([1, 2, 3], plan.Steps.Select(step => step.Id));
        Assert.Equal(["heavy", "standard", "standard"], plan.Steps.Select(step => step.Tier));
        Assert.Null(plan.Steps[2].Verify);
        Assert.All(plan.Steps, step => Assert.Equal(StepStatus.Pending, step.Status));
        Assert.Equal(PlanStatus.AwaitingApproval, plan.Status);
    }

    [Fact]
    public void A_plan_needs_a_title_and_between_one_and_twenty_steps()
    {
        Assert.Throws<ArgumentException>(() => Store.Create("", "g", null, null, null, null, null, null, [Step("a")]));
        Assert.Throws<ArgumentException>(() => Store.Create("t", "g", null, null, null, null, null, null, []));
        Assert.Throws<ArgumentException>(() => Store.Create("t", "g", null, null, null, null, null, null, Enumerable.Range(0, 21).Select(i => Step("s" + i)).ToList()));
        Assert.Throws<ArgumentException>(() => Store.Create("t", "g", null, null, null, null, null, null, [new PlanStepInput("", "d")]));
    }

    [Fact]
    public void A_saved_plan_round_trips_and_bad_ids_are_refused()
    {
        PlanRecord plan = NewPlan(Step("a"));

        PlanRecord? loaded = Store.Get(plan.Id);

        Assert.Equal(plan.Title, loaded!.Title);
        Assert.Equal(@"C:\work", loaded.WorkingDirectory);
        Assert.Null(Store.Get("../../secret"));
        Assert.Null(Store.Get("not-a-guid"));
        Assert.Null(Store.Get(new string('0', 32)));
    }

    [Fact]
    public void Only_a_waiting_or_blocked_plan_can_be_approved()
    {
        PlanRecord plan = NewPlan();

        Assert.Equal(PlanStatus.Approved, Store.Approve(plan.Id)!.Status);
        Assert.NotNull(Store.Get(plan.Id)!.ApprovedUtc);

        Store.Update(plan.Id, p => p with { Status = PlanStatus.Done });
        Assert.Equal(PlanStatus.Done, Store.Approve(plan.Id)!.Status);
    }

    [Fact]
    public void Reject_marks_the_plan_rejected_but_never_a_finished_one()
    {
        PlanRecord waiting = NewPlan();
        PlanRecord finished = NewPlan();
        Store.Update(finished.Id, p => p with { Status = PlanStatus.Done });

        Assert.Equal(PlanStatus.Rejected, Store.Reject(waiting.Id)!.Status);
        Assert.Equal(PlanStatus.Done, Store.Reject(finished.Id)!.Status);
    }

    [Fact]
    public void List_is_newest_first_and_counts_done_steps()
    {
        PlanRecord first = NewPlan(Step("a"), Step("b"));
        Thread.Sleep(15);
        PlanRecord second = NewPlan();
        Store.Update(first.Id, p => p with { Steps = p.Steps.Select(s => s.Id == 1 ? s with { Status = StepStatus.Done } : s).ToList() });

        IReadOnlyList<PlanSummary> list = Store.List();

        Assert.Equal(first.Id, list[0].Id);
        Assert.Equal(1, list[0].StepsDone);
        Assert.Equal(2, list[0].StepsTotal);
        Assert.Equal(second.Id, list[1].Id);
    }

    [Fact]
    public void FindActive_ignores_finished_and_rejected_plans()
    {
        PlanRecord done = NewPlan();
        Store.Update(done.Id, p => p with { Status = PlanStatus.Done });
        Thread.Sleep(15);
        PlanRecord waiting = NewPlan();

        Assert.Equal(waiting.Id, Store.FindActive()!.Id);
        Store.Reject(waiting.Id);
        Assert.Null(Store.FindActive());
    }

    [Fact]
    public void Markdown_carries_the_marker_status_boxes_and_a_fenced_diagram()
    {
        PlanRecord plan = Store.Create("T", "G", null, null, null, null, "flowchart TD\n  A-->B", null, [Step("a"), Step("b")]);
        plan = Store.Update(plan.Id, p => p with { Steps = p.Steps.Select(s => s.Id == 1 ? s with { Status = StepStatus.Done } : s).ToList() })!;

        string markdown = FleetPlanStore.ToMarkdown(plan);

        Assert.Contains($"[plan:{plan.Id}]", markdown);
        Assert.Contains("- [x] **1. a**", markdown);
        Assert.Contains("- [ ] **2. b**", markdown);
        Assert.Contains("```mermaid\nflowchart TD", markdown.Replace("\r\n", "\n"));
        Assert.Contains("Verify: `dotnet build`", markdown);
    }

    [Fact]
    public void The_marker_can_be_found_in_text_and_ids_are_validated()
    {
        string id = Guid.NewGuid().ToString("N");

        Assert.Equal(id, FleetPlanStore.FindMarkerId($"Plan saved. [plan:{id}]\nmore"));
        Assert.Null(FleetPlanStore.FindMarkerId("[plan:short]"));
        Assert.Null(FleetPlanStore.FindMarkerId(null));
        Assert.True(FleetPlanStore.IsValidId(id));
        Assert.False(FleetPlanStore.IsValidId("../x"));
    }
}

public sealed class PlanToolsTests : PlanTestBase
{
    private static readonly FakeValidator AlwaysValid = new(code => new DiagramCheck(true, true, code, null));

    private PlanTools Tools(Func<string, string> run, IDiagramValidator? validator = null) =>
        new(Store, validator ?? AlwaysValid, (command, _, _) => Task.FromResult(run(command)));

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private static string Fail(string _) => "Exit code: 1\n--- stdout ---\nerror CS1002: ; expected";

    private static JsonElement? Steps(params PlanStepInput[] steps) => JsonSerializer.SerializeToElement(steps);

    private static JsonElement? Items(params string[] items) => JsonSerializer.SerializeToElement(items);

    private PlanRecord Approved(params PlanStepInput[] steps)
    {
        PlanRecord plan = NewPlan(steps);
        return Store.Approve(plan.Id)!;
    }

    [Fact]
    public async Task Proposing_a_plan_saves_it_and_tells_the_model_to_stop()
    {
        PlanTools tools = Tools(Pass);

        string reply = await tools.ProposePlanAsync("T", "G", @"C:\p", Items("a"), null, Items("r"), "flowchart TD\n A-->B", Steps(Step("x")), default);

        PlanRecord saved = Assert.Single(Store.List()) is { } s ? Store.Get(s.Id)! : null!;
        Assert.Contains($"[plan:{saved.Id}]", reply);
        Assert.Contains("Do not start the work", reply);
        Assert.Equal("flowchart TD\n A-->B", saved.Diagram);
        Assert.Equal(PlanStatus.AwaitingApproval, saved.Status);
    }

    [Fact]
    public async Task A_plan_without_steps_is_refused()
    {
        string reply = await Tools(Pass).ProposePlanAsync("T", "G", null, null, null, null, null, Steps(), default);

        Assert.StartsWith("Error", reply);
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task A_broken_diagram_is_bounced_once_then_dropped_so_the_plan_is_never_lost()
    {
        var broken = new FakeValidator(code => new DiagramCheck(false, true, code, "Parse error on line 2"));
        PlanTools tools = Tools(Pass, broken);

        string first = await tools.ProposePlanAsync("Same title", "G", null, null, null, null, "flowchart TD\n A(", Steps(Step("x")), default);
        Assert.Contains("NOT saved", first);
        Assert.Contains("Parse error on line 2", first);
        Assert.Empty(Store.List());

        string second = await tools.ProposePlanAsync("Same title", "G", null, null, null, null, "flowchart TD\n A(", Steps(Step("x")), default);
        Assert.Contains("Plan saved", second);
        PlanRecord saved = Store.Get(Store.List()[0].Id)!;
        Assert.Null(saved.Diagram);
        Assert.Contains("left out", saved.DiagramNote);
    }

    [Fact]
    public async Task An_unchecked_diagram_is_kept_with_a_note()
    {
        var unchecked_ = new FakeValidator(code => new DiagramCheck(true, false, code, null));

        await Tools(Pass, unchecked_).ProposePlanAsync("T", "G", null, null, null, null, "flowchart TD\n A-->B", Steps(Step("x")), default);

        PlanRecord saved = Store.Get(Store.List()[0].Id)!;
        Assert.NotNull(saved.Diagram);
        Assert.Contains("could not be checked", saved.DiagramNote);
    }

    [Fact]
    public async Task No_step_can_be_completed_before_approval()
    {
        PlanRecord plan = NewPlan(Step("a"));

        string reply = await Tools(Pass).CompleteStepAsync(plan.Id, 1, null, default);

        Assert.StartsWith("Error", reply);
        Assert.Contains("approval", reply);
        Assert.Equal(StepStatus.Pending, Store.Get(plan.Id)!.Steps[0].Status);
    }

    [Fact]
    public async Task A_step_is_done_only_when_its_own_verify_command_passes()
    {
        PlanRecord plan = Approved(Step("a", verify: "dotnet build"), Step("b", verify: "dotnet test"));
        var ran = new List<string>();
        PlanTools tools = Tools(command => { ran.Add(command); return Pass(command); });

        string reply = await tools.CompleteStepAsync(plan.Id, 1, "built fine", default);

        Assert.Equal(["dotnet build"], ran);
        Assert.Contains("verified and marked done", reply);
        Assert.Contains("Next: step 2", reply);
        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(StepStatus.Done, after.Steps[0].Status);
        Assert.Equal("built fine", after.Steps[0].Note);
        Assert.Equal(PlanStatus.Running, after.Status);
    }

    [Fact]
    public async Task A_failing_verify_leaves_the_step_open_shows_the_output_and_counts_attempts()
    {
        PlanRecord plan = Approved(Step("a"));
        PlanTools tools = Tools(Fail);

        string first = await tools.CompleteStepAsync(plan.Id, 1, null, default);
        string second = await tools.CompleteStepAsync(plan.Id, 1, null, default);

        Assert.Contains("NOT done", first);
        Assert.Contains("CS1002", first);
        Assert.Contains("attempt 2", second);
        PlanStep step = Store.Get(plan.Id)!.Steps[0];
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Equal(2, step.Attempts);
    }

    [Fact]
    public async Task A_failed_step_can_be_retried_and_then_pass()
    {
        PlanRecord plan = Approved(Step("a"));
        bool fix = false;
        PlanTools tools = Tools(command => fix ? Pass(command) : Fail(command));

        await tools.CompleteStepAsync(plan.Id, 1, null, default);
        fix = true;
        string reply = await tools.CompleteStepAsync(plan.Id, 1, null, default);

        Assert.Contains("marked done", reply);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task Steps_must_be_completed_in_order()
    {
        PlanRecord plan = Approved(Step("a"), Step("b"));

        string reply = await Tools(Pass).CompleteStepAsync(plan.Id, 2, null, default);

        Assert.Contains("step 1", reply);
        Assert.Contains("in order", reply);
        Assert.Equal(StepStatus.Pending, Store.Get(plan.Id)!.Steps[1].Status);
    }

    [Fact]
    public async Task A_step_without_a_verify_command_is_done_but_says_nothing_was_checked()
    {
        PlanRecord plan = Approved(Step("a", verify: null));

        string reply = await Tools(_ => throw new InvalidOperationException("must not run")).CompleteStepAsync(plan.Id, 1, null, default);

        Assert.Contains("nothing was checked", reply);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task Finishing_the_last_step_completes_the_plan()
    {
        PlanRecord plan = Approved(Step("a"), Step("b"));
        PlanTools tools = Tools(Pass);

        await tools.CompleteStepAsync(plan.Id, 1, null, default);
        string last = await tools.CompleteStepAsync(plan.Id, 2, null, default);

        Assert.Contains("All steps are done", last);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task Bad_ids_and_step_numbers_are_reported()
    {
        PlanRecord plan = Approved(Step("a"));
        PlanTools tools = Tools(Pass);

        Assert.Contains("no plan", await tools.CompleteStepAsync("nope", 1, null, default));
        Assert.Contains("no step 9", await tools.CompleteStepAsync(plan.Id, 9, null, default));
    }

    [Fact]
    public void Failing_a_step_blocks_the_plan_and_tells_the_model_to_stop()
    {
        PlanRecord plan = Approved(Step("a"));

        string reply = Tools(Pass).FailStep(plan.Id, 1, "the API changed");

        Assert.Contains("blocked", reply);
        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal("the API changed", after.Steps[0].Note);
    }

    [Fact]
    public void GetPlan_without_an_id_shows_the_active_plan()
    {
        PlanRecord plan = NewPlan();

        Assert.Contains($"[plan:{plan.Id}]", Tools(Pass).GetPlan(null));
        Assert.Contains("no plan", Tools(Pass).GetPlan("0000"), StringComparison.OrdinalIgnoreCase);
    }

    // The arguments below are what the fleet's own model actually sent to propose_plan: text where
    // arrays were documented, and steps as plain strings. A strict schema rejected it (and the
    // framework hid why); accepting it produces a real plan.
    [Fact]
    public async Task The_arguments_a_real_model_sent_still_produce_a_plan()
    {
        JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        string reply = await Tools(Pass).ProposePlanAsync(
            "Create Token Bucket Rate Limiter Module",
            "Create a JavaScript module with a TokenBucket class and a test file",
            @"C:\work",
            Json("\"The directory exists and is writable. Node.js is installed on the system.\""),
            Json("\"None\""),
            Json("\"None significant\""),
            "",
            Json("[\"Create ratelimit.js with a TokenBucket class taking capacity and refillPerSecond, and a tryTake() method\",\"Create ratelimit.test.js using node:test\"]"),
            default);

        Assert.Contains("Plan saved", reply);
        PlanRecord plan = Store.Get(Store.List()[0].Id)!;
        Assert.Equal(2, plan.Steps.Count);
        Assert.StartsWith("Create ratelimit.js", plan.Steps[0].Title);
        Assert.Equal(["The directory exists and is writable. Node.js is installed on the system."], plan.Assumptions);
        Assert.Empty(plan.OpenQuestions);
        Assert.Empty(plan.Risks);
        Assert.Null(plan.Diagram);
    }

    [Fact]
    public async Task A_plan_with_no_usable_steps_tells_the_model_the_expected_shape()
    {
        string reply = await Tools(Pass).ProposePlanAsync("T", "G", null, null, null, null, null, JsonDocument.Parse("[]").RootElement, default);

        Assert.StartsWith("Error", reply);
        Assert.Contains("title, detail, files, verify and tier", reply);
    }
}

public sealed class PlanArgumentParsingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Lists_are_accepted_as_arrays_or_text_and_placeholders_are_dropped()
    {
        Assert.Equal(["a", "b"], PlanTools.ToStringList(Json("[\"a\",\" b \"]")));
        Assert.Equal(["one", "two", "three"], PlanTools.ToStringList(Json("\"- one\\n2. two; three\"")));
        Assert.Empty(PlanTools.ToStringList(Json("\"None\"")));
        Assert.Empty(PlanTools.ToStringList(Json("[\"N/A\",\"\",\"none identified\"]")));
        Assert.Empty(PlanTools.ToStringList(null));
        Assert.Empty(PlanTools.ToStringList(Json("null")));
        Assert.Equal(["7"], PlanTools.ToStringList(Json("[7]")));
    }

    [Fact]
    public void Steps_as_documented_objects_keep_every_field()
    {
        List<PlanStepInput> steps = PlanTools.ToSteps(Json("""
            [{"title":"Add class","detail":"Write it","files":["a.js","b.js"],"verify":"node --test","tier":"heavy"}]
            """));

        PlanStepInput step = Assert.Single(steps);
        Assert.Equal("Add class", step.Title);
        Assert.Equal("Write it", step.Detail);
        Assert.Equal(["a.js", "b.js"], step.Files);
        Assert.Equal("node --test", step.Verify);
        Assert.Equal("heavy", step.Tier);
    }

    [Fact]
    public void Steps_with_other_field_names_or_a_files_string_are_understood()
    {
        List<PlanStepInput> steps = PlanTools.ToSteps(Json("""
            [{"name":"Wire it up","description":"Connect x to y","files":"a.cs, b.cs","verification":"dotnet build"}]
            """));

        PlanStepInput step = Assert.Single(steps);
        Assert.Equal("Wire it up", step.Title);
        Assert.Equal("Connect x to y", step.Detail);
        Assert.Equal(["a.cs", "b.cs"], step.Files);
        Assert.Equal("dotnet build", step.Verify);
    }

    [Fact]
    public void Steps_as_strings_or_one_numbered_block_become_separate_steps()
    {
        List<PlanStepInput> fromArray = PlanTools.ToSteps(Json("[\"First: do a thing\",\"Second thing\"]"));
        Assert.Equal(["First", "Second thing"], fromArray.Select(s => s.Title));
        Assert.Equal("First: do a thing", fromArray[0].Detail);

        List<PlanStepInput> fromText = PlanTools.ToSteps(Json("\"1. Create the file\\n2. Run the tests\""));
        Assert.Equal(["Create the file", "Run the tests"], fromText.Select(s => s.Title));
    }

    // What the model sent for `steps` in a real run: the JSON array as one string.
    [Fact]
    public void Steps_sent_as_a_string_of_json_are_parsed_not_split_into_lines()
    {
        const string text = "[\n  {\n    \"title\": \"Create the module\",\n    \"detail\": \"Write ratelimit.js\",\n    \"files\": [\"ratelimit.js\"],\n    \"verify\": \"node -e \\\"require('./ratelimit')\\\"\",\n    \"tier\": \"standard\"\n  },\n  {\n    \"title\": \"Add tests\",\n    \"files\": [\"ratelimit.test.js\"],\n    \"verify\": \"node --test\"\n  }\n]";

        List<PlanStepInput> steps = PlanTools.ToSteps(JsonSerializer.SerializeToElement(text));

        Assert.Equal(["Create the module", "Add tests"], steps.Select(step => step.Title));
        Assert.Equal("node --test", steps[1].Verify);
        Assert.Equal(["ratelimit.js"], steps[0].Files);
    }

    [Fact]
    public void A_json_looking_string_that_does_not_parse_gives_no_steps_rather_than_garbage()
    {
        Assert.Empty(PlanTools.ToSteps(JsonSerializer.SerializeToElement("[\n {\"title\": \"unterminated\"")));
    }

    [Fact]
    public void A_list_sent_as_a_string_of_json_is_parsed()
    {
        Assert.Equal(["a", "b"], PlanTools.ToStringList(JsonSerializer.SerializeToElement("[\"a\", \"b\"]")));
    }

    [Fact]
    public void Unusable_entries_are_skipped_and_a_long_title_is_shortened()
    {
        List<PlanStepInput> steps = PlanTools.ToSteps(Json("[123, null, {}, \"" + new string('x', 200) + "\"]"));

        PlanStepInput step = Assert.Single(steps);
        Assert.True(step.Title.Length <= 95);
        Assert.Equal(200, step.Detail.Length);
    }
}

public sealed class PlanGateTests : PlanTestBase
{
    private static readonly IReadOnlySet<string> ReadOnly = new HashSet<string> { "read_file", "search_files", "get_plan", "docs_search" };

    private static AITool Tool(string name) => AIFunctionFactory.Create(() => "x", name: name);

    private static ChatOptions AllTools() => new()
    {
        Tools =
        [
            Tool("read_file"), Tool("write_file"), Tool("edit_file"), Tool("run_command"), Tool("search_files"),
            Tool("docs_search"), Tool("propose_plan"), Tool("get_plan")
        ]
    };

    private static string[] Names(ChatOptions? options) => options!.Tools!.Select(tool => tool.Name).OrderBy(n => n).ToArray();

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage ToolResult(string text) => new(ChatRole.Tool, [new FunctionResultContent("call", text)]);

    [Fact]
    public void With_plan_mode_off_everything_is_offered_except_the_plan_tools()
    {
        PlanGateResult gate = PlanGate.Evaluate(false, [User("hi")], Store);

        Assert.Equal(PlanPhase.Off, gate.Phase);
        Assert.Equal(
            ["docs_search", "edit_file", "read_file", "run_command", "search_files", "write_file"],
            Names(PlanGate.FilterTools(AllTools(), gate.Phase, ReadOnly)));
    }

    [Fact]
    public void With_no_plan_yet_only_read_only_tools_and_propose_plan_are_offered()
    {
        PlanGateResult gate = PlanGate.Evaluate(true, [User("build a thing")], Store);

        Assert.Equal(PlanPhase.Planning, gate.Phase);
        Assert.Null(gate.Plan);
        string[] offered = Names(PlanGate.FilterTools(AllTools(), gate.Phase, ReadOnly));
        Assert.Equal(["docs_search", "get_plan", "propose_plan", "read_file", "search_files"], offered);
        Assert.DoesNotContain("write_file", offered);
        Assert.DoesNotContain("edit_file", offered);
        Assert.DoesNotContain("run_command", offered);
    }

    [Fact]
    public void An_unapproved_plan_keeps_the_conversation_in_planning()
    {
        PlanRecord plan = NewPlan();

        PlanGateResult gate = PlanGate.Evaluate(true, [User("go"), ToolResult($"Plan saved. [plan:{plan.Id}]"), User("looks interesting, but change step 3")], Store);

        Assert.Equal(PlanPhase.Planning, gate.Phase);
        Assert.Equal(plan.Id, gate.Plan!.Id);
        Assert.Equal(PlanStatus.AwaitingApproval, Store.Get(plan.Id)!.Status);
    }

    [Theory]
    [InlineData("APPROVE")]
    [InlineData("approve.")]
    [InlineData("  Confirm  ")]
    [InlineData("go ahead!")]
    [InlineData("proceed")]
    public void An_approval_word_from_the_user_approves_the_plan_in_code(string reply)
    {
        PlanRecord plan = NewPlan();

        PlanGateResult gate = PlanGate.Evaluate(true, [ToolResult($"Plan saved. [plan:{plan.Id}]"), User(reply)], Store);

        Assert.Equal(PlanPhase.Executing, gate.Phase);
        Assert.Equal(PlanStatus.Approved, Store.Get(plan.Id)!.Status);
    }

    [Theory]
    [InlineData("approve the idea but change step 3")]
    [InlineData("I do not approve")]
    [InlineData("what does approve do?")]
    [InlineData("")]
    public void Anything_but_a_plain_approval_is_feedback_not_a_go_ahead(string reply)
    {
        PlanRecord plan = NewPlan();

        PlanGateResult gate = PlanGate.Evaluate(true, [ToolResult($"[plan:{plan.Id}]"), User(reply)], Store);

        Assert.Equal(PlanPhase.Planning, gate.Phase);
        Assert.Equal(PlanStatus.AwaitingApproval, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public void An_approved_plan_puts_chat_in_read_only_monitor_mode_because_the_runner_does_the_work()
    {
        PlanRecord plan = Store.Approve(NewPlan().Id)!;

        PlanGateResult gate = PlanGate.Evaluate(true, [User("start"), ToolResult($"[plan:{plan.Id}]"), User("continue")], Store);

        Assert.Equal(PlanPhase.Executing, gate.Phase);
        string[] offered = Names(PlanGate.FilterTools(AllTools(), gate.Phase, ReadOnly));
        Assert.Equal(["docs_search", "get_plan", "read_file", "search_files"], offered);
        Assert.DoesNotContain("write_file", offered);
        Assert.DoesNotContain("run_command", offered);
        Assert.DoesNotContain("propose_plan", offered);
    }

    [Fact]
    public void The_marker_in_a_user_message_is_enough_to_find_the_plan()
    {
        PlanRecord plan = Store.Approve(NewPlan().Id)!;

        PlanGateResult gate = PlanGate.Evaluate(true, [User($"Plan approved [plan:{plan.Id}]. Execute it.")], Store);

        Assert.Equal(PlanPhase.Executing, gate.Phase);
    }

    [Theory]
    [InlineData(PlanStatus.Done)]
    [InlineData(PlanStatus.Rejected)]
    public void A_finished_or_rejected_plan_does_not_unlock_further_changes(string status)
    {
        PlanRecord plan = NewPlan();
        Store.Update(plan.Id, p => p with { Status = status });

        PlanGateResult gate = PlanGate.Evaluate(true, [ToolResult($"[plan:{plan.Id}]"), User("also add logging")], Store);

        Assert.Equal(PlanPhase.Planning, gate.Phase);
    }

    [Fact]
    public void A_blocked_plan_can_be_resumed_by_approving_again()
    {
        PlanRecord plan = NewPlan();
        Store.Update(plan.Id, p => p with { Status = PlanStatus.Blocked });

        PlanGateResult gate = PlanGate.Evaluate(true, [ToolResult($"[plan:{plan.Id}]"), User("approve")], Store);

        Assert.Equal(PlanPhase.Executing, gate.Phase);
    }

    [Fact]
    public void Filtering_leaves_the_original_options_untouched_and_handles_no_tools()
    {
        ChatOptions original = AllTools();

        PlanGate.FilterTools(original, PlanPhase.Planning, ReadOnly);

        Assert.Equal(8, original.Tools!.Count);
        Assert.Null(PlanGate.FilterTools(null, PlanPhase.Planning, ReadOnly));
        Assert.Equal(6, PlanGate.FilterTools(original, PlanPhase.Off, ReadOnly)!.Tools!.Count);
        Assert.Equal(8, original.Tools.Count);
    }
}

public sealed class DiagramSanitizerTests
{
    [Fact]
    public void Parentheses_in_flowchart_labels_get_quoted_which_is_what_the_standard_worker_got_wrong()
    {
        string fixedCode = DiagramSanitizer.Fix("flowchart TD\n  D --> E[Heavy Hub (Ollama)]\n  E --> F[Plain]");

        Assert.Contains("E[\"Heavy Hub (Ollama)\"]", fixedCode);
        Assert.Contains("F[Plain]", fixedCode);
    }

    [Fact]
    public void Already_quoted_labels_and_other_diagram_types_are_left_alone()
    {
        const string quoted = "flowchart TD\n  A[\"Start (now)\"] --> B";
        const string sequence = "sequenceDiagram\n  A->>B: call (x)";

        Assert.Equal(quoted, DiagramSanitizer.Fix(quoted));
        Assert.Equal(sequence, DiagramSanitizer.Fix(sequence));
    }

    [Fact]
    public void A_code_fence_around_the_diagram_is_removed()
    {
        Assert.Equal("flowchart TD\n  A --> B", DiagramSanitizer.Fix("```mermaid\nflowchart TD\n  A --> B\n```"));
        Assert.Equal("sequenceDiagram\n  A->>B: hi", DiagramSanitizer.Fix("```\nsequenceDiagram\n  A->>B: hi\n```"));
    }
}
