using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// The repair loop of the plan runner: a failing check goes back into the conversation the model already has, and a
/// step that stops getting better climbs the ladder (worker, hub in the same conversation, a fresh start with a brief).
/// The project folder is real and git is faked, so "the model changed a file" is something the runner can see.
/// </summary>
public sealed partial class PlanRunnerTests
{
    private string ProjectDirectory
    {
        get
        {
            string directory = Path.Combine(PlansDirectory, "project");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private string SourceFile => Path.Combine(ProjectDirectory, "a.cs");

    private static string FailWith(params string[] failing) =>
        "Exit code: 1\n--- stdout ---\n" + string.Join("\n", failing.Select(name => "FAILED " + name));

    // Git as the runner sees it, over the real project folder: every file in it is "changed or new", so a file the model
    // writes (or rewrites with other content) shows up in the runner's before and after snapshots.
    private PlanTools RepairTools(Func<string, string> verify)
    {
        string project = ProjectDirectory;
        return new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(command switch
        {
            "git rev-parse --show-toplevel" => $"Exit code: 0\n--- stdout ---\n{project}",
            "git rev-parse HEAD" => "Exit code: 0\n--- stdout ---\nabc123",
            _ when command.Contains("--untracked-files=all", StringComparison.Ordinal) =>
                "Exit code: 0\n--- stdout ---\n" + string.Join("\n",
                    Directory.GetFiles(project, "*", SearchOption.AllDirectories)
                        .Select(file => "?? " + Path.GetRelativePath(project, file).Replace('\\', '/'))),
            _ when command.StartsWith("git ", StringComparison.Ordinal) => "Exit code: 0\n--- stdout ---\n",
            _ => verify(command)
        }));
    }

    private PlanRunner RepairRunner(FakeStepAgent agent, Func<string, string> verify, FleetOptions? options = null,
        FleetHealthMonitor? health = null, int roundsPerRung = RepairLadder.DefaultRoundsPerRung,
        Func<DateTimeOffset>? utcNow = null, TimeSpan? stepClock = null, TimeSpan? attemptTimeout = null, IPlanNotifier? notifier = null,
        Func<int, TimeSpan>? transientDelay = null, Func<TimeSpan, CancellationToken, Task>? delayAsync = null) =>
        new(Store, RepairTools(verify), agent, NullLogger.Instance, attemptTimeout: attemptTimeout, fleetOptions: options, healthMonitor: health,
            transientDelay: transientDelay ?? (_ => TimeSpan.Zero), delayAsync: delayAsync ?? ((_, _) => Task.CompletedTask), utcNow: utcNow,
            roundsPerRung: roundsPerRung, stepClock: stepClock, notifier: notifier);

    private PlanRecord RepairPlan(string? machine, string scope)
    {
        PlanRecord plan = Store.Create("Repair", "Fix the widget", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Step("fix the widget", "dotnet build", "standard")]);
        if (machine is not null)
        {
            plan = Store.SelectMachine(plan.Id, 1, machine, out string? error)!;
            Assert.Null(error);
        }

        return Store.Approve(plan.Id, recoveryScope: scope, autoRetries: 0, review: PlanSecondOpinion.Off)!;
    }

