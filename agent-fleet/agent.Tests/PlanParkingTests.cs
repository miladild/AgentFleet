using System.Text.Json;

namespace AgentFleet.Tests;

internal sealed class RecordingNotifier : IPlanNotifier
{
    private readonly object _gate = new();

    public List<PlanNotice> Notices { get; } = [];

    public void Notify(PlanNotice notice)
    {
        lock (_gate)
        {
            Notices.Add(notice);
        }
    }
}

/// <summary>
/// Park, do not halt: a plan is a graph of steps, a step that cannot be finished is parked, and only the steps that depend on
/// it wait. The run ends with a report and tells the user once per transition.
/// </summary>
public sealed partial class PlanRunnerTests
{
    // Each step has a check of its own, named by its title, so a test can say which ones pass.
    private static PlanStepInput Node(string name, params int[] dependsOn) =>
        new(name, "do " + name, [], "check " + name, "standard", DependsOn: dependsOn.Length > 0 ? dependsOn : null);

    private PlanRecord Graph(params PlanStepInput[] steps) => Store.Approve(Store.Create("Graph", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null, steps).Id, autoRetries: 0)!;

    private static Func<string, string> PassingExcept(params string[] failing) =>
        command => failing.Contains(command, StringComparer.Ordinal) ? Fail(command) : Pass(command);

    // The first prompt of a step says "This step (3 of 4): three".
    private static string PromptsAbout(FakeStepAgent agent, string name) =>
        string.Join("\n", agent.Calls.Select(call => call.Prompt).Where(prompt =>
            System.Text.RegularExpressions.Regex.IsMatch(prompt, $@"This step \(\d+ of \d+\): {name}\r?$", System.Text.RegularExpressions.RegexOptions.Multiline)));

