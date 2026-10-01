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
        FleetHealthMonitor? health = null, int roundsPerRung = RepairLadder.DefaultRoundsPerRung) =>
        new(Store, RepairTools(verify), agent, NullLogger.Instance, fleetOptions: options, healthMonitor: health,
            transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask, roundsPerRung: roundsPerRung);

    private PlanRecord RepairPlan(string? machine, string scope)
    {
        PlanRecord plan = Store.Create("Repair", "Fix the widget", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [Step("fix the widget", "dotnet build", "standard")]);
        if (machine is not null)
        {
            plan = Store.SelectMachine(plan.Id, 1, machine, out string? error)!;
            Assert.Null(error);
        }

        return Store.Approve(plan.Id, recoveryScope: scope)!;
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
        Assert.Equal(StepStatus.Failed, after.Steps[0].Status);
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
        ]).Id, recoveryScope: PlanRecoveryScope.WorkerOnly)!;
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

    private static PlanRecord PlanWithOneStep(out PlanStep step)
    {
        step = new PlanStep(1, "fix it", "detail", ["a.cs", "test/a.test.ts"], "npm test", "standard", StepStatus.Running, null, 1, null, null);
        return new PlanRecord("0123456789abcdef0123456789abcdef", "t", "Fix the widget", PlanStatus.Running, null, [], [], [], null, null,
            [step], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
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
        IStepSession session = Agent(client).OpenSession();

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