    private (FleetOptions Options, FleetHealthMonitor Health) HubFleet()
    {
        FleetNodeConfig hub = new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);
        FleetNodeConfig workerA = new("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard");
        FleetOptions options = OptionsWithNodes(hub, workerA);
        return (options, new FleetHealthMonitor(options, new HealthyNodeFactory()));
    }

    [Fact]
    public async Task A_failing_check_goes_back_into_the_same_conversation_and_the_first_prompt_is_not_sent_again()
    {
        PlanRecord plan = RepairPlan(machine: null, PlanRecoveryScope.WorkerOnly);
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(SourceFile, ++calls == 1 ? "first try" : "fixed");
            return Task.FromResult("Worked on it.");
        });

        await RepairRunner(agent, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders", "widget::clicks")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        FakeStepSession session = Assert.Single(agent.Sessions);
        Assert.Equal(2, session.Messages.Count);
        Assert.Contains("Overall goal", session.Messages[0].Text);
        Assert.DoesNotContain("Overall goal", session.Messages[1].Text);
        Assert.Contains("FAILED widget::renders", session.Messages[1].Text);
        Assert.Contains("Round 2", session.Messages[1].Text);
        Assert.Contains("a.cs", session.Messages[1].Text);
        Assert.Equal(1, after.Steps[0].Attempts);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        PlanRunEvent[] rounds = after.Events!.Where(runEvent => runEvent.Kind == RunEventKind.RoundClassified).ToArray();
        Assert.Equal([1, 2], rounds.Select(runEvent => runEvent.Round));
        Assert.All(rounds, runEvent => Assert.Equal(1, runEvent.Rung));
        Assert.Equal(["a.cs"], rounds[0].ChangedFiles);
        Assert.Equal("Passed", rounds[1].FailureClass);
    }

    [Fact]
    public async Task A_worker_that_never_edits_goes_to_the_hub_after_one_round_in_the_same_conversation()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.AllowHubRescue);
        FakeStepAgent agent = null!;
        agent = new FakeStepAgent((_, _, _) =>
        {
            // Only the hub's model fixes the file; the worker reads and says it is done.
            if (agent.Calls[^1].Machine == "hub")
            {
                File.WriteAllText(SourceFile, "fixed");
            }

            return Task.FromResult("Looked at it.");
        })
        {
            ToolsOf = call => call.Machine == "hub" ? (4, true) : (2, false)
        };

        await RepairRunner(agent, _ => File.Exists(SourceFile) && File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["worker-a", "hub"], agent.Calls.Select(call => call.Machine));
        FakeStepSession session = Assert.Single(agent.Sessions);
        Assert.Contains("A more capable model (the hub) has taken over", session.Messages[1].Text);
        Assert.Contains("FAILED widget::renders", session.Messages[1].Text);
        PlanRunEvent climb = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Equal(2, climb.Rung);
        Assert.Equal("hub", climb.ModelNode);
        Assert.Contains("rung 1", climb.Detail);
        Assert.Contains("rung 2", climb.Detail);
        Assert.Contains("made no edit and changed no file", climb.Detail);
        Assert.Equal(1, after.Steps[0].Attempts);

        // The morning report says how far up the ladder the step had to go.
        string report = FleetPlanStore.ToReportMarkdown(after);
        Assert.Contains("Repair ladder: 1 failed round, reached rung 2 (the hub model, same conversation)", report);
        Assert.Contains("attempt 1, rung 1 round 1", report);
        Assert.Contains("attempt 1, rung 2", report);
    }

    [Fact]
    public async Task With_worker_only_scope_the_hub_is_never_called_and_the_step_stops_after_a_fresh_start_with_a_brief()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        FakeStepAgent agent = null!;
        agent = new FakeStepAgent((_, _, _) =>
        {
            if (agent.Calls[^1].Machine == "hub")
            {
                File.WriteAllText(SourceFile, "fixed");
            }

            return Task.FromResult("Looked at it.");
        })
        {
            ToolsOf = _ => (2, false)
        };

        await RepairRunner(agent, _ => File.Exists(SourceFile) ? Pass("check") : FailWith("widget::renders"), options, health)
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.DoesNotContain(agent.Calls, call => call.Machine == "hub");
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
        // The worker's round changed nothing, so the step moved on at once, to a new conversation on a worker that
        // knows what was tried; that one changed nothing either and the ladder ended.
        Assert.Equal(2, agent.Sessions.Count);
        Assert.Contains("A conversation before this one", agent.Sessions[1].Messages[0].Text);
        Assert.Contains("Round 1 on worker-a", agent.Sessions[1].Messages[0].Text);
        PlanRunEvent climb = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Equal(3, climb.Rung);
        Assert.Contains("a fresh conversation with a brief", climb.Detail);
        Assert.Contains("worker-a", climb.Detail);
    }

    [Fact]
    public void A_plan_with_no_recorded_scope_is_approved_with_hub_rescue_allowed()
    {
        // A plan created with no scope and approved with no scope argument gets AllowHubRescue.
        PlanRecord plan = Store.Create("Plan", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Step("one", "dotnet build", "standard")]);
        PlanRecord approved = Store.Approve(plan.Id, autoRetries: 0)!;
        Assert.Equal(PlanRecoveryScope.AllowHubRescue, approved.RecoveryScope);

        // A plan approved with an explicit WorkerOnly scope gets WorkerOnly.
        PlanRecord plan2 = Store.Create("Plan2", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Step("one", "dotnet build", "standard")]);
        PlanRecord approved2 = Store.Approve(plan2.Id, recoveryScope: PlanRecoveryScope.WorkerOnly, autoRetries: 0)!;
        Assert.Equal(PlanRecoveryScope.WorkerOnly, approved2.RecoveryScope);

        // A plan created with a stored scope keeps it when approved without a scope argument.
        PlanRecord plan3 = Store.Create("Plan3", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Step("one", "dotnet build", "standard")], recoveryScope: PlanRecoveryScope.WorkerOnly);
        PlanRecord approved3 = Store.Approve(plan3.Id, autoRetries: 0)!;
        Assert.Equal(PlanRecoveryScope.WorkerOnly, approved3.RecoveryScope);
    }

    [Fact]
    public async Task A_worker_that_times_out_with_edits_hands_the_step_to_the_hub_at_once_when_rescue_is_allowed()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.AllowHubRescue);
        FakeStepAgent agent = null!;
        agent = new FakeStepAgent(async (_, _, ct) =>
        {
            if (agent.Calls[^1].Machine == "worker-a")
            {
                File.WriteAllText(SourceFile, "partial");
                await Task.Delay(Timeout.Infinite, ct);
            }
            else if (agent.Calls[^1].Machine == "hub")
            {
                File.WriteAllText(SourceFile, "fixed");
            }

            return "Worked on it.";
        });

        await RepairRunner(agent, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders"), options, health, attemptTimeout: TimeSpan.FromMilliseconds(300))
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["worker-a", "hub"], agent.Calls.Select(c => c.Machine));
        PlanRunEvent climb = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Equal(2, climb.Rung);
        Assert.Contains("ran out of time", climb.Detail);
    }

    [Fact]
    public async Task A_worker_only_plan_whose_worker_times_out_keeps_its_worker_for_the_next_round()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        int calls = 0;
        FakeStepAgent agent = null!;
        agent = new FakeStepAgent(async (_, _, ct) =>
        {
            int callNumber = ++calls;
            if (callNumber == 1)
            {
                File.WriteAllText(SourceFile, "partial");
                await Task.Delay(Timeout.Infinite, ct);
            }
            else
            {
                File.WriteAllText(SourceFile, "fixed");
            }

            return "Worked on it.";
        });

        await RepairRunner(agent, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders"), options, health, attemptTimeout: TimeSpan.FromMilliseconds(300))
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.All(agent.Calls, call => Assert.Equal("worker-a", call.Machine));
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
    }

    [Fact]
    public async Task A_model_call_that_gets_no_reply_is_reported_as_that_and_not_as_the_attempt_running_out_of_time()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            if (++calls == 1)
            {
                // What an HttpClient raises when its timeout passes while the model is still writing.
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing.");
            }

            File.WriteAllText(SourceFile, "fixed");
            return Task.FromResult("Worked on it.");
        });

        await RepairRunner(agent, _ => File.Exists(SourceFile) && File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders"), options, health).RunPlanAsync(plan.Id, default);

        PlanRunEvent timedOut = Assert.Single(Store.Get(plan.Id)!.Events!, runEvent => runEvent.Kind == RunEventKind.TimedOut);
        Assert.Contains("got no reply within 15 minutes", timedOut.Detail);
        Assert.Contains("too long to finish", timedOut.Detail);
        Assert.DoesNotContain("ran out of time", timedOut.Detail);
    }

    [Fact]
    public async Task The_same_failure_with_no_file_changed_is_a_stall_and_climbs_a_rung()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        File.WriteAllText(SourceFile, "old");
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.AllowHubRescue);
        FakeStepAgent agent = null!;
        agent = new FakeStepAgent((_, _, _) =>
        {
            // The worker says it edited, and rewrites nothing; the hub fixes it.
            if (agent.Calls[^1].Machine == "hub")
            {
                File.WriteAllText(SourceFile, "fixed");
            }

            return Task.FromResult("Edited.");
        });

        await RepairRunner(agent, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["worker-a", "worker-a", "hub"], agent.Calls.Select(call => call.Machine));
        Assert.Single(agent.Sessions);
        PlanRunEvent climb = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Contains("left the same failures and changed no file", climb.Detail);
        Assert.Equal(nameof(FailureClass.Stall), after.Events!.Where(runEvent => runEvent.Kind == RunEventKind.RoundClassified).ElementAt(1).FailureClass);
    }

    [Fact]
    public async Task A_rung_gets_its_round_budget_while_the_failures_keep_changing()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.AllowHubRescue);
        int calls = 0;
        FakeStepAgent agent = null!;
        agent = new FakeStepAgent((_, _, _) =>
        {
            // Every worker round edits something new and gets a different failure: progress, but not enough.
            File.WriteAllText(SourceFile, agent.Calls[^1].Machine == "hub" ? "fixed" : $"attempt {++calls}");
            return Task.FromResult("Edited.");
        });

        await RepairRunner(agent, _ =>
        {
            string content = File.ReadAllText(SourceFile);
            return content == "fixed" ? Pass("check") : FailWith($"widget::{content.Replace(' ', '-')}");
        }, options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(["worker-a", "worker-a", "worker-a", "hub"], agent.Calls.Select(call => call.Machine));
        Assert.Single(agent.Sessions);
        PlanRunEvent climb = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Contains("3 rounds on this rung did not get the check passing", climb.Detail);
    }

    [Fact]
    public async Task The_last_rung_is_a_fresh_conversation_that_starts_from_a_brief_of_the_rounds_before()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.AllowHubRescue);
        int calls = 0;
        FakeStepAgent agent = null!;
        agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(SourceFile, $"try {++calls}");
            return Task.FromResult($"Tried number {calls}.");
        });

        await RepairRunner(agent, _ => FailWith($"widget::case-{File.ReadAllText(SourceFile).Replace(' ', '-')}"), options, health)
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        // Three rounds on the worker, three on the hub in the same conversation, three in a fresh conversation on the hub.
        Assert.Equal(9, agent.Calls.Count);
        Assert.Equal(["worker-a", "worker-a", "worker-a", "hub", "hub", "hub", "hub", "hub", "hub"], agent.Calls.Select(call => call.Machine));
        Assert.Equal(2, agent.Sessions.Count);
        Assert.Equal(6, agent.Sessions[0].Messages.Count);
        Assert.Equal(3, agent.Sessions[1].Messages.Count);
        string brief = agent.Sessions[1].Messages[0].Text;
        Assert.Contains("A conversation before this one", brief);
        Assert.Contains("Round 1 on worker-a: changed a.cs; the check then failed on widget::case-try-1", brief);
        Assert.Contains("It said: \"Tried number 1.\"", brief);
        Assert.Contains("Round 3 on worker-a", brief);
        Assert.Contains("Round 4 on hub", brief);
        Assert.Contains("Round 6 on hub", brief);
        Assert.Contains("Find the root cause first, then fix it", brief);
        Assert.Contains("Overall goal", brief);
        Assert.Contains("9 rounds over 2 attempts", after.Steps[0].Note);
        Assert.Equal(["Rung 2", "Rung 3"], after.Events!.Where(runEvent => runEvent.Kind == RunEventKind.RungChanged).Select(runEvent => $"Rung {runEvent.Rung}"));
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.PlanBlocked && runEvent.Detail.Contains("repair ladder"));
    }

    [Fact]
    public async Task A_machine_outage_in_the_middle_of_a_conversation_waits_and_sends_the_same_message_again()
    {
        PlanRecord plan = RepairPlan(machine: null, PlanRecoveryScope.WorkerOnly);
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            if (++calls == 2)
            {
                throw new HttpRequestException("the worker went away");
            }

            File.WriteAllText(SourceFile, calls == 1 ? "first try" : "fixed");
            return Task.FromResult("Worked.");
        });

        await RepairRunner(agent, _ => File.Exists(SourceFile) && File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        FakeStepSession session = Assert.Single(agent.Sessions);
        Assert.Equal(3, session.Messages.Count);
        Assert.Equal(session.Messages[1].Text, session.Messages[2].Text);
        Assert.Contains("Round 2", session.Messages[1].Text);
        Assert.Equal(1, after.Steps[0].Attempts);
        Assert.Equal(1, after.Events!.Count(runEvent => runEvent.Kind == RunEventKind.Waiting));
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
    }

    [Fact]
    public async Task A_restart_keeps_the_rung_and_starts_a_new_conversation_from_a_brief()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        File.WriteAllText(SourceFile, "old");
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.AllowHubRescue);
        // What the first run left in the log: three rounds on the worker, the climb to the hub, one round there, then the
        // backend stopped between rounds.
        for (int round = 1; round <= 3; round++)
        {
            Store.AddEvent(plan.Id, 1, 1, RunEventKind.AttemptEnded, "standard", "worker-a", $"Model finished after 10 s: Try {round}.",
                modelNode: "worker-a", rung: 1, round: round);
            Store.AddEvent(plan.Id, 1, 1, RunEventKind.RoundClassified, "standard", "worker-a", "Round classified.", modelNode: "worker-a",
                failureClass: "CodeProgress", failureSignature: $"widget::case-{round}", failureSignatureSize: 1, filesChanged: 1,
                toolCalls: 3, editToolCalled: true, rung: 1, round: round, changedFiles: ["a.cs"]);
        }

        Store.AddEvent(plan.Id, 1, 1, RunEventKind.RungChanged, "standard", "hub", "Climbing from rung 1 to rung 2.", modelNode: "hub", rung: 2);
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.RoundClassified, "standard", "hub", "Round classified.", modelNode: "hub",
            failureClass: "CodeProgress", failureSignature: "widget::case-4", failureSignatureSize: 1, filesChanged: 1,
            toolCalls: 3, editToolCalled: true, rung: 2, round: 1, changedFiles: ["a.cs"]);
        Store.AddEvent(plan.Id, 1, 1, RunEventKind.CheckFailed, "standard", "hub", "FAILED widget::case-4", modelNode: "hub", rung: 2, round: 1);
        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select(step => step with { Status = StepStatus.Failed, Attempts = 1 }).ToList()
        });
        FakeStepAgent agent = null!;
        agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(SourceFile, "fixed");
            return Task.FromResult("Fixed.");
        });

        await RepairRunner(agent, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::case-5"), options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        (string Tier, string Prompt, string? Machine) first = Assert.Single(agent.Calls);
        Assert.Equal("hub", first.Machine);
        Assert.Contains("A conversation before this one", first.Prompt);
        Assert.Contains("Round 1 on worker-a", first.Prompt);
        Assert.Contains("FAILED widget::case-4", first.Prompt);
        Assert.Equal(2, after.Steps[0].Attempts);
        // The step went on from rung 2, round 2: the restart did not give it a fresh ladder.
        PlanRunEvent resumedRound = after.Events!.Last(runEvent => runEvent.Kind == RunEventKind.RoundClassified);
        Assert.Equal(2, resumedRound.Rung);
        Assert.Equal(2, resumedRound.Round);
    }

    [Fact]
    public async Task A_rung_with_a_spent_budget_at_restart_climbs_before_the_first_message()
    {
        File.WriteAllText(SourceFile, "old");
        PlanRecord plan = RepairPlan(machine: null, PlanRecoveryScope.WorkerOnly);
        for (int round = 1; round <= RepairLadder.DefaultRoundsPerRung; round++)
        {
            Store.AddEvent(plan.Id, 1, 1, RunEventKind.RoundClassified, "standard", "worker-a", "Round classified.", modelNode: "worker-a",
                failureClass: "CodeProgress", failureSignature: $"widget::case-{round}", failureSignatureSize: 1, filesChanged: 1,
                toolCalls: 3, editToolCalled: true, rung: 1, round: round, changedFiles: ["a.cs"]);
        }

        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select(step => step with { Status = StepStatus.Failed, Attempts = 1 }).ToList()
        });
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(SourceFile, "fixed");
            return Task.FromResult("Fixed.");
        });

        await RepairRunner(agent, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::case-9")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        PlanRunEvent climb = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Equal(3, climb.Rung);
        Assert.Contains("A conversation before this one", Assert.Single(agent.Calls).Prompt);
    }

    [Fact]
    public async Task A_parallel_group_retry_continues_the_conversation_of_its_first_attempt()
    {
        FleetNodeConfig hub = new("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true);
        FleetOptions options = OptionsWithNodes(hub,
            new FleetNodeConfig("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard"),
            new FleetNodeConfig("worker-b", "http://worker-b.example.test/v1", "m", "worker", "light"));
        var health = new FleetHealthMonitor(options, new HealthyNodeFactory());
        PlanRecord plan = Store.Approve(Store.Create("Group", "Two at once", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
        [
            new PlanStepInput("write the first file", "detail", ["one.txt"], "check one", "standard", ParallelGroup: "pair"),
            new PlanStepInput("write the second file", "detail", ["two.txt"], "check two", "light", ParallelGroup: "pair"),
            new PlanStepInput("validate everything", "detail", ["a.cs"], "check all", "standard")
        ]).Id, recoveryScope: PlanRecoveryScope.WorkerOnly, autoRetries: 0)!;
        int firstChecks = 0;
        var agent = new FakeStepAgent((prompt, _, _) =>
        {
            string name = prompt.Contains("write the first file", StringComparison.Ordinal) ? "one.txt"
                : prompt.Contains("write the second file", StringComparison.Ordinal) ? "two.txt" : "a.cs";
            File.WriteAllText(Path.Combine(ProjectDirectory, name), "content " + Guid.NewGuid());
            return Task.FromResult("Wrote " + name);
        });

        await RepairRunner(agent, command => command switch
        {
            "check one" when ++firstChecks == 1 => FailWith("one::exists"),
            _ => Pass(command)
        }, options, health).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.ParallelStarted);
        FakeStepSession firstStepSession = Assert.Single(agent.Sessions, session => session.Messages[0].Text.Contains("(1 of 3): write the first file", StringComparison.Ordinal));
        Assert.Equal(2, firstStepSession.Messages.Count);
        Assert.Contains("FAILED one::exists", firstStepSession.Messages[1].Text);
        Assert.Equal(1, after.Steps[0].Attempts);
    }

    // What the fake model writes in each round and what the check then says about those files.
    private static string ChecksOf(string content) => content switch
    {
        "fixed" => Pass("check"),
        "one" => FailWith("t1", "t2"),
        "broke it" => FailWith("t1", "t2", "t3", "t4"),
        "broke the build" => "Exit code: 1\n--- stdout ---\nsrc/a.ts(3,5): error TS2322: Type 'string' is not assignable to type 'number'.",
        "two" => FailWith("t1", "t3"),
        "two again" => FailWith("t2", "t4"),
        _ => FailWith("t1", "t2", "t3")
    };

    private async Task<(PlanRecord Plan, FakeStepAgent Agent)> RunWritingAsync(string[] writes, int roundsPerRung = 3, bool allowHub = false)
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", allowHub ? PlanRecoveryScope.AllowHubRescue : PlanRecoveryScope.WorkerOnly);
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(SourceFile, writes[Math.Min(calls++, writes.Length - 1)]);
            return Task.FromResult($"Wrote {writes[Math.Min(calls - 1, writes.Length - 1)]}.");
        });
        await RepairRunner(agent, _ => ChecksOf(File.ReadAllText(SourceFile)), options, health, roundsPerRung).RunPlanAsync(plan.Id, default);
        return (Store.Get(plan.Id)!, agent);
    }

    [Fact]
    public async Task A_step_that_is_getting_closer_is_not_moved_up_a_rung_for_having_used_its_rounds()
    {
        // Four failing, then three, then two, then done, with two rounds allowed on a rung. Rounds that improve are free, so the
        // worker keeps its conversation and its machine all the way; charged, the second round would have ended the rung.
        (PlanRecord after, FakeStepAgent agent) = await RunWritingAsync(["broke it", "three", "one", "fixed"], roundsPerRung: 2, allowHub: true);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Equal(4, agent.Calls.Count);
        Assert.All(agent.Calls, call => Assert.Equal("worker-a", call.Machine));
    }

    [Fact]
    public async Task A_step_that_stands_still_is_still_moved_up_after_its_rounds()
    {
        // The same number failing round after round is not progress: two rounds, then the climb.
        (PlanRecord after, _) = await RunWritingAsync(["two", "two again", "fixed"], roundsPerRung: 2, allowHub: true);

        Assert.Equal(PlanStatus.Done, after.Status);
        PlanRunEvent climb = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Equal(RepairLadder.HubRung, climb.Rung);
    }

    [Fact]
    public async Task A_round_that_breaks_more_tests_is_undone_and_the_next_message_says_so()
    {
        File.WriteAllText(Path.Combine(ProjectDirectory, "earlier.cs"), "an earlier step's work");

        (PlanRecord after, FakeStepAgent agent) = await RunWritingAsync(["one", "broke it", "fixed"]);

        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal("fixed", File.ReadAllText(SourceFile));
        Assert.Equal("an earlier step's work", File.ReadAllText(Path.Combine(ProjectDirectory, "earlier.cs")));
        PlanRunEvent undone = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundRolledBack);
        Assert.Equal("RolledBack", undone.FailureClass);
        Assert.Equal(2, undone.Round);
        Assert.Equal(["a.cs"], undone.ChangedFiles);
        Assert.Contains("4 failing where round 1 had 2 failing, so it was undone", undone.Detail);
        // What the model is told: what happened, what it broke, and the check output of the state it is in now.
        string message = Assert.Single(agent.Sessions).Messages[2].Text;
        Assert.Contains("Round 2 made the check worse, so the fleet undid it", message);
        Assert.Contains("It broke: t3, t4.", message);
        Assert.Contains("read them again before you edit", message);
        Assert.Contains("FAILED t1", message);
        Assert.DoesNotContain("FAILED t4", message);
        Assert.DoesNotContain("Since your last round", message);
    }

    [Fact]
    public async Task The_files_really_are_the_best_rounds_when_the_round_after_a_bad_one_is_checked()
    {
        string seenAtRoundThree = string.Empty;
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        string[] writes = ["one", "broke it", "one"];
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            // The model's file tools run before the check: what it finds on disk at the start of round 3 is round 1's file.
            if (calls == 2)
            {
                seenAtRoundThree = File.ReadAllText(SourceFile);
            }

            File.WriteAllText(SourceFile, writes[Math.Min(calls++, writes.Length - 1)]);
            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, _ => ChecksOf(File.ReadAllText(SourceFile)), options, health).RunPlanAsync(plan.Id, default);

        Assert.Equal("one", seenAtRoundThree);
    }

    [Fact]
    public async Task A_round_whose_code_no_longer_builds_is_undone_even_when_it_names_fewer_failures()
    {
        (PlanRecord after, FakeStepAgent agent) = await RunWritingAsync(["one", "broke the build", "fixed"]);

        Assert.Equal(PlanStatus.Done, after.Status);
        PlanRunEvent undone = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundRolledBack);
        Assert.Contains("code that does not build (1 error code) where round 1 had 2 failing", undone.Detail);
        string message = Assert.Single(agent.Sessions).Messages[2].Text;
        Assert.Contains("Round 2 made the check worse", message);
        Assert.Contains("code that does not build (1 error code) where round 1 had 2 failing. It broke: TS2322.", message);
        Assert.Contains("Repair ladder: 2 failed rounds, reached rung 1 (the requested tier); 1 round was undone because the check got worse",
            FleetPlanStore.ToReportMarkdown(after));
    }

    [Fact]
    public async Task A_round_that_ties_the_best_is_kept_and_becomes_the_state_to_go_back_to()
    {
        // 2 failing, 2 failing (other names: kept, and now the best), then 3 failing: back to the second round's file.
        (PlanRecord after, _) = await RunWritingAsync(["one", "two again", "three", "three"], roundsPerRung: 5);

        PlanRunEvent[] undone = after.Events!.Where(runEvent => runEvent.Kind == RunEventKind.RoundRolledBack).ToArray();
        Assert.NotEmpty(undone);
        Assert.All(undone, runEvent => Assert.Contains("had 2 failing", runEvent.Detail));
        Assert.Equal("two again", File.ReadAllText(SourceFile));
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.RoundRolledBack && runEvent.Rung == 1 && runEvent.Round == 2);
    }

    [Fact]
    public async Task After_an_undone_round_the_next_one_is_compared_with_the_state_the_project_is_in()
    {
        // Round 3 changes nothing and the project is back at round 1's state: the same failures, no change: a stall.
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            if (++calls <= 2)
            {
                File.WriteAllText(SourceFile, calls == 1 ? "one" : "broke it");
            }

            return Task.FromResult("Done.");
        });

        await RepairRunner(agent, _ => ChecksOf(File.ReadAllText(SourceFile)), options, health).RunPlanAsync(plan.Id, default);

        PlanRunEvent[] rounds = Store.Get(plan.Id)!.Events!.Where(runEvent => runEvent.Kind == RunEventKind.RoundClassified).ToArray();
        Assert.Equal(nameof(FailureClass.CodeProgress), rounds[0].FailureClass);
        Assert.Equal(nameof(FailureClass.CodeProgress), rounds[1].FailureClass);
        Assert.Equal(nameof(FailureClass.Stall), rounds[2].FailureClass);
        // It was the best round's state that the third check ran on, not the undone round's.
        Assert.Equal(rounds[0].FailureSignature, rounds[2].FailureSignature);
        Assert.NotEqual(rounds[1].FailureSignature, rounds[2].FailureSignature);
        Assert.Equal("one", File.ReadAllText(SourceFile));
    }

    [Fact]
    public async Task A_step_that_ends_without_passing_leaves_the_project_at_its_best_state_and_says_so()
    {
        (PlanRecord after, _) = await RunWritingAsync(["three", "one", "broke it", "broke it", "broke it", "broke it", "broke it"]);

        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal("one", File.ReadAllText(SourceFile));
        Assert.Contains("left as it was after its best round (round 2, 2 failing)", after.Steps[0].Note);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.PlanBlocked && runEvent.Detail.Contains("best round (round 2"));
    }

    [Fact]
    public async Task A_conversation_that_starts_after_an_undone_round_is_told_about_it_in_its_brief()
    {
        (PlanRecord after, FakeStepAgent agent) = await RunWritingAsync(["one", "broke it", "broke it", "broke it", "fixed"]);

        Assert.Equal(PlanStatus.Done, after.Status);
        string brief = agent.Sessions[1].Messages[0].Text;
        Assert.Contains("Round 2 on worker-a: changed a.cs; the check then failed on t1, t2, t3, t4.", brief);
        Assert.Contains("It left the step worse than the best round before it, so it was undone.", brief);
        // The fresh conversation is told the check's output for the state the project is in (the best round's), not the worst round's.
        string wrong = brief[brief.IndexOf("What went wrong:", StringComparison.Ordinal)..brief.IndexOf("Look at what is already on disk", StringComparison.Ordinal)];
        Assert.Contains("FAILED t1", wrong);
        Assert.DoesNotContain("FAILED t3", wrong);
    }


    [Fact]
    public async Task A_step_that_keeps_getting_closer_is_not_stopped_at_the_old_forty_five_minutes()
    {
        // On a slow machine a round takes twenty minutes. The step goes from four failing to three to two to done in eighty:
        // the default working time (a backstop of hours, no longer forty-five minutes) does not cut it off while it improves.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        string[] writes = ["broke it", "three", "one", "fixed"];
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            now += TimeSpan.FromMinutes(20);
            File.WriteAllText(SourceFile, writes[Math.Min(calls++, writes.Length - 1)]);
            return Task.FromResult("Worked.");
        });

        await RepairRunner(agent, _ => ChecksOf(File.ReadAllText(SourceFile)), utcNow: () => now).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(4, agent.Calls.Count);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepTimeLimit);
        Assert.Equal(TimeSpan.FromHours(4), PlanRunner.DefaultStepClock);
    }

    [Fact]
    public async Task A_step_stops_when_its_working_time_is_used_up_and_leaves_the_project_at_its_best_state()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        string[] writes = ["three", "one", "two"];
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            now += TimeSpan.FromMinutes(20); // each model call takes twenty minutes
            File.WriteAllText(SourceFile, writes[Math.Min(calls++, writes.Length - 1)]);
            return Task.FromResult("Worked.");
        });

        await RepairRunner(agent, _ => ChecksOf(File.ReadAllText(SourceFile)), utcNow: () => now, stepClock: TimeSpan.FromMinutes(45))
            .RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(3, agent.Calls.Count); // 20, 40 and 60 minutes: the fourth round never starts
        PlanRunEvent limit = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepTimeLimit);
        Assert.Contains("used its 45 minutes of working time (60 minutes of model calls and checks)", limit.Detail);
        Assert.Contains("retrying the step gives it a fresh clock", limit.Detail);
        Assert.Contains("best round", limit.Detail);
        Assert.Equal([1200, 1200, 1200], after.Events!.Where(runEvent => runEvent.Kind == RunEventKind.RoundClassified).Select(runEvent => runEvent.DurationSeconds));
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.PlanBlocked);
        Assert.Equal(StepStatus.Parked, after.Steps[0].Status);
    }

    [Fact]
    public async Task The_working_time_already_used_survives_a_restart()
    {
        File.WriteAllText(SourceFile, "old");
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        for (int round = 1; round <= 2; round++)
        {
            Store.AddEvent(plan.Id, 1, 1, RunEventKind.RoundClassified, "standard", "worker-a", "Round classified.", modelNode: "worker-a",
                failureClass: "CodeProgress", failureSignature: $"t{round}", failureSignatureSize: 1, filesChanged: 1, toolCalls: 3,
                editToolCalled: true, rung: 1, round: round, changedFiles: ["a.cs"], durationSeconds: 25 * 60);
        }

        Store.Update(plan.Id, current => current with
        {
            Status = PlanStatus.Running,
            Steps = current.Steps.Select(step => step with { Status = StepStatus.Failed, Attempts = 1 }).ToList()
        });
        var agent = new FakeStepAgent((_, _, _) => Task.FromResult("Should not be asked."));

        await RepairRunner(agent, _ => FailWith("t1"), stepClock: TimeSpan.FromMinutes(45)).RunPlanAsync(plan.Id, default);

        Assert.Empty(agent.Calls);
        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Contains("(50 minutes of model calls and checks)", Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepTimeLimit).Detail);
    }

    [Fact]
    public async Task A_model_call_is_stopped_when_the_steps_working_time_runs_out_not_twenty_minutes_later()
    {
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        var agent = new FakeStepAgent(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await RepairRunner(agent, _ => FailWith("t1"), stepClock: TimeSpan.FromMilliseconds(700)).RunPlanAsync(plan.Id, default);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Contains("because the step's working time ran out", Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.TimedOut).Detail);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepTimeLimit);
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Single(agent.Calls);
    }

    [Fact]
    public async Task A_model_call_never_outlives_the_plans_run_deadline()
    {
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        Store.Update(plan.Id, current => current with { RunDeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(900) });
        var agent = new FakeStepAgent(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await RepairRunner(agent, _ => FailWith("t1")).RunPlanAsync(plan.Id, default);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Contains("because the plan's run deadline arrived", Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.TimedOut).Detail);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.RunDeadlineExceeded);
        Assert.Equal(PlanStatus.Blocked, after.Status);
    }

    [Fact]
    public async Task A_round_that_changed_only_a_lock_file_made_no_progress_and_climbs_at_once()
    {
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.WorkerOnly);
        int calls = 0;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            // The model ran an install: the lock file changed, no source file did, and it called no edit tool.
            File.WriteAllText(Path.Combine(ProjectDirectory, "package-lock.json"), $"{{ \"run\": {++calls} }}");
            if (calls == 2)
            {
                File.WriteAllText(SourceFile, "fixed");
            }

            return Task.FromResult("Installed things.");
        })
        {
            ToolsOf = _ => (3, false)
        };

        await RepairRunner(agent, _ => ChecksOf(File.Exists(SourceFile) ? File.ReadAllText(SourceFile) : "none")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        PlanRunEvent first = after.Events!.First(runEvent => runEvent.Kind == RunEventKind.RoundClassified);
        Assert.Equal(nameof(FailureClass.NoOp), first.FailureClass);
        Assert.Equal(["package-lock.json"], first.ChangedFiles);
        Assert.Contains("only lock or generated files, so not counted as progress", first.Detail);
        PlanRunEvent climb = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.RungChanged);
        Assert.Contains("the last round made no edit and changed no file apart from lock or generated files", climb.Detail);
        Assert.Equal(PlanStatus.Done, after.Status);
    }

}