    [Fact]
    public async Task A_chain_whose_second_step_parks_leaves_the_later_steps_unrun_and_the_plan_blocked_with_a_report()
    {
        PlanRecord plan = Graph(Node("one"), Node("two"), Node("three"));
        FakeStepAgent agent = Agent();
        var notifier = new RecordingNotifier();

        await Runner(agent, PassingExcept("check two"), notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal([StepStatus.Done, StepStatus.Parked, StepStatus.Pending], after.Steps.Select(step => step.Status));
        Assert.NotEmpty(PromptsAbout(agent, "two"));
        Assert.Empty(PromptsAbout(agent, "three"));
        Assert.Contains("not pass its check", after.Steps[1].Note);
        Assert.Equal(RunEventKind.PlanBlocked, after.Events!.Last(e => e.Kind != RunEventKind.FilesChanged).Kind);

        PlanRunEvent parked = Assert.Single(after.Events!, e => e.Kind == RunEventKind.StepParked);
        Assert.Equal(2, parked.StepId);
        Assert.Equal("check-kept-failing", parked.FailureSignature);
        PlanRunEvent attention = Assert.Single(after.Events!, e => e.Kind == RunEventKind.PlanNeedsAttention);
        Assert.Contains("1 of 3 steps are done", attention.Detail);
        Assert.Contains("#2 (two)", attention.Detail);
        Assert.Contains("#3 (three)", attention.Detail);
        Assert.Contains("Not run, because they wait for a step that stopped: #3 (three)", Assert.Single(after.Events!, e => e.Kind == RunEventKind.PlanBlocked).Detail);

        string report = FleetPlanStore.ToReportMarkdown(after);
        Assert.Contains("## Where it stopped", report);
        Assert.Contains("Step 2, two", report);
        Assert.Contains("(cause: check-kept-failing)", report);
        Assert.Contains("[p] 2. two", report);
        Assert.Contains("1 of 3 steps done, 1 parked or stopped, 1 waiting for them", report);
        Assert.Contains("  - Waiting on #2", report);
        Assert.Contains("## What to do next", report);
        Assert.Contains("Step 2 (two) is parked: fix the cause and retry it", report);
        Assert.Contains("Step 3 was not run", report);
    }

    [Fact]
    public async Task A_step_that_does_not_depend_on_the_parked_one_runs_to_done_while_the_final_step_waits()
    {
        // 1 passes, 2 parks, 3 needs only 1, 4 is the final step and needs everything.
        PlanRecord plan = Graph(Node("one"), Node("two", 1), Node("three", 1), Node("four", 1, 2, 3));
        FakeStepAgent agent = Agent();

        await Runner(agent, PassingExcept("check two")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal([StepStatus.Done, StepStatus.Parked, StepStatus.Done, StepStatus.Pending], after.Steps.Select(step => step.Status));
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Empty(PromptsAbout(agent, "four"));

        // Step 3 ran after step 2 was parked: the plan did not stop at the first failure.
        int parkedAt = after.Events!.ToList().FindIndex(e => e.Kind == RunEventKind.StepParked && e.StepId == 2);
        int threeStarted = after.Events!.ToList().FindIndex(e => e.Kind == RunEventKind.AttemptStarted && e.StepId == 3);
        Assert.True(parkedAt >= 0 && threeStarted > parkedAt, $"parked at {parkedAt}, step 3 started at {threeStarted}");
        Assert.Contains("#4 (four)", Assert.Single(after.Events!, e => e.Kind == RunEventKind.PlanBlocked).Detail);
    }

    [Fact]
    public async Task The_final_step_depends_on_every_step_even_when_it_names_only_one()
    {
        PlanRecord plan = Graph(Node("one"), Node("two", 1), Node("three", 1));
        Assert.Equal([1, 2], plan.Steps[2].DependsOn);

        await Runner(Agent(), PassingExcept("check two")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(StepStatus.Pending, after.Steps[2].Status);
    }

    [Fact]
    public async Task A_parked_step_that_is_retried_goes_on_and_only_the_steps_that_waited_for_it_follow()
    {
        PlanRecord plan = Graph(Node("one"), Node("two", 1), Node("three", 1), Node("four", 1, 2, 3));
        bool twoWorks = false;
        FakeStepAgent agent = Agent();
        Func<string, string> verify = command => command == "check two" && !twoWorks ? Fail(command) : Pass(command);
        await Runner(agent, verify).RunPlanAsync(plan.Id, default);
        Assert.Equal(StepStatus.Parked, Store.Get(plan.Id)!.Steps[1].Status);

        // The user fixes the cause and retries step 2 alone.
        twoWorks = true;
        PlanRecord retried = Store.RetryStep(plan.Id, 2)!;
        Assert.Equal(StepStatus.Pending, retried.Steps[1].Status);
        Assert.Equal(0, retried.Steps[1].Attempts);
        Assert.Single(retried.Events!, e => e.Kind == RunEventKind.StepRetried && e.StepId == 2);
        Store.Approve(plan.Id, retryStopped: false, autoRetries: 0);
        await Runner(agent, verify).RunPlanAsync(plan.Id, default);

        PlanRecord done = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, done.Status);
        Assert.All(done.Steps, step => Assert.Equal(StepStatus.Done, step.Status));
        // Steps 1 and 3 were not run again.
        Assert.Equal(1, agent.Calls.Count(call => System.Text.RegularExpressions.Regex.IsMatch(call.Prompt, @"This step \(\d+ of \d+\): three\r?$", System.Text.RegularExpressions.RegexOptions.Multiline)));
    }

    [Fact]
    public void Approving_a_blocked_plan_again_tries_every_parked_step_unless_told_to_go_on_past_them()
    {
        PlanRecord plan = Graph(Node("one"), Node("two", 1), Node("three", 1), Node("four", 1, 2, 3));
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Blocked,
            Steps = current.Steps.Select(step => step.Id is 2 or 3 ? step with { Status = StepStatus.Parked, Attempts = 3 } : step).ToList()
        });

        PlanRecord goingOn = Store.Approve(plan.Id, retryStopped: false, autoRetries: 0)!;
        Assert.Equal([StepStatus.Pending, StepStatus.Parked, StepStatus.Parked, StepStatus.Pending], goingOn.Steps.Select(step => step.Status));

        Store.Update(plan.Id, current => current with { Status = PlanStatus.Blocked });
        PlanRecord all = Store.Approve(plan.Id, autoRetries: 0)!;
        Assert.Equal([StepStatus.Pending, StepStatus.Pending, StepStatus.Pending, StepStatus.Pending], all.Steps.Select(step => step.Status));
        Assert.Equal([0, 0, 0, 0], all.Steps.Select(step => step.Attempts));
    }

