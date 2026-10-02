using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// Whole nights, named after the failures that really stopped approved plans: a model server that is down for hours, a
/// worker and a hub that are both gone, a backend that dies in the middle of a round, and one plan that has all of them
/// at once. Each runs the real plan runner against a fake model and a fake clock, so a night takes milliseconds. The
/// single faults have their own tests next to the code that handles them; these check that the pieces work together and
/// that nothing a person has to look at in the morning is missing.
/// </summary>
public sealed partial class PlanRunnerTests
{
    // The runner's own spacing for an outage: 30 seconds doubling to 8 minutes, then every 15 minutes.
    private static TimeSpan OutageSpacing(int wait) => wait switch
    {
        0 => TimeSpan.FromSeconds(30),
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(4),
        4 => TimeSpan.FromMinutes(8),
        _ => TimeSpan.FromMinutes(15)
    };

    [Fact]
    public async Task A_model_server_that_answers_500_for_six_hours_and_comes_back_costs_no_attempt_and_no_strike()
    {
        PlanRecord plan = ApprovedPlan(Step("while the server is down"));
        DateTimeOffset start = DateTimeOffset.UtcNow;
        DateTimeOffset now = start;
        var agent = new FakeStepAgent((_, _, _) => now < start.AddHours(6)
            ? throw new HttpRequestException("Response status code does not indicate success: 500 (Internal Server Error).")
            : Task.FromResult("Done."));

        await Runner(agent, Pass, transientDelay: OutageSpacing, utcNow: () => now, delayAsync: (delay, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            now += delay;
            return Task.CompletedTask;
        }).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        // The night went by while it waited, inside the eight-hour deadline; the step was tried once for real.
        Assert.InRange((now - start).TotalHours, 6, 8);
        Assert.Equal(1, after.Steps[0].Attempts);
        PlanRunEvent[] waits = after.Events!.Where(e => e.Kind == RunEventKind.Waiting).ToArray();
        Assert.InRange(waits.Length, 20, 45);
        Assert.All(waits, wait => Assert.Contains("does not count as an attempt", wait.Detail));
        Assert.DoesNotContain(after.Events!, e => e.Kind is RunEventKind.CheckFailed or RunEventKind.StepParked or RunEventKind.PlanBlocked);
        Assert.DoesNotContain(after.Events!, e => RepairLadder.IsVerdictRound(e));
    }