public sealed class PlanRepairLogicTests
{
    private static PlanRunEvent Round(int attempt, int rung, int? round, string failureClass = "CodeProgress", string? signature = null,
        IReadOnlyList<string>? files = null, string node = "worker-a") =>
        new(DateTimeOffset.UtcNow, 1, attempt, RunEventKind.RoundClassified, "standard", node, "Round classified.", node, null,
            failureClass, signature, signature is null ? 0 : 1, files?.Count ?? 0, 3, true, rung, round, files);

    [Theory]
    [InlineData(1, 0, false, "NextRound", 1)]
    [InlineData(1, 2, false, "NextRound", 1)]
    [InlineData(1, 3, false, "ContinueOnHub", 2)]
    [InlineData(1, 1, true, "ContinueOnHub", 2)]
    [InlineData(2, 1, false, "NextRound", 2)]
    [InlineData(2, 3, false, "FreshConversation", 3)]
    [InlineData(2, 1, true, "FreshConversation", 3)]
    [InlineData(3, 2, false, "NextRound", 3)]
    [InlineData(3, 3, false, "GiveUp", 3)]
    [InlineData(3, 1, true, "GiveUp", 3)]
    public void The_ladder_climbs_when_rounds_stop_paying_off(int rung, int rounds, bool noChange, string expected, int toRung)
    {
        RepairDecision decision = RepairLadder.Decide(rung, rounds, 3, noChange, "nothing changed", hubRungAvailable: true);

        Assert.Equal(Enum.Parse<RepairMove>(expected), decision.Move);
        Assert.Equal(toRung, decision.ToRung);
    }