    [Fact]
    public void A_parked_step_of_a_running_plan_can_be_retried_or_skipped_but_a_failing_one_cannot()
    {
        PlanRecord plan = Graph(Node("one"), Node("two", 1), Node("three", 1), Node("four", 1, 2, 3));
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select(step => step.Id switch
            {
                2 => step with { Status = StepStatus.Parked },
                3 => step with { Status = StepStatus.Failed },
                _ => step
            }).ToList()
        });

        // Step 3 is between two rounds of its repair loop (the check marks it failed): leave it to the runner.
        Assert.Equal(StepStatus.Failed, Store.RetryStep(plan.Id, 3)!.Steps[2].Status);
        Assert.Equal(StepStatus.Failed, Store.SkipStep(plan.Id, 3)!.Steps[2].Status);

        Assert.Equal(StepStatus.Done, Store.SkipStep(plan.Id, 2)!.Steps[1].Status);
        Assert.Equal(PlanStatus.Running, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task Each_transition_notifies_exactly_once_and_a_notice_has_no_output_in_it()
    {
        PlanRecord plan = Graph(Node("one"), Node("two", 1), Node("three", 1), Node("four", 1, 2, 3));
        var notifier = new RecordingNotifier();
        bool twoWorks = false;
        Func<string, string> verify = command => command == "check two" && !twoWorks
            ? "Exit code: 1\n--- stdout ---\nFAILED secret::password_is_hunter2"
            : Pass(command);

        await Runner(Agent(), verify, notifier: notifier).RunPlanAsync(plan.Id, default);

        Assert.Equal(["step-parked", "plan-needs-attention"], notifier.Notices.Select(notice => notice.Kind));
        PlanNotice parked = notifier.Notices[0];
        Assert.Equal(2, parked.StepId);
        Assert.Equal("two", parked.StepTitle);
        Assert.Equal("check-kept-failing", parked.Cause);
        Assert.Equal((1, 1, 4), (parked.StepsDone, parked.StepsParked, parked.StepsTotal));
        string body = WebhookPlanNotifier.Body(parked);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("FAILED", body);

        // Retried and finished: one more notice, the plan is done; nothing is told twice.
        twoWorks = true;
        Store.RetryStep(plan.Id, 2);
        Store.Approve(plan.Id, retryStopped: false, autoRetries: 0);
        await Runner(Agent(), verify, notifier: notifier).RunPlanAsync(plan.Id, default);

        Assert.Equal(["step-parked", "plan-needs-attention", "plan-done"], notifier.Notices.Select(notice => notice.Kind));
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task The_run_deadline_is_told_once_and_is_a_stop_for_the_whole_plan_not_a_parked_step()
    {
        PlanRecord plan = Graph(Node("one"), Node("two", 1));
        var notifier = new RecordingNotifier();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Store.Update(plan.Id, current => current with { RunDeadlineUtc = now.AddMinutes(-1) });

        await Runner(Agent(), Pass, utcNow: () => now, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Failed, after.Steps[0].Status);
        Assert.Equal(["plan-deadline"], notifier.Notices.Select(notice => notice.Kind));
    }

    [Fact]
    public async Task A_step_whose_check_cannot_run_is_parked_with_that_reason_and_independent_work_goes_on()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "package.json"), """{"scripts":{"dev":"tsx watch src/index.ts","build":"tsc"}}""");
        PlanRecord plan = Store.Approve(Store.Create("Mixed", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
        [
            new PlanStepInput("serve", "start it", [], "npm run dev", "standard"),
            new PlanStepInput("build", "build it", [], "check build", "standard", DependsOn: [1]),
            new PlanStepInput("other", "unrelated", [], "check other", "standard", DependsOn: [1]),
            new PlanStepInput("final", "everything", [], "check final", "standard", DependsOn: [1, 2, 3])
        ]).Id, autoRetries: 0)!;
        // Step 2 is the one whose check never ends; steps 3 does not depend on it.
        Store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, 1, step => step with { Verify = "check one" }));
        Store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, 2, step => step with { Verify = "npm run dev" }));

        await Runner(Agent(), Pass).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal([StepStatus.Done, StepStatus.Parked, StepStatus.Done, StepStatus.Pending], after.Steps.Select(step => step.Status));
        Assert.Contains("does not exit on its own", after.Steps[1].Note);
        PlanRunEvent parked = Assert.Single(after.Events!, e => e.Kind == RunEventKind.StepParked);
        Assert.Equal("check", parked.FailureSignature);
    }
}

public sealed class PlanGraphTests : PlanTestBase
{
    private static PlanStepInput Input(string title, string? group = null, int[]? dependsOn = null) =>
        new(title, "detail", [], "dotnet build", "standard", group, DependsOn: dependsOn);

    private PlanRecord Plan(params PlanStepInput[] steps) => NewPlan(steps);

