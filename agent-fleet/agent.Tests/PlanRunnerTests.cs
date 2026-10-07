using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>One message sent to a fake conversation: which one, which message of it, and where it went.</summary>
internal sealed record FakeStepCall(int Session, int Message, string Tier, string Text, string? Machine);

/// <summary>
/// A stand-in for the model. By default a call reports that the model used a tool and edited a file, as a model that
/// works does; a test that needs a model that only reads, or edits on some machines only, sets <see cref="ToolsOf"/>.
/// Every conversation it opens is kept in <see cref="Sessions"/>, with the messages that went into it.
/// </summary>
internal sealed class FakeStepAgent(
    Func<string, string, CancellationToken, Task<string>> run,
    int toolCalls = 1,
    bool editToolCalled = true) : IStepAgent
{
    private readonly object _gate = new();

    public List<(string Tier, string Prompt, string? Machine)> Calls { get; } = [];

    public List<FakeStepSession> Sessions { get; } = [];

    /// <summary>What a call reports about the model's tool use: tool calls made, and whether one edited a file.</summary>
    public Func<FakeStepCall, (int ToolCalls, bool EditToolCalled)>? ToolsOf { get; init; }

    /// <summary>The tools a step's model is seen calling in a reply (report_blocker).</summary>
    public Func<FakeStepCall, IReadOnlyList<AgentToolCall>>? CallsOf { get; init; }

    /// <summary>
    /// How the auditor of a check answers (a conversation opened with the check-audit role). By default it answers in words
    /// and proposes nothing, which is what a model does when it finds the check sound.
    /// </summary>
    public Func<FakeAuditCall, StepAgentReply>? Auditor { get; init; }

    /// <summary>Every message that went to an auditor, oldest first.</summary>
    public List<FakeAuditCall> Audits { get; } = [];

    public IStepSession OpenSession(string? role) => role is null ? OpenSession() : new FakeAuditSession(this, role);

    internal StepAgentReply Audit(FakeAuditCall call)
    {
        lock (_gate)
        {
            Audits.Add(call);
        }

        return Auditor?.Invoke(call) ?? new StepAgentReply("The check is sound.", 0, false);
    }

    internal static StepAgentReply Proposes(string check, string why = "the check could not run") =>
        new(string.Empty, 1, false, [new AgentToolCall("propose_check", new Dictionary<string, string?> { ["check"] = check, ["why"] = why })]);

    public Task<StepAgentReply> RunStepAsync(string prompt, string tier, CancellationToken cancellationToken)
        => RunStepAsync(prompt, tier, null, cancellationToken);

    public Task<StepAgentReply> RunStepAsync(string prompt, string tier, string? machine, CancellationToken cancellationToken)
        => OpenSession().SendAsync(prompt, tier, machine, cancellationToken);

    public IStepSession OpenSession()
    {
        lock (_gate)
        {
            var session = new FakeStepSession(this, Sessions.Count);
            Sessions.Add(session);
            return session;
        }
    }

    internal async Task<StepAgentReply> SendAsync(FakeStepSession session, string message, string tier, string? machine, CancellationToken cancellationToken)
    {
        FakeStepCall call;
        lock (_gate)
        {
            call = new FakeStepCall(session.Index, session.Messages.Count, tier, message, machine);
            session.Messages.Add(call);
            Calls.Add((tier, message, machine));
        }

        string text = await run(message, tier, cancellationToken);
        (int tools, bool edited) = ToolsOf?.Invoke(call) ?? (toolCalls, editToolCalled);
        return new StepAgentReply(text, tools, edited, CallsOf?.Invoke(call));
    }
}

/// <summary>One message to a check auditor: the role of its conversation, the model it was sent to and its text.</summary>
internal sealed record FakeAuditCall(string Role, int Number, string Tier, string Message, string? Machine);

internal sealed class FakeAuditSession(FakeStepAgent owner, string role) : IStepSession
{
    private int _messages;

    public bool HasHistory { get; private set; }