    [Fact]
    public void Without_a_hub_rung_the_first_climb_is_a_fresh_conversation()
    {
        RepairDecision decision = RepairLadder.Decide(1, 1, 3, noChange: true, "no edit", hubRungAvailable: false);

        Assert.Equal(RepairMove.FreshConversation, decision.Move);
        Assert.Equal(RepairLadder.FreshRung, decision.ToRung);
        Assert.Equal("no edit", decision.Why);
    }

    [Fact]
    public void The_position_is_counted_from_the_rounds_whose_check_ran_since_the_last_climb()
    {
        PlanRunEvent[] events =
        [
            Round(1, 1, 1),
            Round(1, 1, null, "Infra"),
            Round(1, 1, 2),
            new PlanRunEvent(DateTimeOffset.UtcNow, 1, 1, RunEventKind.RungChanged, "standard", "hub", "climb", Rung: 2),
            Round(1, 2, 1),
            Round(1, 2, 2, "Passed")
        ];

        RepairPosition position = RepairLadder.PositionFrom(events);

        Assert.Equal(new RepairPosition(2, 1, 3), position);
        Assert.Equal(new RepairPosition(1, 0, 0), RepairLadder.PositionFrom([]));
    }

    [Fact]
    public void A_round_with_fewer_failing_names_than_any_before_it_is_not_charged_to_the_rung()
    {
        PlanRunEvent[] events =
        [
            Round(1, 1, 1, signature: "a\nb\nc\nd"), // the first round is always charged
            Round(1, 1, 2, signature: "a\nb\nc"),    // 3 < 4: progress, free
            Round(1, 1, 3, signature: "a\nb"),       // 2 < 3: progress, free
            Round(1, 1, 4, signature: "x\ny")        // 2 is not fewer than 2: charged
        ];

        Assert.Equal(new RepairPosition(1, 2, 4), RepairLadder.PositionFrom(events));
        Assert.Equal(2, RepairLadder.BestFailing(events));
    }