    [Fact]
    public void A_plan_that_says_nothing_is_a_chain_and_the_last_step_waits_for_all()
    {
        PlanRecord plan = Plan(Input("a"), Input("b"), Input("c"), Input("d"));
        Assert.Equal([[], [1], [2], [1, 2, 3]], plan.Steps.Select(step => (IReadOnlyList<int>)(step.DependsOn ?? [])).ToArray());
        Assert.Equal([1], PlanGraph.DependenciesOf(plan.Steps, plan.Steps[1]));
        Assert.Equal([1, 2, 3], PlanGraph.DependenciesOf(plan.Steps, plan.Steps[3]));
    }

    [Fact]
    public void The_members_of_a_parallel_group_share_what_came_before_the_group()
    {
        PlanRecord plan = Plan(Input("setup"), Input("x", "g"), Input("y", "g"), Input("z", "g"), Input("final"));
        IReadOnlyList<PlanStep> steps = plan.Steps;
        Assert.Equal([1], PlanGraph.DependenciesOf(steps, steps[1]));
        Assert.Equal([1], PlanGraph.DependenciesOf(steps, steps[2]));
        Assert.Equal([1], PlanGraph.DependenciesOf(steps, steps[3]));
        Assert.Equal([1, 2, 3, 4], PlanGraph.DependenciesOf(steps, steps[4]));
    }

    [Fact]
    public void Written_dependencies_are_kept_and_unknown_or_own_ids_are_dropped()
    {
        PlanRecord plan = Plan(Input("a"), Input("b"), Input("c", dependsOn: [1]), Input("d", dependsOn: [3, 3, 9, 4]));
        Assert.Equal([1], plan.Steps[2].DependsOn);
        Assert.Equal([1, 2, 3], plan.Steps[3].DependsOn);
    }