    public Task<StepAgentReply> SendAsync(string message, string tier, string? machine, CancellationToken cancellationToken)
    {
        StepAgentReply reply = owner.Audit(new FakeAuditCall(role, ++_messages, tier, message, machine));
        HasHistory = true;
        return Task.FromResult(reply);
    }
}

internal sealed class FakeStepSession(FakeStepAgent owner, int index) : IStepSession
{
    public int Index => index;

    public List<FakeStepCall> Messages { get; } = [];

    /// <summary>As in the real conversation: a message that failed left nothing in it.</summary>
    public bool HasHistory { get; private set; }

    public async Task<StepAgentReply> SendAsync(string message, string tier, string? machine, CancellationToken cancellationToken)
    {
        StepAgentReply reply = await owner.SendAsync(this, message, tier, machine, cancellationToken);
        HasHistory = true;
        return reply;
    }
}

public sealed partial class PlanRunnerTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    [Fact]
    public void Aggressive_mode_routes_unpinned_models_to_hub_without_changing_the_worker_workspace()
    {
        Assert.Equal("primary", PlanRunner.ModelMachineFor(FleetMode.Aggressive, null, "worker-a", "primary"));
        Assert.Equal("worker-a", PlanRunner.ModelMachineFor(FleetMode.Conservative, null, "worker-a", "primary"));
        Assert.Equal("worker-b", PlanRunner.ModelMachineFor(FleetMode.Aggressive, "worker-b", "worker-a", "primary"));
    }

    private PlanRunner Runner(FakeStepAgent agent, Func<string, string> verify, TimeSpan? timeout = null, ISleepGuard? sleepGuard = null,
        Func<int, TimeSpan>? transientDelay = null, Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        IWorkerWorkspaceManager? workerWorkspaces = null, Func<DateTimeOffset>? utcNow = null, IPlanNotifier? notifier = null) =>
        new(Store, new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(verify(command))), agent, NullLogger.Instance, timeout,
            transientDelay: transientDelay ?? (_ => TimeSpan.Zero), sleepGuard: sleepGuard,
            workerWorkspaces: workerWorkspaces, delayAsync: delayAsync ?? ((_, _) => Task.CompletedTask), utcNow: utcNow, notifier: notifier);

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private static string Fail(string _) => "Exit code: 1\n--- stdout ---\nerror CS1002: ; expected";

    private PlanRecord ApprovedPlan(params PlanStepInput[] steps) => Store.Approve(NewPlan(steps).Id, autoRetries: 0)!;

    private PlanRecord InterruptedWorkerPlan(out WorkerWorkspaceBaseline baseline)
    {
        PlanRecord plan = NewPlan(new PlanStepInput("recover worker work", "detail", ["a.cs"], "dotnet build", "standard", RetrySafe: true));
        plan = Store.SelectMachine(plan.Id, 1, "worker-a", out string? selectionError)!;
        Assert.Null(selectionError);
        plan = Store.Approve(plan.Id, autoRetries: 0)!;
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select(step => step with { Status = StepStatus.Running, Attempts = 1 }).ToList()
        });
        baseline = new WorkerWorkspaceBaseline("worker-a", new Dictionary<string, string> { ["a.cs"] = "baseline" });
        Store.SaveWorkerBaseline(plan.Id, 1, 1, baseline.Machine, baseline.Files);
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.AttemptStarted, "standard", "worker-a", "Interrupted attempt.",
            modelNode: "worker-a", workspaceNode: "worker-a");
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.WorkspaceStaged, "standard", "worker-a", "Staged the project.",
            modelNode: "worker-a", workspaceNode: "worker-a");
        return Store.Get(plan.Id)!;
    }

    private static FakeStepAgent Agent(string reply = "Done.") => new((_, _, _) => Task.FromResult(reply));

    private FleetOptions OptionsWithNodes(params FleetNodeConfig[] nodes)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONFIG_PATH"] = Path.Combine(PlansDirectory, "fleet.config.json")
            })
            .Build();
        var configStore = new FleetConfigStore(configuration);
        configStore.Save(configStore.Current with { Nodes = nodes });
        return FleetOptions.Load(configuration, configStore);
    }

    private sealed class HealthyNodeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Method == HttpMethod.Post
                ? "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}"
                : "{\"models\":[{\"name\":\"m:latest\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class HealthyNodeFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HealthyNodeHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class FakeWorkerWorkspaceManager(WorkerWorkspaceRecoveryAnalysis recovery) : IWorkerWorkspaceManager
    {
        public List<string> StagedMachines { get; } = [];
        public int ResumeCalls { get; private set; }
        public bool WorkerAnswers { get; set; } = true;

        public Task<IWorkerWorkspaceSession> StageAsync(PlanRecord plan, PlanStep step, string machine, CancellationToken cancellationToken)
        {
            StagedMachines.Add(machine);
            return Task.FromResult<IWorkerWorkspaceSession>(new FakeWorkerWorkspaceSession(machine) { Answers = WorkerAnswers });
        }

        public Task<WorkerWorkspaceResume> ResumeInterruptedAsync(PlanRecord plan, PlanStep step,
            WorkerWorkspaceBaseline baseline, CancellationToken cancellationToken)
        {
            ResumeCalls++;
            return Task.FromResult(new WorkerWorkspaceResume(recovery.Safe
                ? new FakeWorkerWorkspaceSession(baseline.Machine) { Answers = WorkerAnswers }
                : null, recovery));
        }
    }

    private sealed class FakeWorkerWorkspaceSession(string machine) : IWorkerWorkspaceSession
    {
        public string Machine => machine;
        public string Platform => "linux";
        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);
        public bool Answers { get; set; } = true;
        public Task<bool> AnswersAsync(CancellationToken cancellationToken) => Task.FromResult(Answers);
        public string SnapshotId() => "synthetic-snapshot";
        public Dictionary<string, string> BaselineHashes() => new(StringComparer.OrdinalIgnoreCase) { ["a.cs"] = "baseline" };
        public IDisposable Enter() => new NoopScope();
        public Task SyncToHubAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshFromHubAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }

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
    public async Task A_failing_check_retries_at_the_requested_tier_with_the_real_output()
    {
        PlanRecord plan = ApprovedPlan(Step("build it", tier: "standard"));
        int verifications = 0;
        FakeStepAgent agent = Agent();

        await Runner(agent, command => ++verifications < 3 ? Fail(command) : Pass(command)).RunPlanAsync(plan.Id, default);

        Assert.Equal(["standard", "standard", "standard"], agent.Calls.Select(call => call.Tier));
        Assert.DoesNotContain("did not pass", agent.Calls[0].Prompt);
        Assert.Contains("did not pass", agent.Calls[1].Prompt);
        Assert.Contains("CS1002", agent.Calls[1].Prompt);
        Assert.Contains("CS1002", agent.Calls[2].Prompt);
        Assert.Equal(PlanStatus.Done, Store.Get(plan.Id)!.Status);
    }

    [Fact]
    public async Task Each_conversation_is_recorded_on_the_step_when_it_starts_not_when_it_is_checked()
    {
        PlanRecord plan = ApprovedPlan(Step("build it"));
        var seenWhileWorking = new List<int>();
        // The first conversation only reads (a round that changes nothing), so the ladder starts a second one. An attempt
        // is a conversation: rounds inside it do not count again.
        FakeStepAgent agent = new((_, _, _) =>
        {
            seenWhileWorking.Add(Store.Get(plan.Id)!.Steps[0].Attempts);
            return Task.FromResult("Done.");
        })
        {
            ToolsOf = call => (1, call.Session > 0)
        };
        int checks = 0;

        await Runner(agent, command => command.StartsWith("git ", StringComparison.Ordinal)
            ? Pass(command)
            : ++checks switch { 1 => "Exit code: 1\nerror CS1002: ; expected", 2 => "Exit code: 1\nerror CS1003: syntax error", _ => Pass(command) })
            .RunPlanAsync(plan.Id, default);

        Assert.Equal([1, 2, 2], seenWhileWorking);
        Assert.Equal(2, Store.Get(plan.Id)!.Steps[0].Attempts);
        Assert.Equal(2, agent.Sessions.Count);
    }

    [Fact]
    public async Task A_user_selected_machine_is_used_without_changing_the_step_tier()
    {
        PlanRecord plan = NewPlan(Step("build it", tier: "light"));
        PlanRecord? routed = Store.SelectMachine(plan.Id, 1, "worker-b", out string? error);
        Assert.Null(error);
        Assert.Equal("worker-b", routed!.Steps[0].Machine);
        plan = Store.Approve(plan.Id, autoRetries: 0)!;
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        Assert.Equal("light", agent.Calls.Single().Tier);
        Assert.Equal("worker-b", agent.Calls.Single().Machine);
        // The chosen machine is the step's workspace route; the model route is a separate field.
        Assert.Contains(Store.Get(plan.Id)!.Events!, e => e.Kind == RunEventKind.MachineSelected && e.WorkspaceNode == "worker-b");
    }

    [Fact]
    public void A_ready_pending_step_can_be_routed_while_the_plan_is_running()
    {
        PlanRecord plan = ApprovedPlan(Step("first", tier: "standard"), Step("next", tier: "light"));
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select((step, index) => index == 0
                ? step with { Status = StepStatus.Done, CompletedUtc = DateTimeOffset.UtcNow }
                : step with { Status = StepStatus.Pending }).ToList()
        });

        PlanRecord? routed = Store.SelectMachine(plan.Id, 2, "worker-a", out string? error);

        Assert.Null(error);
        Assert.Equal("worker-a", routed!.Steps[1].Machine);
        Assert.Equal("light", routed.Steps[1].Tier);
    }

    [Fact]
    public async Task A_blocked_failed_step_can_be_routed_before_retry_but_a_running_or_waiting_step_cannot()
    {
        PlanRecord plan = ApprovedPlan(Step("fails"), Step("waits"));
        await Runner(Agent(), Fail).RunPlanAsync(plan.Id, default);

        PlanRecord? routed = Store.SelectMachine(plan.Id, 1, "worker-a", out string? failedError);
        Assert.Null(failedError);
        Assert.Equal("worker-a", routed!.Steps[0].Machine);
        Assert.NotNull(Store.SelectMachine(plan.Id, 2, "worker-a", out string? waitingError));
        Assert.NotNull(waitingError);

        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select((step, index) => index == 0
                ? step with { Status = StepStatus.Running }
                : step with { Status = StepStatus.Pending }).ToList()
        });
        Assert.NotNull(Store.SelectMachine(plan.Id, 1, "hub", out string? runningError));
        Assert.NotNull(runningError);
    }

    [Fact]
    public void A_pending_parallel_step_cannot_be_moved_after_its_group_has_started()
    {
        PlanRecord plan = ApprovedPlan(Step("first parallel task", tier: "standard"), Step("second parallel task", tier: "light"));
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select((step, index) => step with
            {
                ParallelGroup = "pair",
                Status = index == 0 ? StepStatus.Running : StepStatus.Pending
            }).ToList()
        });

        Assert.NotNull(Store.SelectMachine(plan.Id, 2, "worker-b", out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task A_step_that_never_passes_blocks_the_plan_and_leaves_later_steps_untouched()
    {
        PlanRecord plan = ApprovedPlan(Step("hard one"), Step("never reached"));
        FakeStepAgent agent = Agent();

        await Runner(agent, Fail).RunPlanAsync(plan.Id, default);

        // Without a hub to climb to: the worker's rounds, then a fresh conversation with a brief, each with its round budget.
        Assert.Equal(2 * RepairLadder.DefaultRoundsPerRung, agent.Calls.Count);
        Assert.Equal(2, agent.Sessions.Count);
        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
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
    public async Task Each_model_round_persists_its_failure_class_and_tool_summary()
    {
        PlanRecord plan = ApprovedPlan(Step("build it"));
        int checks = 0;
        var agent = new FakeStepAgent((_, _, _) => Task.FromResult("Worked on the build."), toolCalls: 3, editToolCalled: true);

        await Runner(agent, _ => ++checks == 1 ? "Exit code: 1\nerror CS1002: ; expected" : Pass("build"))
            .RunPlanAsync(plan.Id, default);

        PlanRunEvent[] rounds = Store.Get(plan.Id)!.Events!
            .Where(runEvent => runEvent.Kind == RunEventKind.RoundClassified)
            .ToArray();
        Assert.Equal(2, rounds.Length);
        Assert.Equal(nameof(FailureClass.CodeProgress), rounds[0].FailureClass);
        Assert.Equal("CS1002", rounds[0].FailureSignature);
        Assert.Equal(1, rounds[0].FailureSignatureSize);
        Assert.Equal(0, rounds[0].FilesChanged);
        Assert.Equal(3, rounds[0].ToolCalls);
        Assert.True(rounds[0].EditToolCalled);
        Assert.Equal("Passed", rounds[1].FailureClass);
    }

    [Fact]
    public async Task When_no_machine_answers_the_step_waits_without_spending_its_attempts()
    {
        PlanRecord plan = ApprovedPlan(Step("during a reboot", tier: "standard"));
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) => ++calls <= 12
            ? throw new HttpRequestException("No connection could be made because the target machine actively refused it.")
            : Task.FromResult("fine"));

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        // Twelve outages and then the first real attempt, still on the step's own tier: nothing was escalated.
        Assert.Equal(13, agent.Calls.Count);
        Assert.All(agent.Calls, call => Assert.Equal("standard", call.Tier));
        Assert.Equal(12, after.Events!.Count(e => e.Kind == RunEventKind.Waiting));
        Assert.Equal(1, after.Steps[0].Attempts);
        Assert.Contains("does not count as an attempt", after.Events!.First(e => e.Kind == RunEventKind.Waiting).Detail);
    }

    [Fact]
    public async Task An_outage_keeps_waiting_past_the_former_eight_wait_limit()
    {
        PlanRecord plan = ApprovedPlan(Step("long reboot"));
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) => ++calls <= 20
            ? throw new HttpRequestException("unreachable")
            : Task.FromResult("fine"));

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(21, agent.Calls.Count);
        Assert.Equal(20, after.Events!.Count(e => e.Kind == RunEventKind.Waiting));
        Assert.Equal(1, after.Steps[0].Attempts);
    }

    [Fact]
    public async Task Model_outage_uses_same_tier_alternate_then_hub_only_when_scope_allows()
    {
        FleetNodeConfig hub = new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);
        FleetNodeConfig workerA = new("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard");
        FleetOptions workerOptions = OptionsWithNodes(
            hub,
            workerA,
            new FleetNodeConfig("worker-b", "http://worker-b.example.test/v1", "m", "worker", "standard"));
        var workerHealth = new FleetHealthMonitor(workerOptions, new HealthyNodeFactory());
        FleetOptions hubOptions = OptionsWithNodes(hub, workerA);
        var hubHealth = new FleetHealthMonitor(hubOptions, new HealthyNodeFactory());

        async Task<(string[] Machines, PlanRecord Plan)> RunPinnedAsync(FleetOptions options, FleetHealthMonitor health, bool allowHub)
        {
            PlanRecord plan = NewPlan(Step("recover", tier: "standard"));
            plan = Store.SelectMachine(plan.Id, 1, "worker-a", out string? selectError)!;
            Assert.Null(selectError);
            plan = Store.Approve(plan.Id, recoveryScope: allowHub ? PlanRecoveryScope.AllowHubRescue : PlanRecoveryScope.WorkerOnly, autoRetries: 0)!;
            int calls = 0;
            var agent = new FakeStepAgent((_, _, _) => ++calls == 1
                ? throw new HttpRequestException("model server unavailable")
                : Task.FromResult("finished"));
            var runner = new PlanRunner(Store,
                new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(Pass(command))),
                agent, NullLogger.Instance, fleetOptions: options, healthMonitor: health,
                transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask);

            await runner.RunPlanAsync(plan.Id, default);
            PlanRecord after = Store.Get(plan.Id)!;
            Assert.Equal(PlanStatus.Done, after.Status);
            return (agent.Calls.Select(call => call.Machine!).ToArray(), after);
        }

        (string[] sameTierMachines, PlanRecord sameTierPlan) = await RunPinnedAsync(workerOptions, workerHealth, allowHub: false);
        Assert.Equal(["worker-a", "worker-b"], sameTierMachines);
        Assert.Contains(sameTierPlan.Events!, runEvent => runEvent.Kind == RunEventKind.RecoveryRouted && runEvent.ModelNode == "worker-b");

        (string[] waitingMachines, PlanRecord waitingPlan) = await RunPinnedAsync(hubOptions, hubHealth, allowHub: false);
        Assert.Equal(["worker-a", "worker-a"], waitingMachines);
        Assert.Contains(waitingPlan.Events!, runEvent => runEvent.Kind == RunEventKind.InferenceProbePassed && runEvent.Node == "worker-a");

        (string[] hubMachines, PlanRecord hubPlan) = await RunPinnedAsync(hubOptions, hubHealth, allowHub: true);
        Assert.Equal(["worker-a", "hub"], hubMachines);
        Assert.Contains(hubPlan.Events!, runEvent => runEvent.Kind == RunEventKind.RecoveryRouted && runEvent.ModelNode == "hub");
    }

    [Fact]
    public async Task An_outage_waits_only_until_the_plan_deadline()
    {
        PlanRecord plan = ApprovedPlan(Step("machines gone"));
        DateTimeOffset fakeNow = DateTimeOffset.UtcNow;
        DateTimeOffset deadline = fakeNow.AddSeconds(20);
        plan = Store.Update(plan.Id, current => current with { RunDeadlineUtc = deadline })!;
        var agent = new FakeStepAgent((_, _, _) => throw new HttpRequestException("unreachable"));
        int waits = 0;
        Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fakeNow = ++waits < 20 ? fakeNow + delay : deadline;
            return Task.CompletedTask;
        }

        await Runner(agent, Pass, transientDelay: _ => TimeSpan.FromSeconds(1), delayAsync: Delay, utcNow: () => fakeNow)
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(20, agent.Calls.Count);
        Assert.Equal(20, after.Events!.Count(runEvent => runEvent.Kind == RunEventKind.Waiting));
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.RunDeadlineExceeded);
        Assert.Contains("deadline", after.Steps[0].Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_worker_transport_staging_failure_waits_without_spending_an_attempt_until_deadline()
    {
        DateTimeOffset fakeNow = DateTimeOffset.UtcNow;
        string project = Path.Combine(Path.GetTempPath(), "fleet-recovery-test-" + Guid.NewGuid().ToString("N"));
        string keyPath = Path.Combine(project, "worker-key.pem");
        Directory.CreateDirectory(project);
        using (RSA rsa = RSA.Create(2048)) File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
        File.WriteAllText(Path.Combine(project, "a.cs"), "class Sample { }");

        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FLEET_CONFIG_PATH"] = Path.Combine(PlansDirectory, "staging-fleet.config.json")
                })
                .Build();
            var configStore = new FleetConfigStore(configuration);
            configStore.Save(configStore.Current with
            {
                Nodes =
                [
                    new FleetNodeConfig("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true),
                    new FleetNodeConfig("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard",
                        Workspace: new FleetWorkerWorkspaceConfig("127.0.0.1", "test-user", keyPath,
                            "SHA256:synthetic-host-key", "/tmp/fleet-test/workspaces", "linux", Port: 1))
                ]
            });
            FleetOptions options = FleetOptions.Load(configuration, configStore);
            PlanRecord plan = NewPlan(Step("stage on worker", tier: "standard"));
            plan = Store.Update(plan.Id, current => current with
            {
                WorkingDirectory = project,
                RunDeadlineUtc = fakeNow.AddSeconds(10)
            })!;
            plan = Store.SelectMachine(plan.Id, 1, "worker-a", out string? selectError)!;
            Assert.Null(selectError);
            plan = Store.Approve(plan.Id, autoRetries: 0)!;
            plan = Store.Update(plan.Id, current => current with { RunDeadlineUtc = fakeNow.AddSeconds(10) })!;

            var workspaces = new WorkerWorkspaceManager(options, NullLogger.Instance, TimeSpan.FromSeconds(1));
            var runner = new PlanRunner(Store,
                new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(Pass(command))),
                Agent(), NullLogger.Instance, fleetOptions: options, workerWorkspaces: workspaces,
                transientDelay: _ => TimeSpan.FromSeconds(10),
                delayAsync: (delay, cancellationToken) => { cancellationToken.ThrowIfCancellationRequested(); fakeNow += delay; return Task.CompletedTask; },
                utcNow: () => fakeNow);
            await runner.RunPlanAsync(plan.Id, default);

            PlanRecord after = Store.Get(plan.Id)!;
            Assert.Equal(PlanStatus.Blocked, after.Status);
            Assert.Equal(0, after.Steps[0].Attempts);
            Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.Waiting && runEvent.WorkspaceNode == "worker-a");
            Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.RunDeadlineExceeded);
            Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundClassified && runEvent.FailureClass == nameof(FailureClass.Infra));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task An_interrupted_worker_without_its_saved_baseline_blocks_without_replaying()
    {
        PlanRecord plan = ApprovedPlan(Step("interrupted worker"));
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select(step => step with { Status = StepStatus.Running, Attempts = 1 }).ToList()
        });
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.WorkspaceStaged, "standard", "worker-a",
            "Staged the project on this worker.", modelNode: "worker-a", workspaceNode: "worker-a");
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Empty(agent.Calls);
        Assert.Contains("without a recoverable staged baseline", after.Steps[0].Note);
    }

    [Fact]
    public async Task A_reconciled_worker_snapshot_does_not_need_its_deleted_baseline_after_restart()
    {
        PlanRecord plan = ApprovedPlan(Step("reconciled worker"));
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select(step => step with { Status = StepStatus.Running, Attempts = 1 }).ToList()
        });
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.WorkspaceStaged, "standard", "worker-a",
            "Staged the project on this worker.", modelNode: "worker-a", workspaceNode: "worker-a");
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.WorkspaceReconciled, "standard", "worker-a",
            "The snapshot was compared with the hub and safely re-staged.", modelNode: "worker-a", workspaceNode: "worker-a");
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Single(agent.Calls);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.AttemptStarted && runEvent.Attempt == 2);
    }

    [Fact]
    public async Task Restart_with_unchanged_worker_files_replays_the_same_attempt()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        var recovery = new WorkerWorkspaceRecoveryAnalysis([], [], []);
        var workspaces = new FakeWorkerWorkspaceManager(recovery);
        FakeStepAgent agent = Agent();

        await Runner(agent, Pass, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(1, after.Steps[0].Attempts);
        Assert.Equal(1, workspaces.ResumeCalls);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.WorkspaceReconciled && runEvent.Detail.Contains("match their saved staged baseline", StringComparison.Ordinal));
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.WorkspaceStaged && runEvent.Attempt == 1);
        Assert.Single(agent.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_worker_check_timeout_with_a_live_session_is_a_failed_check(bool wrapTimeout)
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = true };
        var agent = Agent("I will make the check finish.");
        Exception timeout = new WorkerCommandTimeoutException("Operation has timed out.",
            new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out."));
        if (wrapTimeout) timeout = new InvalidOperationException("wrapped timeout", timeout);
        int checks = 0;

        string Verify(string command)
        {
            if (command == "dotnet build" && checks++ == 0) throw timeout;
            return Pass(command);
        }

        await Runner(agent, Verify, workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, checks);
        Assert.Contains(agent.Calls.Skip(1), call => call.Prompt.Contains("The check never finished.", StringComparison.Ordinal));
        Assert.Contains(agent.Calls.Skip(1), call => call.Prompt.Contains("Error: command exceeded the 120s timeout and was killed.", StringComparison.Ordinal));
        Assert.Contains(after.Events!, runEvent => runEvent.Kind is RunEventKind.CheckFailed or RunEventKind.FinalValidationFailed);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundClassified &&
            runEvent.FailureClass == nameof(FailureClass.Environment));
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.CheckHealed);
    }

    [Fact]
    public async Task A_worker_check_timeout_with_a_dead_session_remains_an_environment_park()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = false };
        FakeStepAgent agent = Agent();

        await Runner(agent, _ => throw new WorkerCommandTimeoutException("Operation has timed out.",
                new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out.")),
            workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
        PlanRunEvent parked = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepParked);
        Assert.Equal("environment", parked.FailureSignature);
        Assert.Contains("Worker workspace unavailable:", parked.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.CheckHealed);
    }

    [Fact]
    public async Task A_plain_ssh_timeout_with_a_live_worker_remains_an_environment_park()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        var workspaces = new FakeWorkerWorkspaceManager(new WorkerWorkspaceRecoveryAnalysis([], [], [])) { WorkerAnswers = true };
        FakeStepAgent agent = Agent();

        await Runner(agent, _ => throw new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out."),
            workerWorkspaces: workspaces).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
        PlanRunEvent parked = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepParked);
        Assert.Equal("environment", parked.FailureSignature);
        Assert.Contains("Worker workspace unavailable:", parked.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("The check never finished.", parked.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_with_safe_worker_edits_runs_the_check_without_another_model_call()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        var recovery = new WorkerWorkspaceRecoveryAnalysis(["a.cs"], ["a.cs"], []);
        var workspaces = new FakeWorkerWorkspaceManager(recovery);
        FakeStepAgent agent = Agent();
        int checks = 0;

        await Runner(agent, command => { if (command == "dotnet build") checks++; return Pass(command); }, workerWorkspaces: workspaces)
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.True(after.Status == PlanStatus.Done, string.Join(Environment.NewLine, after.Events!.Select(runEvent => $"{runEvent.Kind}: {runEvent.Detail}")));
        Assert.Equal(1, after.Steps[0].Attempts);
        Assert.Equal(1, checks);
        Assert.Empty(agent.Calls);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.WorkspaceReconciled && runEvent.Detail.Contains("a.cs", StringComparison.Ordinal));
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.CheckPassed && runEvent.Attempt == 1);
    }

    [Fact]
    public async Task Restart_with_conflicting_worker_edits_blocks_without_running_the_check()
    {
        PlanRecord plan = InterruptedWorkerPlan(out _);
        var recovery = new WorkerWorkspaceRecoveryAnalysis(["a.cs"], [], ["a.cs"]);
        var workspaces = new FakeWorkerWorkspaceManager(recovery);
        FakeStepAgent agent = Agent();
        int checks = 0;

        await Runner(agent, command => { if (command == "dotnet build") checks++; return Pass(command); }, workerWorkspaces: workspaces)
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(1, workspaces.ResumeCalls);
        Assert.Equal(0, checks);
        Assert.Empty(agent.Calls);
        Assert.Contains("a.cs", after.Steps[0].Note);
        Assert.Contains("Nothing was overwritten", after.Steps[0].Note);
    }

    [Fact]
    public async Task Unknown_model_exceptions_wait_at_their_minimum_pacing_and_park_after_five()
    {
        PlanRecord plan = ApprovedPlan(Step("unknown errors"));
        var delays = new List<TimeSpan>();
        var agent = new FakeStepAgent((_, _, _) => throw new InvalidOperationException("unexpected model failure"));

        await Runner(agent, Pass, delayAsync: (delay, _) => { delays.Add(delay); return Task.CompletedTask; })
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(5, agent.Calls.Count);
        Assert.Equal([20d, 40d, 60d, 80d], delays.Select(delay => delay.TotalSeconds));
        Assert.Contains("InvalidOperationException", after.Steps[0].Note);
        Assert.Contains("five unknown failures", after.Steps[0].Note);
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
        // The step names no file, so what is under test is only that the check runs after the timeout (a step that names
        // files and changed none is not accepted on a passing check, whatever stopped the model).
        PlanRecord plan = ApprovedPlan(new PlanStepInput("slow", "detail of slow", [], "dotnet build", null));
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

        Store.Approve(plan.Id, autoRetries: 0);
        Store.Approve(plan.Id, autoRetries: 0);

        Assert.Equal([plan.Id], approvals);
        await Task.CompletedTask;
    }
}