    [Fact]
    public void A_worse_round_and_a_round_whose_failures_cannot_be_counted_are_charged()
    {
        PlanRunEvent[] events =
        [
            Round(1, 1, 1, signature: "a\nb\nc"),
            Round(1, 1, 2, signature: "a\nb\nc\nd"),   // worse
            Round(1, 1, 3, signature: "sha256:0123abcd"), // a hash of the output: nothing to count
            Round(1, 1, 4)                                 // no signature at all
        ];

        Assert.Equal(new RepairPosition(1, 4, 4), RepairLadder.PositionFrom(events));
        Assert.Equal(3, RepairLadder.BestFailing(events));
    }

    [Fact]
    public void A_climb_resets_the_rung_but_the_best_so_far_stays_the_bar_to_beat()
    {
        PlanRunEvent[] events =
        [
            Round(1, 1, 1, signature: "a\nb\nc\nd"),
            new PlanRunEvent(DateTimeOffset.UtcNow, 1, 1, RunEventKind.RungChanged, "standard", "hub", "climb", Rung: 2),
            Round(1, 2, 1, signature: "a\nb\nc"),  // better than the best before the climb: free
            Round(1, 2, 2, signature: "a\nb\nc")   // equal: charged
        ];

        Assert.Equal(new RepairPosition(2, 1, 3), RepairLadder.PositionFrom(events));
    }