    [Fact]
    public void A_plan_saved_before_dependencies_existed_reads_as_the_chain_it_was()
    {
        PlanRecord plan = Plan(Input("a"), Input("b"), Input("c"));
        // The file as an older backend wrote it: no dependsOn on any step.
        string path = Path.Combine(PlansDirectory, plan.Id + ".json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var step in node["steps"]!.AsArray())
        {
            step!.AsObject().Remove("dependsOn");
        }

        File.WriteAllText(path, node.ToJsonString());
        Assert.DoesNotContain("dependsOn", File.ReadAllText(path));

        PlanRecord loaded = Store.Get(plan.Id)!;
        Assert.Equal([[], [1], [1, 2]], loaded.Steps.Select(step => (IReadOnlyList<int>)(step.DependsOn ?? [])).ToArray());
        Assert.Equal(StepStatus.Pending, loaded.Steps[0].Status);
    }

    [Fact]
    public void A_cycle_an_unknown_step_a_step_that_waits_for_itself_or_a_later_one_is_refused_when_proposed()
    {
        IReadOnlyList<string> unknown = PlanGraph.Problems([Input("a"), Input("b", dependsOn: [7])]);
        Assert.Contains("step 7, which is not in the plan", Assert.Single(unknown));

        IReadOnlyList<string> self = PlanGraph.Problems([Input("a", dependsOn: [1]), Input("b")]);
        Assert.Contains("depends on itself", Assert.Single(self));

        IReadOnlyList<string> cycle = PlanGraph.Problems([Input("a", dependsOn: [2]), Input("b", dependsOn: [1])]);
        Assert.Contains(cycle, problem => problem.Contains("a cycle of dependencies"));

        IReadOnlyList<string> later = PlanGraph.Problems([Input("a", dependsOn: [3]), Input("b"), Input("c")]);
        Assert.Contains("comes after it", Assert.Single(later));
        Assert.Contains("List the steps in an order", Assert.Single(later));

        IReadOnlyList<string> longer = PlanGraph.Problems([Input("a", dependsOn: [3]), Input("b"), Input("c", dependsOn: [1])]);
        Assert.Contains(longer, problem => problem.Contains("cycle"));

        IReadOnlyList<string> group = PlanGraph.Problems([Input("a"), Input("b", "g"), Input("c", "g", [2])]);
        Assert.Contains("cannot wait for each other", Assert.Single(group));

        Assert.Empty(PlanGraph.Problems([Input("a"), Input("b", dependsOn: [1]), Input("c", dependsOn: [1, 2])]));
        Assert.Empty(PlanGraph.Problems([Input("a"), Input("b"), Input("c")]));
    }

    [Fact]
    public void A_step_number_too_long_to_be_a_step_is_a_step_that_is_not_in_the_plan_and_never_a_crash()
    {
        Assert.Equal([1, 3], PlanGraph.ParseStepNumbers("Step 1, and step 3 (and 3)"));
        Assert.Equal([int.MaxValue], PlanGraph.ParseStepNumbers("99999999999"));
        Assert.Equal([2, int.MaxValue], PlanGraph.ParseStepNumbers("2 and 123456789012345678901234567890"));
        Assert.Empty(PlanGraph.ParseStepNumbers(null));

        IReadOnlyList<string> problems = PlanGraph.Problems([Input("a"), Input("b", dependsOn: PlanGraph.ParseStepNumbers("99999999999"))]);
        Assert.Contains("which is not in the plan", Assert.Single(problems));

        JsonElement steps = JsonSerializer.SerializeToElement(new object[]
        {
            new { title = "a", detail = "d", verify = "npm test" },
            new { title = "b", detail = "d", verify = "npm test", dependsOn = new[] { "step 99999999999" } }
        });
        PlanTools tools = new(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (_, _, _) => Task.FromResult("Exit code: 0"));
        IReadOnlyList<string> review = tools.Review(@"C:\p", steps);
        Assert.Contains(review, problem => problem.Contains("which is not in the plan"));
    }

    [Fact]
    public void A_proposed_plan_with_a_bad_graph_is_sent_back_with_the_problems_in_the_review()
    {
        string root = Path.Combine(Path.GetTempPath(), $"fleet-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), """{"scripts":{"test":"node --test"}}""");
            IReadOnlyList<PlanStepInput> steps =
            [
                new PlanStepInput("a", "d", [], "node --version", "standard", DependsOn: [2]),
                new PlanStepInput("b", "d", [], "node --version", "standard", DependsOn: [1]),
                new PlanStepInput("c", "d", [], "npm test", "standard")
            ];
            IReadOnlyList<string> problems = PlanReview.Problems(root, steps, program => true);
            Assert.Contains(problems, problem => problem.Contains("a cycle of dependencies"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_planner_s_dependsOn_is_read_from_numbers_text_and_step_names_in_the_proposal()
    {
        JsonElement steps = JsonSerializer.SerializeToElement(new object[]
        {
            new { title = "a", detail = "d", verify = "npm test" },
            new { title = "b", detail = "d", verify = "npm test", dependsOn = new[] { 1 } },
            new { title = "c", detail = "d", verify = "npm test", depends_on = "1, 2" },
            new { title = "d", detail = "d", verify = "npm test", dependencies = new[] { "Step 1", "step 3" } },
            new { title = "e", detail = "d", verify = "npm test", dependsOn = Array.Empty<int>() }
        });
        PlanTools tools = new(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (_, _, _) => Task.FromResult("Exit code: 0"));
        IReadOnlyList<string> problems = tools.Review(@"C:\p", steps);
        Assert.DoesNotContain(problems, problem => problem.Contains("cycle") || problem.Contains("not in the plan"));
    }

    [Fact]
    public async Task The_proposal_tool_refuses_a_cycle_and_saves_a_graph_with_its_dependencies()
    {
        string root = Path.Combine(PlansDirectory, "project");
        Directory.CreateDirectory(root);
        PlanTools tools = new(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)),
            (_, _, _) => Task.FromResult("Exit code: 0"), workerWorkspacesEnabled: true);
        PlanStepInput Part(string title, params int[] dependsOn) =>
            new(title, "do " + title, [], "npm test", "standard", DependsOn: dependsOn.Length > 0 ? dependsOn : null);
        JsonElement Steps(params PlanStepInput[] steps) => JsonSerializer.SerializeToElement(steps);

        string refused = await tools.ProposePlanAsync("Cycle", "G", root, null, null, null, null,
            Steps(Part("a", 2), Part("b", 1), Part("c")), default);
        Assert.Contains("NOT saved", refused);
        Assert.Contains("a cycle of dependencies", refused);
        Assert.Empty(Store.List());

        string unknown = await tools.ProposePlanAsync("Unknown", "G", root, null, null, null, null,
            Steps(Part("a"), Part("b", 9), Part("c")), default);
        Assert.Contains("step 9, which is not in the plan", unknown);
        Assert.Empty(Store.List());

        string saved = await tools.ProposePlanAsync("Graph", "G", root, null, null, null, null,
            Steps(Part("a"), Part("b", 1), Part("c", 1), Part("d")), default);
        Assert.StartsWith("Plan saved", saved);
        PlanRecord plan = Store.Get(Assert.Single(Store.List()).Id)!;
        Assert.Equal([[], [1], [1], [1, 2, 3]], plan.Steps.Select(step => (IReadOnlyList<int>)(step.DependsOn ?? [])).ToArray());
    }
}
