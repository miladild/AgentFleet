using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class PlanContextTests : ContextTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private FleetPlanStore Plans { get; set; }

    private readonly string _project;

    public PlanContextTests()
    {
        Plans = new FleetPlanStore(Configuration);
        _project = Path.Combine(Root, "project");
        Directory.CreateDirectory(_project);
    }

    private string File1 => Path.Combine(_project, "a.txt");

    private PlanRunner Runner(FakeStepAgent agent, FleetPlanStore? plans = null, TimeSpan? timeout = null)
    {
        plans ??= Plans;
        var tools = new PlanTools(plans, Valid, (command, _, _) => Task.FromResult("Exit code: 0\n--- stdout ---\nok"), Journal);
        return new PlanRunner(
            plans, tools, agent, NullLogger.Instance, timeout,
            recorder: new PlanContextRecorder(Store, plans, NullLogger.Instance), journal: Journal);
    }

    private PlanRecord Approved(FleetPlanStore plans, params PlanStepInput[] steps)
    {
        PlanRecord plan = plans.Create("Rate limiting", "Limit login attempts", _project, ["requests are keyed by address"], [], ["shared NAT"], null, null, steps);
        return plans.Approve(plan.Id)!;
    }

    private static PlanStepInput Step(string title, string file, string? tier = "standard") =>
        new(title, "detail of " + title, [file], "check", tier);

    [Fact]
    public async Task Step_two_receives_what_step_one_decided_handed_over_and_produced()
    {
        PlanRecord plan = Approved(Plans, Step("Write the limiter", "a.txt"), Step("Write the tests", "b.txt"));
        string? injectedForStepTwo = null;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("(1 of 2)"))
            {
                File.WriteAllText(File1, "class Limiter {}");
                Journal.RecordDecision("Use a token bucket, refilled once a second", "simplest correct option");
            }
            else
            {
                injectedForStepTwo = Journal.BuildInjection("worker1");
                File.WriteAllText(Path.Combine(_project, "b.txt"), "tests");
            }

            return Task.FromResult("Done.\n\n_— via worker1_");
        });

        await Runner(agent).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Plans.Get(plan.Id)!.Status);
        Assert.NotNull(injectedForStepTwo);
        Assert.Contains("token bucket", injectedForStepTwo);
        Assert.Contains("Handoff from step 1 on worker1", injectedForStepTwo);
        Assert.Contains("Do step 2, Write the tests", injectedForStepTwo);
        Assert.Contains(File1, injectedForStepTwo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unchanged", injectedForStepTwo);
        Assert.Contains("requests are keyed by address", injectedForStepTwo);

        string contextId = Plans.Get(plan.Id)!.ContextId!;
        Assert.True(FleetRequestContext.IsValidContextId(contextId));
        Assert.Equal(2, Store.ListHandoffs(contextId, 10).Count);
        Assert.Equal(2, Store.EventsOfKinds(contextId, [FleetContextEventKind.Verification], 10).Count);
        Assert.Equal(2, Store.ListArtifacts(contextId).Count);
        Assert.All(Store.ListTasks(contextId).Where(t => t.Kind == "step"), t => Assert.Equal(StepStatus.Done, t.Status));
        Assert.Equal(PlanStatus.Done, Store.ListTasks(contextId).Single(t => t.Kind == "plan").Status);
        Assert.NotNull(Store.LatestCheckpoint(contextId));
    }

    [Fact]
    public async Task A_file_changed_outside_the_plan_between_steps_is_flagged_to_the_next_agent_and_noted()
    {
        PlanRecord plan = Approved(Plans, Step("Create a", "a.txt"), Step("Unrelated work", "b.txt"), Step("Use a", "c.txt"));
        string? injectedForStepThree = null;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("(1 of 3)"))
            {
                File.WriteAllText(File1, "original");
            }
            else if (prompt.Contains("(2 of 3)"))
            {
                File.WriteAllText(File1, "edited behind the record's back");
                File.WriteAllText(Path.Combine(_project, "b.txt"), "step two's own file");
            }
            else
            {
                injectedForStepThree = Journal.BuildInjection("hub");
                File.WriteAllText(Path.Combine(_project, "c.txt"), "step three's own file");
            }

            return Task.FromResult("ok");
        });

        await Runner(agent).RunPlanAsync(plan.Id, default);

        Assert.Contains("CHANGED since it was recorded", injectedForStepThree);
        string contextId = Plans.Get(plan.Id)!.ContextId!;
        FleetContextEvent note = Assert.Single(Store.EventsOfKinds(contextId, [FleetContextEventKind.ArtifactDiverged], 10), e => ContextText.Bool(e.Payload, "exists") == true);
        Assert.NotEqual(ContextText.String(note.Payload, "recordedSha256"), ContextText.String(note.Payload, "currentSha256"));
    }

    [Fact]
    public async Task A_file_that_vanishes_between_steps_is_reported_missing()
    {
        PlanRecord plan = Approved(Plans, Step("Create a", "a.txt"), Step("Remove a", "b.txt"), Step("Use a", "c.txt"));
        string? injectedForStepThree = null;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("(1 of 3)"))
            {
                File.WriteAllText(File1, "original");
            }
            else if (prompt.Contains("(2 of 3)"))
            {
                File.Delete(File1);
                File.WriteAllText(Path.Combine(_project, "b.txt"), "step two's own file");
            }
            else
            {
                injectedForStepThree = Journal.BuildInjection("hub");
                File.WriteAllText(Path.Combine(_project, "c.txt"), "step three's own file");
            }

            return Task.FromResult("ok");
        });

        await Runner(agent).RunPlanAsync(plan.Id, default);

        Assert.Contains("MISSING now", injectedForStepThree);
        Assert.Contains(Store.VerifyArtifacts(Plans.Get(plan.Id)!.ContextId!), v => !v.Exists);
    }

    [Fact]
    public async Task A_plan_proposed_in_a_chat_runs_inside_that_chats_context_with_its_decisions()
    {
        string chat = NewContextId();
        var planTools = new PlanTools(Plans, Valid, (_, _, _) => Task.FromResult("Exit code: 0"), Journal);
        JsonElement steps = JsonSerializer.SerializeToElement(new[]
        {
            new { title = "Only step", detail = "do it", files = new[] { "a.txt" }, verify = "dotnet build", tier = "standard" }
        });
        using (Scope(chat))
        {
            Journal.RecordDecision("Do not touch the public API", "another team depends on it", "constraint");
            await planTools.ProposePlanAsync("Add a limiter", "Limit logins", _project, null, null, null, null, steps, default);
        }

        PlanRecord plan = Plans.List().Select(s => Plans.Get(s.Id)!).Single();
        Assert.Equal(chat, plan.ContextId);
        Plans.Approve(plan.Id);
        string? injected = null;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            injected = Journal.BuildInjection("hub");
            File.WriteAllText(File1, "created");
            return Task.FromResult("ok");
        });

        await Runner(agent).RunPlanAsync(plan.Id, default);

        Assert.Contains("Do not touch the public API", injected);
        Assert.Contains(Store.ListTasks(chat), t => t.Kind == "plan");
    }

    [Fact]
    public async Task A_chat_keeps_its_own_title_when_a_plan_runs_in_it_and_a_plans_own_context_is_named_after_it()
    {
        string chat = NewContextId();
        Store.UpsertMessages(chat, "My rate limiter chat", Messages(("m1", "user", "add a limiter")));
        PlanRecord inChat = Approved(Plans, Step("Only step", "a.txt"));
        Plans.Update(inChat.Id, p => p with { ContextId = chat });
        PlanRecord alone = Approved(Plans, Step("Other step", "b.txt"));

        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            File.WriteAllText(Path.Combine(_project, prompt.Contains("Other step") ? "b.txt" : "a.txt"), "x");
            return Task.FromResult("ok");
        });
        await Runner(agent).RunPlanAsync(inChat.Id, default);
        await Runner(agent).RunPlanAsync(alone.Id, default);

        Assert.Equal("My rate limiter chat", Store.GetContext(chat)!.Title);
        Assert.StartsWith("Plan: ", Store.GetContext(Plans.Get(alone.Id)!.ContextId!)!.Title);
    }

    [Fact]
    public async Task After_a_backend_restart_the_plan_resumes_in_the_same_context_with_the_handoff_intact()
    {
        PlanRecord plan = Approved(Plans, Step("Step one", "a.txt"), Step("Step two", "b.txt"));
        using var shutdown = new CancellationTokenSource();
        var firstRun = new FakeStepAgent((prompt, _, _) =>
        {
            if (prompt.Contains("(1 of 2)"))
            {
                File.WriteAllText(File1, "one");
                Journal.RecordDecision("Keep everything in one module", null);
                return Task.FromResult("ok");
            }

            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(firstRun).RunPlanAsync(plan.Id, shutdown.Token));
        string contextId = Plans.Get(plan.Id)!.ContextId!;
        Assert.Equal(PlanStatus.Running, Plans.Get(plan.Id)!.Status);

        // The backend starts again: nothing but the files on disk survives.
        SqliteConnection.ClearAllPools();
        Restart();
        Plans = new FleetPlanStore(Configuration);
        string? injected = null;
        var secondRun = new FakeStepAgent((_, _, _) =>
        {
            injected = Journal.BuildInjection("hub");
            File.WriteAllText(Path.Combine(_project, "b.txt"), "step two");
            return Task.FromResult("ok");
        });

        await Runner(secondRun, Plans).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Plans.Get(plan.Id)!.Status);
        Assert.Equal(contextId, Plans.Get(plan.Id)!.ContextId);
        Assert.Contains("Keep everything in one module", injected);
        Assert.Contains("Handoff from step 1", injected);
        Assert.Contains(Store.EventsOfKinds(contextId, [FleetContextEventKind.PlanTransition], 50),
            e => ContextText.String(e.Payload, "note")!.Contains("resumed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deleting_a_chat_a_plan_runs_in_only_hides_it_and_keeps_the_record()
    {
        string chat = NewContextId();
        Store.UpsertMessages(chat, "Chat with a plan", Messages(("m1", "user", "add a limiter")));
        PlanRecord plan = Approved(Plans, Step("Only step", "a.txt"));
        Plans.Update(plan.Id, p => p with { ContextId = chat });
        Store.UpsertTask(chat, FleetPlanContext.PlanTaskId(plan.Id), "plan", PlanStatus.Approved, plan.Title);

        Assert.True(Plans.UsesContext(chat));
        Assert.False(Plans.UsesContext(NewContextId()));

        Assert.True(Store.ClearMessages(chat));

        Assert.DoesNotContain(Store.ListSessions(), s => s.Id == chat);
        Assert.NotNull(Store.GetContext(chat));
        Assert.Single(Store.ListTasks(chat));
        Assert.NotEmpty(Store.AllEvents(chat));
    }

    [Fact]
    public async Task Every_model_call_of_a_step_is_recorded_under_that_steps_task()
    {
        PlanRecord plan = Approved(Plans, Step("Only step", "a.txt"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            Journal.RecordRoute("worker1", "standard", "Off", "plan step");
            Journal.RecordToolExecution("c1", "write_file", "{\"path\":\"a.txt\"}", "written", false);
            return Task.FromResult("ok");
        });

        await Runner(agent).RunPlanAsync(plan.Id, default);

        string contextId = Plans.Get(plan.Id)!.ContextId!;
        string stepTask = FleetPlanContext.StepTaskId(plan.Id, 1);
        Assert.Contains(Store.AllEvents(contextId), e => e.Kind == FleetContextEventKind.Route && e.TaskId == stepTask);
        Assert.Contains(Store.AllEvents(contextId), e => e.Kind == FleetContextEventKind.ToolCall && e.TaskId == stepTask);
    }

    [Fact]
    public async Task A_step_that_passes_its_check_but_never_created_its_file_is_not_done_and_is_retried()
    {
        PlanRecord plan = Approved(Plans, Step("Write the tests", "a.txt"));
        int calls = 0;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            // The first two attempts claim success without creating anything (a check like `node --test` passes with no tests).
            if (++calls == 3)
            {
                File.WriteAllText(File1, "finally written");
            }

            return Task.FromResult("Done, all good.");
        });

        await Runner(agent).RunPlanAsync(plan.Id, default);

        PlanRecord after = Plans.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(3, agent.Calls.Count);
        Assert.Contains("do not exist: a.txt", agent.Calls[1].Prompt);
    }

    [Fact]
    public async Task A_step_that_never_creates_its_file_blocks_the_plan_and_says_which_file()
    {
        PlanRecord plan = Approved(Plans, Step("Write the tests", "a.txt"));

        await Runner(new FakeStepAgent((_, _, _) => Task.FromResult("Done."))).RunPlanAsync(plan.Id, default);

        PlanRecord after = Plans.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Contains("a.txt", Store.EventsOfKinds(after.ContextId!, [FleetContextEventKind.Verification], 10).Last().Payload.GetProperty("output").GetString());
    }

    [Fact]
    public async Task A_failing_step_is_told_about_scratch_files_it_left_behind_that_the_plan_does_not_name()
    {
        PlanRecord plan = Approved(Plans, Step("Write the tests", "a.txt"));
        int checks = 0;
        var tools = new PlanTools(Plans, Valid, (command, _, _) =>
            Task.FromResult(command == "git status --short" || ++checks > 1 ? "Exit code: 0\n--- stdout ---\nok" : "Exit code: 1\n--- stdout ---\nunexpected file found"), Journal);
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(File1, "the real file");
            File.WriteAllText(Path.Combine(_project, "scratch-experiment.txt"), "left over");
            return Task.FromResult("Done.");
        });
        var runner = new PlanRunner(
            Plans, tools, agent, NullLogger.Instance,
            recorder: new PlanContextRecorder(Store, Plans, NullLogger.Instance), journal: Journal);

        await runner.RunPlanAsync(plan.Id, default);

        Assert.Equal(2, agent.Calls.Count);
        Assert.Contains("scratch-experiment.txt", agent.Calls[1].Prompt);
        Assert.Contains("does not name", agent.Calls[1].Prompt);
        PlanRecord after = Plans.Get(plan.Id)!;
        Assert.Contains(after.Events!, e => e.Kind == RunEventKind.CheckFailed && e.Detail.Contains("scratch-experiment.txt"));
        Assert.Contains(after.Events!, e => e.Kind == RunEventKind.StepDone && e.Detail.Contains("scratch-experiment.txt"));
        Assert.True(File.Exists(Path.Combine(_project, "scratch-experiment.txt")), "the fleet reports leftovers, it never deletes them");
    }

    [Fact]
    public async Task A_file_named_by_any_step_of_the_plan_is_not_a_scratch_file()
    {
        PlanRecord plan = Approved(Plans, Step("Write a", "a.txt"), Step("Write b", "b.txt"));
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            // Step one also writes step two's file early: named by the plan, so not a stray.
            File.WriteAllText(File1, "a");
            File.WriteAllText(Path.Combine(_project, "b.txt"), "b");
            return Task.FromResult("ok");
        });

        await Runner(agent).RunPlanAsync(plan.Id, default);

        Assert.DoesNotContain(Plans.Get(plan.Id)!.Events!, e => e.Kind == RunEventKind.StepDone && e.Detail.Contains("does not name"));
    }

    [Fact]
    public async Task A_step_that_names_a_folder_passes_once_the_folder_exists_and_its_files_are_not_scratch_files()
    {
        // Measured on a real fleet: a step naming "test/" was failed three times after its check passed, because the
        // folder was looked for as a file, and the plan blocked overnight.
        PlanRecord plan = Approved(Plans, Step("Write the tests", "test/"));
        var agent = new FakeStepAgent((_, _, _) =>
        {
            Directory.CreateDirectory(Path.Combine(_project, "test"));
            File.WriteAllText(Path.Combine(_project, "test", "slug.test.js"), "test");
            return Task.FromResult("Done.");
        });

        await Runner(agent).RunPlanAsync(plan.Id, default);

        PlanRecord after = Plans.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Single(agent.Calls);
        Assert.DoesNotContain(after.Events!, e => e.Kind == RunEventKind.StepDone && e.Detail.Contains("does not name"));
    }

    [Fact]
    public async Task A_file_that_already_existed_is_not_required_to_be_created_so_a_removal_step_can_pass()
    {
        File.WriteAllText(File1, "old");
        PlanRecord plan = Approved(Plans, Step("Remove the old file", "a.txt"));

        await Runner(new FakeStepAgent((_, _, _) =>
        {
            File.Delete(File1);
            return Task.FromResult("Removed.");
        })).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Done, Plans.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_blocked_plan_leaves_the_error_and_a_checkpoint_in_the_record()
    {
        PlanRecord plan = Approved(Plans, Step("Never passes", "a.txt"));
        var tools = new PlanTools(Plans, Valid, (_, _, _) => Task.FromResult("Exit code: 1\n--- stdout ---\nboom"), Journal);
        var runner = new PlanRunner(
            Plans, tools, new FakeStepAgent((_, _, _) => Task.FromResult("ok")), NullLogger.Instance,
            recorder: new PlanContextRecorder(Store, Plans, NullLogger.Instance), journal: Journal);

        await runner.RunPlanAsync(plan.Id, default);

        string contextId = Plans.Get(plan.Id)!.ContextId!;
        Assert.Equal(PlanStatus.Blocked, Plans.Get(plan.Id)!.Status);
        Assert.Contains(Store.EventsOfKinds(contextId, [FleetContextEventKind.Error], 10), e => ContextText.String(e.Payload, "message")!.Contains("did not pass"));
        Assert.Equal(3, Store.EventsOfKinds(contextId, [FleetContextEventKind.Verification], 10).Count);
        Assert.Equal("plan-blocked", Store.LatestCheckpoint(contextId)!.Kind);
    }
}