    [Theory]
    [InlineData(3, 4, true)]
    [InlineData(4, 4, false)]
    [InlineData(5, 4, false)]
    [InlineData(null, 4, false)]
    [InlineData(3, null, false)]
    [InlineData(null, null, false)]
    public void Only_fewer_failing_names_than_the_best_so_far_is_progress(int? failing, int? best, bool progress)
    {
        Assert.Equal(progress, RepairLadder.Improved(failing, best));
    }

    private static PlanRecord PlanWithOneStep(out PlanStep step)
    {
        step = new PlanStep(1, "fix it", "detail", ["a.cs", "test/a.test.ts"], "npm test", "standard", StepStatus.Running, null, 1, null, null);
        return new PlanRecord("0123456789abcdef0123456789abcdef", "t", "Fix the widget", PlanStatus.Running, null, [], [], [], null, null,
            [step], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
    }

    [Fact]
    public void A_follow_up_ends_by_telling_the_model_to_act_before_explaining()
    {
        PlanRecord plan = PlanWithOneStep(out PlanStep step);

        string message = RepairMessages.Build(plan, step, "FAILED widget::a", 2, previousFailing: [], currentFailing: ["widget::a"],
            sameOutput: false, changedFiles: null);

        Assert.Contains(PlanRunner.ActFirst, message);
        Assert.True(message.IndexOf(PlanRunner.ActFirst, StringComparison.Ordinal) > message.IndexOf("FAILED widget::a", StringComparison.Ordinal),
            "the instruction comes after the failure it is about");
    }

    [Fact]
    public void A_follow_up_says_what_was_fixed_what_is_new_and_what_files_changed_without_repeating_the_task()
    {
        PlanRecord plan = PlanWithOneStep(out PlanStep step);

        string message = RepairMessages.Build(plan, step, "FAILED widget::b\nFAILED widget::c", 3,
            previousFailing: ["widget::a", "widget::b"], currentFailing: ["widget::b", "widget::c"], sameOutput: false,
            changedFiles: ["a.cs"]);

        Assert.Contains("Round 3: the approved check did not pass yet", message);
        Assert.Contains("`npm test`", message);
        Assert.Contains("fixed since the round before: widget::a", message);
        Assert.Contains("new failures: widget::c", message);
        Assert.Contains("still failing: widget::b", message);
        Assert.Contains("files you changed in the last round: a.cs", message);
        Assert.Contains("This step writes its own test, so the test can be the mistake", message);
        Assert.DoesNotContain("Overall goal", message);
        Assert.DoesNotContain("You are carrying out ONE step", message);
        Assert.Contains($"[plan:{plan.Id}]", message);
    }

    [Fact]
    public void A_round_that_changed_nothing_is_told_so()
    {
        PlanRecord plan = PlanWithOneStep(out PlanStep step);

        string message = RepairMessages.Build(plan, step, "Exit code: 1\nboom", 2, [], [], sameOutput: true, changedFiles: [],
            handover: "A more capable model (the hub) has taken over this conversation.");

        Assert.StartsWith("A more capable model (the hub) has taken over this conversation.", message);
        Assert.Contains("the check printed the same as after the round before", message);
        Assert.Contains("you changed no file in the last round", message);
    }

    [Theory]
    [InlineData(true, 6, true, 9, true)]
    [InlineData(true, 6, true, 6, false)]
    [InlineData(true, 6, true, 3, false)]
    [InlineData(true, 6, false, 1, true)]
    [InlineData(false, 3, true, 9, false)]
    [InlineData(false, 3, false, 5, true)]
    public void A_round_is_worse_when_the_code_stops_building_or_more_names_fail(bool bestBuilds, int bestFailures, bool builds, int failures, bool worse) =>
        Assert.Equal(worse, new RoundScore(builds, failures).IsWorseThan(new RoundScore(bestBuilds, bestFailures)));

    [Fact]
    public void A_score_comes_from_the_names_in_the_check_output_and_a_hash_of_it_has_none()
    {
        string tests = "Exit code: 1\nFAILED a\nFAILED b";
        Assert.Equal(new RoundScore(true, 2), RoundScore.Of(PlanFailure.FailureSignature(tests), tests));

        string build = "Exit code: 1\nsrc/a.ts(1,1): error TS2322: nope\nsrc/b.ts(2,2): error TS2345: nope";
        Assert.Equal(new RoundScore(false, 2), RoundScore.Of(PlanFailure.FailureSignature(build), build));

        string syntax = "Exit code: 1\nFAILED parses\nSyntaxError: Unexpected token";
        Assert.Equal(new RoundScore(false, 1), RoundScore.Of(PlanFailure.FailureSignature(syntax), syntax));

        string unnamed = "Exit code: 1\nsomething went wrong in a way no name captures";
        Assert.Null(RoundScore.Of(PlanFailure.FailureSignature(unnamed), unnamed));
    }

    [Fact]
    public void An_undone_round_is_said_in_place_of_the_comparison_with_the_round_before()
    {
        PlanRecord plan = PlanWithOneStep(out PlanStep step);
        var rollback = new RollbackNote(3, new RoundScore(true, 9), 2, new RoundScore(true, 6), ["widget::b", "widget::c"], ["src/a.ts"]);

        string message = RepairMessages.Build(plan, step, "FAILED widget::a", 4, ["widget::a"], ["widget::a"], sameOutput: false,
            changedFiles: ["src/a.ts"], rollback: rollback);

        Assert.Contains("Round 3 made the check worse, so the fleet undid it: it left 9 failing where round 2 had 6 failing. It broke: widget::b, widget::c.", message);
        Assert.Contains("back as they were after round 2 (src/a.ts)", message);
        Assert.DoesNotContain("Since your last round", message);
        Assert.DoesNotContain("files you changed in the last round", message);
    }

    [Theory]
    [InlineData("package-lock.json", true)]
    [InlineData("web/package-lock.json", true)]
    [InlineData("yarn.lock", true)]
    [InlineData("Cargo.lock", true)]
    [InlineData("pnpm-lock.yaml", true)]
    [InlineData("tsconfig.tsbuildinfo", true)]
    [InlineData("debug.log", true)]
    [InlineData("src/server/marketHours.ts", false)]
    [InlineData("package.json", false)]
    [InlineData("docs/lockfile.md", false)]
    public void Lock_files_and_build_caches_are_not_progress(string path, bool incidental) =>
        Assert.Equal(incidental, RepairLadder.IsIncidentalFile(path));

    [Fact]
    public void Failing_names_are_read_back_from_a_stored_signature_and_a_hash_has_none()
    {
        Assert.Equal(["widget::a", "widget::b"], RepairMessages.FailingNames("widget::a\nwidget::b"));
        Assert.Empty(RepairMessages.FailingNames("sha256:abcdef"));
        Assert.Empty(RepairMessages.FailingNames(null));
    }

    [Fact]
    public void The_brief_lists_the_rounds_oldest_first_with_the_machine_the_files_and_what_the_model_said()
    {
        PlanRunEvent[] events =
        [
            new(DateTimeOffset.UtcNow, 1, 1, RunEventKind.AttemptEnded, "standard", "worker-a", "Model finished after 12 s: Added the guard.", "worker-a", Rung: 1, Round: 1),
            Round(1, 1, 1, signature: "widget::a\nwidget::b", files: ["src/a.ts", "test/a.test.ts"]),
            Round(1, 1, 2, "NoOp", signature: "widget::a\nwidget::b", node: "hub")
        ];

        string brief = RepairMessages.Brief(events);

        Assert.Contains("- Round 1 on worker-a: changed src/a.ts, test/a.test.ts; the check then failed on widget::a, widget::b. It said: \"Added the guard.\"", brief);
        Assert.Contains("- Round 2 on hub: changed no file; the check then failed on widget::a, widget::b.", brief);
        Assert.Contains("Files changed so far: src/a.ts, test/a.test.ts.", brief);
        Assert.EndsWith("before you edit anything.\n", brief.Replace("\r\n", "\n"));
        Assert.Equal(string.Empty, RepairMessages.Brief([Round(1, 1, null, "Infra")]));
    }
}

public sealed class FleetStepSessionTests
{
    private sealed class ScriptedChatClient(Func<int, Exception?> failure, Func<int, string>? reply = null) : IChatClient
    {
        private int _calls;

