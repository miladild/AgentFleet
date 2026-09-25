using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

internal sealed class FakeStepAgent(Func<string, string, CancellationToken, Task<string>> run) : IStepAgent
{
    public List<(string Tier, string Prompt)> Calls { get; } = [];

    public Task<string> RunStepAsync(string prompt, string tier, CancellationToken cancellationToken)
    {
        Calls.Add((tier, prompt));
        return run(prompt, tier, cancellationToken);
    }
}

public sealed class PlanRunnerTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private PlanRunner Runner(FakeStepAgent agent, Func<string, string> verify, TimeSpan? timeout = null, ISleepGuard? sleepGuard = null) =>
        new(Store, new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(verify(command))), agent, NullLogger.Instance, timeout,
            transientDelay: _ => TimeSpan.Zero, sleepGuard: sleepGuard);

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private static string Fail(string _) => "Exit code: 1\n--- stdout ---\nerror CS1002: ; expected";

    private PlanRecord ApprovedPlan(params PlanStepInput[] steps) => Store.Approve(NewPlan(steps).Id)!;

    private static FakeStepAgent Agent(string reply = "Done.") => new((_, _, _) => Task.FromResult(reply));

    [Fact]
    public async Task Steps_run_in_order_each_on_its_own_tier_and_the_plan_finishes()
    {
        PlanRecord plan = ApprovedPlan(Step("first", tier: "light"), Step("second", tier: "heavy"));
        FakeStepAgent agent = Agent("Did the thing.\n\n_— via worker1_");

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        Assert.Equal(["light", "heavy"], agent.Calls.Select(call => call.Tier));
        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.All(after.Steps, step => Assert.Equal(StepStatus.Done, step.Status));
        Assert.All(after.Steps, step => Assert.Equal("Did the thing.", step.Note));
    }

    [Fact]
    public async Task Each_step_prompt_carries_only_that_step_the_marker_and_how_it_will_be_checked()
    {
        PlanRecord plan = ApprovedPlan(Step("alpha", verify: "dotnet build"), Step("beta", verify: "dotnet test"));
        FakeStepAgent agent = Agent("ok");

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        string first = agent.Calls[0].Prompt;
        Assert.Contains("(1 of 2): alpha", first);
        Assert.Contains("dotnet build", first);
        Assert.Contains($"[plan:{plan.Id}]", first);
        Assert.DoesNotContain("beta", first);

        string second = agent.Calls[1].Prompt;
        Assert.Contains("(2 of 2): beta", second);
        Assert.Contains("Already done in earlier steps", second);
        Assert.Contains("1. alpha: ok", second);
    }

    [Fact]
    public async Task A_failing_check_is_retried_with_the_real_output_and_the_last_attempt_moves_to_the_heavy_tier()
    {
        PlanRecord plan = ApprovedPlan(Step("build it", tier: "standard"));
        int verifications = 0;
        FakeStepAgent agent = Agent();

        await Runner(agent, command => ++verifications < 3 ? Fail(command) : Pass(command)).RunPlanAsync(plan.Id, default);

        Assert.Equal(["standard", "standard", "heavy"], agent.Calls.Select(call => call.Tier));
        Assert.DoesNotContain("did not pass", agent.Calls[0].Prompt);
        Assert.Contains("did not pass", agent.Calls[1].Prompt);
        Assert.Contains("CS1002", agent.Calls[1].Prompt);
        Assert.Contains("CS1002", agent.Calls[2].Prompt);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_step_that_never_passes_blocks_the_plan_and_leaves_later_steps_untouched()
    {
        PlanRecord plan = ApprovedPlan(Step("hard one"), Step("never reached"));
        FakeStepAgent agent = Agent();

        await Runner(agent, Fail).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanRunner.MaxAttemptsPerStep, agent.Calls.Count);
        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Failed, after.Steps[0].Status);
        Assert.Contains("attempts", after.Steps[0].Note);
        Assert.Equal(StepStatus.Pending, after.Steps[1].Status);
    }

    [Fact]
    public async Task A_model_error_counts_as_an_attempt_and_is_retried_without_running_the_check()
    {
        PlanRecord plan = ApprovedPlan(Step("flaky"));
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) => ++calls == 1 ? throw new InvalidOperationException("node went away") : Task.FromResult("fine"));
        int verifications = 0;

        // The end-of-run "what changed" lookup is a command too, so count only the step's own check.
        await Runner(agent, command => { if (command != "git status --short") verifications++; return Pass(command); }).RunPlanAsync(plan.Id, default);

        Assert.Equal(2, agent.Calls.Count);
        Assert.Equal(1, verifications);
        Assert.Contains("node went away", agent.Calls[1].Prompt);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task When_no_machine_answers_the_step_waits_without_spending_its_attempts()
    {
        PlanRecord plan = ApprovedPlan(Step("during a reboot", tier: "standard"));
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) => ++calls <= 5
            ? throw new HttpRequestException("No connection could be made because the target machine actively refused it.")
            : Task.FromResult("fine"));

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        // Five outages and then the first real attempt, still on the step's own tier: nothing was escalated.
        Assert.Equal(6, agent.Calls.Count);
        Assert.All(agent.Calls, call => Assert.Equal("standard", call.Tier));
        Assert.Equal(5, after.Events!.Count(e => e.Kind == RunEventKind.Waiting));
        Assert.Contains("does not count as an attempt", after.Events!.First(e => e.Kind == RunEventKind.Waiting).Detail);
    }

    [Fact]
    public async Task A_long_outage_does_eventually_use_the_attempts_and_block_the_plan()
    {
        PlanRecord plan = ApprovedPlan(Step("machines gone"));
        var agent = new FakeStepAgent((_, _, _) => throw new HttpRequestException("unreachable"));

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        Assert.Equal(PlanStatus.Blocked, Store.Get(plan.Id)!.Status);
        Assert.Equal(PlanRunner.MaxTransientWaits + PlanRunner.MaxAttemptsPerStep, agent.Calls.Count);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), true)]
    [InlineData(typeof(System.Net.Sockets.SocketException), true)]
    [InlineData(typeof(TimeoutException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(ArgumentException), false)]
    public void Outages_are_told_apart_from_other_failures(Type type, bool transient) =>
        Assert.Equal(transient, PlanRunner.IsTransient((Exception)Activator.CreateInstance(type)!));

    [Fact]
    public void An_outage_wrapped_in_another_exception_is_still_an_outage() =>
        Assert.True(PlanRunner.IsTransient(new InvalidOperationException("wrapper", new HttpRequestException("refused"))));

    private sealed class CountingSleepGuard : ISleepGuard
    {
        public int Held { get; private set; }

        public int Released { get; private set; }

        public IDisposable Hold(string reason)
        {
            Held++;
            return new Release(this);
        }

        private sealed class Release(CountingSleepGuard owner) : IDisposable
        {
            public void Dispose() => owner.Released++;
        }
    }

    [Fact]
    public async Task The_computer_is_kept_awake_while_a_plan_runs_and_released_after()
    {
        PlanRecord plan = ApprovedPlan(Step("one"), Step("two"));
        var guard = new CountingSleepGuard();

        await Runner(Agent(), Pass, sleepGuard: guard).RunPlanAsync(plan.Id, default);

        Assert.Equal(1, guard.Held);
        Assert.Equal(1, guard.Released);
    }
    [Fact]
    public async Task A_step_that_times_out_is_still_checked_because_the_work_may_be_done()
    {
        PlanRecord plan = ApprovedPlan(Step("slow"));
        var agent = new FakeStepAgent(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return string.Empty; });

        await Runner(agent, Pass, TimeSpan.FromMilliseconds(150)).RunPlanAsync(plan.Id, default);

        Assert.Single(agent.Calls);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task A_resumed_plan_skips_finished_steps_and_retries_ones_left_running_or_failed()
    {
        PlanRecord plan = ApprovedPlan(Step("one"), Step("two"), Step("three"));
        Store.Update(plan.Id, p => p with
        {
            Status = PlanStatus.Running,
            Steps = p.Steps.Select(s => s.Id switch
            {
                1 => s with { Status = StepStatus.Done, Note = "was done" },
                2 => s with { Status = StepStatus.Running },
                _ => s with { Status = StepStatus.Failed }
            }).ToList()
        });
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        Assert.Equal(2, agent.Calls.Count);
        Assert.Contains("(2 of 3): two", agent.Calls[0].Prompt);
        Assert.Contains("was done", agent.Calls[0].Prompt);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Theory]
    [InlineData(PlanStatus.AwaitingApproval)]
    [InlineData(PlanStatus.Rejected)]
    [InlineData(PlanStatus.Done)]
    [InlineData(PlanStatus.Blocked)]
    public async Task A_plan_that_is_not_approved_or_running_is_left_alone(string status)
    {
        PlanRecord plan = NewPlan(Step("x"));
        Store.Update(plan.Id, p => p with { Status = status });
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        Assert.Empty(agent.Calls);
        Assert.Equal(status, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task Stopping_a_running_plan_cancels_the_step_and_blocks_it_with_a_note()
    {
        PlanRecord plan = ApprovedPlan(Step("long running"), Step("later"));
        var started = new TaskCompletionSource();
        var agent = new FakeStepAgent(async (_, _, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return string.Empty; });
        PlanRunner runner = Runner(agent, Pass);

        Task run = runner.RunPlanAsync(plan.Id, default);
        await started.Task;
        Assert.True(runner.Stop(plan.Id));
        await run;

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Pending, after.Steps[0].Status);
        Assert.Equal("Stopped by the user.", after.Steps[0].Note);
        Assert.Equal(StepStatus.Pending, after.Steps[1].Status);
    }

    [Fact]
    public void Stopping_a_queued_plan_blocks_it_and_stopping_a_plan_that_is_not_running_is_refused()
    {
        PlanRecord queued = ApprovedPlan(Step("x"));
        PlanRecord waiting = NewPlan(Step("y"));
        PlanRunner runner = Runner(Agent(), Pass);

        Assert.True(runner.Stop(queued.Id));
        Assert.Equal(PlanStatus.Blocked, Store.Get(queued.Id)!.Status);
        Assert.False(runner.Stop(waiting.Id));
        Assert.False(runner.Stop(new string('0', 32)));
    }

    [Fact]
    public void The_closing_sentence_becomes_the_note_without_the_via_footer_on_one_line_and_capped()
    {
        Assert.Equal("Made it.", PlanRunner.Summarize("Made it.\n\n_— via hub_"));
        Assert.Equal("a b", PlanRunner.Summarize("a\nb"));
        Assert.Equal(string.Empty, PlanRunner.Summarize(string.Empty));
        string capped = PlanRunner.Summarize(new string('x', 500));
        Assert.True(capped.Length <= 303);
        Assert.EndsWith("...", capped);
    }

    [Fact]
    public async Task Approving_a_plan_raises_the_event_the_runner_listens_for_exactly_once()
    {
        PlanRecord plan = NewPlan(Step("x"));
        var approvals = new List<string>();
        Store.Approved += approvals.Add;

        Store.Approve(plan.Id);
        Store.Approve(plan.Id);

        Assert.Equal([plan.Id], approvals);
        await Task.CompletedTask;
    }
}