    [Fact]
    public async Task When_the_worker_and_the_hub_are_both_down_the_run_waits_until_its_deadline_and_tells_the_user_once()
    {
        (FleetOptions options, FleetHealthMonitor health) = HubFleet();
        PlanRecord plan = RepairPlan("worker-a", PlanRecoveryScope.AllowHubRescue);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Store.Update(plan.Id, current => current with { RunDeadlineUtc = now.AddHours(1) });
        var agent = new FakeStepAgent((_, _, _) => throw new HttpRequestException("No connection could be made because the target machine actively refused it."));
        var notifier = new RecordingNotifier();

        await RepairRunner(agent, Pass, options, health, utcNow: () => now, notifier: notifier, transientDelay: _ => TimeSpan.FromMinutes(15),
            delayAsync: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                now += delay;
                return Task.CompletedTask;
            }).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        // Waiting is free: not one attempt was spent, and no check ever ran.
        Assert.Equal(0, after.Steps[0].Attempts);
        Assert.InRange(after.Events!.Count(e => e.Kind == RunEventKind.Waiting), 3, 12);
        Assert.Single(after.Events!, e => e.Kind == RunEventKind.RunDeadlineExceeded);
        Assert.DoesNotContain(after.Events!, e => e.Kind is RunEventKind.CheckFailed or RunEventKind.StepParked);
        Assert.Equal(["plan-deadline"], notifier.Notices.Select(notice => notice.Kind));
        Assert.Contains("run deadline", FleetPlanStore.ToReportMarkdown(after), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_backend_that_dies_in_the_middle_of_a_round_resumes_in_a_new_conversation_from_a_brief_and_keeps_the_rung_and_the_clock()
    {
        PlanRecord plan = Store.Approve(Store.Create("Restart", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("fix the widget", "Fix it", ["a.cs"], "dotnet build", "standard", RetrySafe: true)]).Id)!;
        using var killed = new CancellationTokenSource();
        int calls = 0;
        var firstRun = new FakeStepAgent(async (_, _, cancellationToken) =>
        {
            if (++calls == 1)
            {
                File.WriteAllText(SourceFile, "first try");
                return "Tried.";
            }

            // The process is killed here: the second round's model call never returns.
            killed.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return string.Empty;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RepairRunner(firstRun, _ => FailWith("widget::renders")).RunPlanAsync(plan.Id, killed.Token));

        // What the dead backend left: the plan still running, the step still running, one failed round in the log.
        PlanRecord left = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Running, left.Status);
        Assert.Equal(StepStatus.Running, left.Steps[0].Status);
        Assert.Equal(1, left.Steps[0].Attempts);
        PlanRunEvent firstRound = Assert.Single(left.Events!, e => e.Kind == RunEventKind.RoundClassified);
        Assert.NotNull(firstRound.DurationSeconds);

        var secondRun = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(SourceFile, "fixed");
            return Task.FromResult("Fixed.");
        });
        await RepairRunner(secondRun, _ => File.ReadAllText(SourceFile).Contains("fixed", StringComparison.Ordinal)
            ? Pass("check")
            : FailWith("widget::renders")).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        // The open conversation was lost with the process, so the new one starts from what the first one tried.
        (string Tier, string Prompt, string? Machine) restarted = Assert.Single(secondRun.Calls);
        Assert.Contains("A conversation before this one", restarted.Prompt);
        Assert.Contains("FAILED widget::renders", restarted.Prompt);
        Assert.Equal(2, after.Steps[0].Attempts);
        // It went on from round 2 of the same rung, not from a fresh ladder.
        PlanRunEvent resumed = after.Events!.Last(e => e.Kind == RunEventKind.RoundClassified);
        Assert.Equal((1, 2), (resumed.Rung, resumed.Round));
    }

    [Fact]
    public async Task The_night_with_every_fault_ends_partial_with_a_report_and_finishes_after_one_retry()
    {
        // Six steps. 1: the model server is down for three calls. 2: its check does not parse in the worker's shell.
        // 3: its check starts a server and never exits. 4: nothing the model does makes it pass. 5: needs only step 1.
        // 6: the final check, which needs everything.
        File.WriteAllText(Path.Combine(ProjectDirectory, "package.json"), """{"scripts":{"dev":"tsx watch src/index.ts","build":"tsc","test":"node --test"}}""");
        PlanStepInput Part(string title, string verify, params int[] dependsOn) =>
            new(title, "do " + title, [], verify, "standard", RetrySafe: true, DependsOn: dependsOn.Length > 0 ? dependsOn : null);
        PlanRecord plan = Store.Approve(Store.Create("The long night", "goal", ProjectDirectory, ["a"], ["q?"], ["r"], null, null,
        [
            Part("one", "check one"),
            Part("two", "npm run build && npm test"),
            Part("three", "npm run dev"),
            Part("four", "check four"),
            Part("five", "check five", 1),
            Part("six", "check six")
        ]).Id)!;

        int outageLeft = 3;
        int edits = 0;
        var agent = new FakeStepAgent((message, _, _) =>
        {
            if (message.Contains("This step (1 of 6): one", StringComparison.Ordinal) && outageLeft-- > 0)
            {
                throw new HttpRequestException("Response status code does not indicate success: 500 (Internal Server Error).");
            }

            File.WriteAllText(SourceFile, $"edit {++edits}");
            return Task.FromResult("Did the work.");
        })
        {
            Auditor = call => call.Message.Contains("`npm run dev`", StringComparison.Ordinal)
                ? FakeStepAgent.Proposes("npm run build", "npm run dev starts a server that never exits")
                : FakeStepAgent.Proposes("npm run build; if ($?) { npm test }", "Windows PowerShell 5.1 has no && operator")
        };
        bool fourIsFixed = false;
        string Verify(string command) => command switch
        {
            "npm run build && npm test" => ParserError,
            "check four" => fourIsFixed ? Pass(command) : FailWith("widget::renders"),
            _ => Pass(command)
        };
        var notifier = new RecordingNotifier();

        await RepairRunner(agent, Verify, roundsPerRung: 2, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord morning = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, morning.Status);
        Assert.Equal([StepStatus.Done, StepStatus.Done, StepStatus.Done, StepStatus.Parked, StepStatus.Done, StepStatus.Pending],
            morning.Steps.Select(step => step.Status));

        // The outage, the shell and the never-ending check cost no attempt: each of those steps was worked on once.
        Assert.Equal([1, 1, 1], new[] { morning.Steps[0].Attempts, morning.Steps[1].Attempts, morning.Steps[2].Attempts });
        Assert.Equal(1, morning.Steps[4].Attempts);
        Assert.Equal(3, morning.Events!.Count(e => e.Kind == RunEventKind.Waiting && e.StepId == 1));

        // Two checks were changed, each keeping what it was.
        PlanRunEvent[] healed = morning.Events!.Where(e => e.Kind == RunEventKind.CheckHealed).ToArray();
        Assert.Equal([2, 3], healed.Select(e => e.StepId));
        Assert.Equal("npm run build; if ($?) { npm test }", morning.Steps[1].Verify);
        Assert.Equal("npm run build && npm test", morning.Steps[1].OriginalVerify);
        Assert.Equal("npm run build", morning.Steps[2].Verify);
        Assert.Equal("npm run dev", morning.Steps[2].OriginalVerify);
        // The never-ending check was fixed before the step was started; the shell problem showed in the first check.
        List<PlanRunEvent> events = morning.Events!.ToList();
        Assert.True(events.FindIndex(e => e.Kind == RunEventKind.CheckHealed && e.StepId == 3) <
                    events.FindIndex(e => e.Kind == RunEventKind.AttemptStarted && e.StepId == 3));

        // The stuck step is parked with its reason, the independent step went on, and the last one waited.
        PlanRunEvent parked = Assert.Single(morning.Events!, e => e.Kind == RunEventKind.StepParked);
        Assert.Equal(4, parked.StepId);
        Assert.True(events.FindIndex(e => e.Kind == RunEventKind.StepParked) < events.FindIndex(e => e.Kind == RunEventKind.AttemptStarted && e.StepId == 5));
        Assert.Equal(["step-parked", "plan-needs-attention"], notifier.Notices.Select(notice => notice.Kind));

        // What a person reads in the morning.
        string report = FleetPlanStore.ToReportMarkdown(morning);
        Assert.Contains("## Where it stopped", report);
        Assert.Contains("Step 4, four", report);
        Assert.Contains("## What to do next", report);
        Assert.Contains("## Checks changed by Fleet", report);
        Assert.Contains("`npm run dev` became `npm run build`", report);
        Assert.Contains("4 of 6 steps done", report.Replace("1 of 6", "x"), StringComparison.Ordinal);
        Assert.Contains("1 parked or stopped, 1 waiting for them", report);
        Assert.Contains("Step 6 was not run", report);

        // The user fixes the cause and retries step 4 alone: it finishes, the last step runs, and nothing else is redone.
        fourIsFixed = true;
        Store.RetryStep(plan.Id, 4);
        Store.Approve(plan.Id, retryStopped: false);
        await RepairRunner(agent, Verify, roundsPerRung: 2, notifier: notifier).RunPlanAsync(plan.Id, default);

        PlanRecord done = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, done.Status);
        Assert.All(done.Steps, step => Assert.Equal(StepStatus.Done, step.Status));
        Assert.Equal(["step-parked", "plan-needs-attention", "plan-done"], notifier.Notices.Select(notice => notice.Kind));
        Assert.Equal(1, agent.Calls.Count(call => System.Text.RegularExpressions.Regex.IsMatch(call.Prompt, @"This step \(\d+ of \d+\): five\r?$", System.Text.RegularExpressions.RegexOptions.Multiline)));
        Assert.Equal(2, done.Events!.Count(e => e.Kind == RunEventKind.CheckHealed));
    }

    private sealed class DownWorkspaces : IWorkerWorkspaceManager
    {
        private readonly object _gate = new();

        public List<string> Staged { get; } = [];

        public Task<IWorkerWorkspaceSession> StageAsync(PlanRecord plan, PlanStep step, string machine, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Staged.Add(machine);
            }

            // Connection refused: it comes back at once, as it does when a machine is switched off.
            throw new System.Net.Sockets.SocketException(10061);
        }

        public Task<WorkerWorkspaceResume> ResumeInterruptedAsync(PlanRecord plan, PlanStep step, WorkerWorkspaceBaseline baseline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task When_every_worker_refuses_the_connection_the_step_waits_instead_of_moving_between_them_as_fast_as_they_refuse()
    {
        string project = Path.Combine(PlansDirectory, "two-workers");
        Directory.CreateDirectory(project);
        string keyPath = Path.Combine(project, "worker-key.pem");
        using (System.Security.Cryptography.RSA rsa = System.Security.Cryptography.RSA.Create(2048))
        {
            File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
        }

        FleetWorkerWorkspaceConfig Workspace() => new("127.0.0.1", "test-user", keyPath, "SHA256:synthetic-host-key", "/tmp/fleet-test/workspaces", "linux", Port: 1);
        FleetOptions options = OptionsWithNodes(
            new FleetNodeConfig("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true),
            new FleetNodeConfig("worker-a", "http://worker-a.example.test/v1", "m", "worker", "standard", Workspace: Workspace()),
            new FleetNodeConfig("worker-b", "http://worker-b.example.test/v1", "m", "worker", "standard", Workspace: Workspace()));
        var health = new FleetHealthMonitor(options, new HealthyNodeFactory());
        PlanRecord plan = Store.Approve(Store.Create("Two workers down", "goal", project, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("stage it", "Do it", [], "dotnet build", "standard", RetrySafe: true)]).Id)!;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Store.Update(plan.Id, current => current with { RunDeadlineUtc = now.AddHours(1) });
        var workspaces = new DownWorkspaces();
        var agent = new FakeStepAgent((_, _, _) => throw new InvalidOperationException("the model must not be asked: nothing was staged"));

        var runner = new PlanRunner(Store, new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(Pass(command))), agent, NullLogger.Instance,
            fleetOptions: options, healthMonitor: health, workerWorkspaces: workspaces, transientDelay: _ => TimeSpan.FromMinutes(15),
            delayAsync: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                now += delay;
                return Task.CompletedTask;
            }, utcNow: () => now);
        await runner.RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Blocked, after.Status);
        Assert.Equal(0, after.Steps[0].Attempts);
        Assert.Empty(agent.Calls);
        // It tried both workers, then waited, an hour's worth of quarter hours; it did not hammer them.
        Assert.Contains("worker-a", workspaces.Staged);
        Assert.Contains("worker-b", workspaces.Staged);
        Assert.InRange(workspaces.Staged.Count, 2, 20);
        Assert.InRange(after.Events!.Count(e => e.Kind == RunEventKind.Waiting), 3, 12);
        Assert.Single(after.Events!, e => e.Kind == RunEventKind.RunDeadlineExceeded);
    }
}