        public List<ChatMessage[]> Requests { get; } = [];

        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int call = ++_calls;
            Requests.Add(messages.ToArray());
            Options.Add(options);
            if (failure(call) is { } exception)
            {
                throw exception;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply?.Invoke(call) ?? $"answer {call}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static FleetStepAgent Agent(ScriptedChatClient client) =>
        new(new ChatClientAgent(client, instructions: "You are a test model.", name: "test"));

    private static string TextOf(ChatMessage message) => message.Text ?? string.Empty;

    [Fact]
    public async Task A_second_message_reaches_the_model_with_the_first_exchange_before_it_and_the_machine_it_names()
    {
        var client = new ScriptedChatClient(_ => null);
        // A role session: this test is about what the conversation holds, and a step's session would also be nudged when the
        // scripted answer calls no tool.
        IStepSession session = Agent(client).OpenSession("probe");

        Assert.False(session.HasHistory);
        await session.SendAsync("the whole task", "standard", "worker-a", default);
        Assert.True(session.HasHistory);
        await session.SendAsync("the check failed", "standard", "hub", default);

        Assert.Equal(2, client.Requests.Count);
        string[] second = client.Requests[1].Select(TextOf).ToArray();
        Assert.Contains("the whole task", second);
        Assert.Contains("answer 1", second);
        Assert.Contains("the check failed", second);
        Assert.Equal("worker-a", client.Options[0]!.AdditionalProperties![FleetRoutingChatClient.RunnerMachineKey]);
        Assert.Equal("hub", client.Options[1]!.AdditionalProperties![FleetRoutingChatClient.RunnerMachineKey]);
        Assert.Equal("standard", client.Options[1]!.AdditionalProperties![FleetRoutingChatClient.RunnerTierKey]);
    }

    [Fact]
    public async Task A_message_that_failed_left_nothing_in_the_conversation_so_the_next_one_has_to_be_the_whole_task()
    {
        var client = new ScriptedChatClient(call => call == 1 ? new HttpRequestException("the worker went away") : null);
        IStepSession session = Agent(client).OpenSession();

        await Assert.ThrowsAsync<HttpRequestException>(() => session.SendAsync("the whole task", "standard", "worker-a", default));
        Assert.False(session.HasHistory);
        await session.SendAsync("the whole task again", "standard", "worker-a", default);

        string[] retried = client.Requests[1].Select(TextOf).ToArray();
        Assert.Contains("the whole task again", retried);
        Assert.DoesNotContain("the whole task", retried);
        Assert.True(session.HasHistory);
    }

    [Fact]
    public async Task A_conversation_with_a_role_carries_it_to_the_router_and_is_not_nudged_to_make_a_change()
    {
        var client = new ScriptedChatClient(_ => null, _ => string.Empty);
        IStepSession session = Agent(client).OpenSession(PlanRunnerToolPolicy.CheckAuditRole);

        StepAgentReply reply = await session.SendAsync("audit this check", "heavy", "hub", default);

        // An answer in words, or none, is a valid audit: there is nothing to finish, so nothing asks for a change.
        Assert.Single(client.Requests);
        Assert.Equal(string.Empty, reply.Text);
        Assert.Equal(PlanRunnerToolPolicy.CheckAuditRole, client.Options[0]!.AdditionalProperties![FleetRoutingChatClient.RunnerRoleKey]);
        Assert.Equal("heavy", client.Options[0]!.AdditionalProperties![FleetRoutingChatClient.RunnerTierKey]);
        Assert.Equal("hub", client.Options[0]!.AdditionalProperties![FleetRoutingChatClient.RunnerMachineKey]);

        // A conversation that carries out a step has no role.
        var stepClient = new ScriptedChatClient(_ => null);
        await Agent(stepClient).OpenSession().SendAsync("the whole task", "standard", null, default);
        Assert.False(stepClient.Options[0]!.AdditionalProperties!.ContainsKey(FleetRoutingChatClient.RunnerRoleKey));
    }

    [Fact]
    public async Task The_tool_calls_of_a_turn_come_back_with_their_arguments_as_text()
    {
        var client = new CallingChatClient(
            new FunctionCallContent("c1", "propose_check", new Dictionary<string, object?> { ["check"] = "npm run build", ["why"] = "dev never exits" }),
            new FunctionCallContent("c2", "report_blocker", new Dictionary<string, object?>
            {
                ["kind"] = System.Text.Json.JsonDocument.Parse("\"environment\"").RootElement,
                ["evidence"] = System.Text.Json.JsonDocument.Parse("{\"a\":1}").RootElement
            }),
            new FunctionCallContent("c3", "edit_file", null));
        IStepSession session = new FleetStepAgent(new ChatClientAgent(client, instructions: "test", name: "test")).OpenSession();

        StepAgentReply reply = await session.SendAsync("the whole task", "standard", null, default);

        Assert.Equal(3, reply.ToolCalls);
        Assert.NotNull(reply.Calls);
        Assert.Equal(["propose_check", "report_blocker", "edit_file"], reply.Calls.Select(call => call.Name));
        Assert.Equal("npm run build", reply.Calls[0].Arguments["check"]);
        Assert.Equal("environment", reply.Calls[1].Arguments["kind"]);
        Assert.Equal("{\"a\":1}", reply.Calls[1].Arguments["evidence"]);
        Assert.Empty(reply.Calls[2].Arguments);
        Assert.Equal("npm run build", CheckAudit.ProposalFrom(reply)!.Value.Check);
        Assert.Equal("environment", CheckAudit.BlockerFrom(reply)!.Kind);
    }

    private sealed class CallingChatClient(params AIContent[] contents) : IChatClient
    {
        private int _sent;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            // The tools are not registered with the agent, so the invocation loop asks once more; a model answers in words then.
            Task.FromResult(new ChatResponse(_sent++ == 0 ? new ChatMessage(ChatRole.Assistant, [.. contents]) : new ChatMessage(ChatRole.Assistant, "done")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task An_empty_reply_is_nudged_once_inside_the_same_conversation()
    {
        var client = new ScriptedChatClient(_ => null, call => call == 1 ? string.Empty : "done");
        IStepSession session = Agent(client).OpenSession();

        StepAgentReply reply = await session.SendAsync("the whole task", "standard", null, default);

        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(FleetStepAgent.Nudge, client.Requests[1].Select(TextOf));
        Assert.Contains("the whole task", client.Requests[1].Select(TextOf));
        Assert.Equal("done", reply.Text);
    }
}
