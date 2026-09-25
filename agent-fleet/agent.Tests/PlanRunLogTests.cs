using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class PlanRunLogTests : PlanTestBase
{
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    private PlanRunner Runner(FakeStepAgent agent, Func<string, string> verify) =>
        new(Store, new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(verify(command))), agent, NullLogger.Instance);

    private static string Pass(string _) => "Exit code: 0\n--- stdout ---\nok";

    private static string Fail(string _) => "Exit code: 1\n--- stdout ---\nAssertionError: expected 3 got 2";

    private const string GitStatus = "git status --short";

    // A project that is a git repository with two changed files; every other command passes.
    private static string PassInRepo(string command) =>
        command == GitStatus ? "Exit code: 0\n--- stdout ---\n M src/Login.cs\n?? src/RateLimiter.cs\n" : Pass(command);

    // Not a repository: git exits with an error, every other command passes.
    private static string PassOutsideRepo(string command) =>
        command == GitStatus ? "Exit code: 128\n--- stderr ---\nfatal: not a git repository" : Pass(command);

    private PlanRecord Approved(params PlanStepInput[] steps) => Store.Approve(NewPlan(steps).Id)!;

    private static FakeStepAgent Agent(string reply = "Wrote it.\n\n_— via worker1_") => new((_, _, _) => Task.FromResult(reply));

    [Fact]
    public async Task A_passing_run_records_start_each_attempt_its_check_and_the_end()
    {
        PlanRecord plan = Approved(Step("first", tier: "light"), Step("second", tier: "heavy"));

        await Runner(Agent(), PassOutsideRepo).RunPlanAsync(plan.Id, default);

        IReadOnlyList<PlanRunEvent> events = Store.Get(plan.Id)!.Events!;
        Assert.Equal(
            [
                RunEventKind.RunStarted,
                RunEventKind.AttemptStarted, RunEventKind.AttemptEnded, RunEventKind.CheckPassed, RunEventKind.StepDone,
                RunEventKind.AttemptStarted, RunEventKind.AttemptEnded, RunEventKind.CheckPassed, RunEventKind.StepDone,
                RunEventKind.PlanDone
            ],
            events.Select(e => e.Kind));
        Assert.Equal([null, 1, 1, 1, 1, 2, 2, 2, 2, null], events.Select(e => e.StepId));
        Assert.All(events.Where(e => e.Kind == RunEventKind.AttemptEnded), e => Assert.Equal("worker1", e.Node));
        Assert.Contains("Wrote it.", events.First(e => e.Kind == RunEventKind.AttemptEnded).Detail);
    }

    [Fact]
    public async Task When_the_project_is_a_git_repository_the_run_ends_with_the_files_it_changed()
    {
        PlanRecord plan = Approved(Step("only"));

        await Runner(Agent(), PassInRepo).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        PlanRunEvent last = after.Events![^1];
        Assert.Equal(RunEventKind.FilesChanged, last.Kind);
        Assert.Contains("M src/Login.cs", last.Detail);
        Assert.Contains("?? src/RateLimiter.cs", last.Detail);
        string report = FleetPlanStore.ToReportMarkdown(after);
        Assert.Contains("## Files changed (git status)", report);
        Assert.Contains("src/RateLimiter.cs", report);
    }

    [Fact]
    public async Task A_blocked_run_also_lists_what_it_left_behind()
    {
        PlanRecord plan = Approved(Step("hard one"));

        await Runner(Agent(), command => command == GitStatus ? PassInRepo(command) : Fail(command)).RunPlanAsync(plan.Id, default);

        IReadOnlyList<PlanRunEvent> events = Store.Get(plan.Id)!.Events!;
        Assert.Equal(RunEventKind.PlanBlocked, events[^2].Kind);
        Assert.Equal(RunEventKind.FilesChanged, events[^1].Kind);
    }

    [Fact]
    public async Task A_clean_repository_says_so_instead_of_staying_silent()
    {
        PlanRecord plan = Approved(Step("only"));

        await Runner(Agent(), command => command == GitStatus ? "Exit code: 0\n--- stdout ---\n" : Pass(command)).RunPlanAsync(plan.Id, default);

        Assert.Contains("no changed files", Store.Get(plan.Id)!.Events![^1].Detail);
    }

    [Fact]
    public async Task A_long_file_list_is_capped_with_a_count_of_the_rest()
    {
        var tools = new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(
            "Exit code: 0\n--- stdout ---\n" + string.Join('\n', Enumerable.Range(1, 100).Select(i => $"?? file{i}.txt"))));

        string? list = await tools.ChangedFilesAsync(@"C:\work", default);

        Assert.Contains("?? file60.txt", list);
        Assert.DoesNotContain("file61.txt", list);
        Assert.EndsWith("... and 40 more", list);
    }

    [Fact]
    public async Task No_working_directory_means_nothing_to_list()
    {
        var tools = new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(Pass(command)));

        Assert.Null(await tools.ChangedFilesAsync(null, default));
    }

    [Fact]
    public async Task A_failing_check_is_logged_with_its_output_and_the_tier_of_each_attempt()
    {
        PlanRecord plan = Approved(Step("hard", tier: "standard"));
        int checks = 0;

        await Runner(Agent(), command => ++checks < 3 ? Fail(command) : Pass(command)).RunPlanAsync(plan.Id, default);

        IReadOnlyList<PlanRunEvent> events = Store.Get(plan.Id)!.Events!;
        PlanRunEvent[] failures = events.Where(e => e.Kind == RunEventKind.CheckFailed).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, e => Assert.Contains("expected 3 got 2", e.Detail));
        Assert.Equal(["standard", "standard", "heavy"], events.Where(e => e.Kind == RunEventKind.AttemptStarted).Select(e => e.Tier));
    }

    [Fact]
    public async Task A_blocked_plan_says_where_and_the_report_quotes_the_last_failing_output()
    {
        PlanRecord plan = Approved(Step("hard one"));

        await Runner(Agent(), Fail).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Contains(after.Events!, e => e.Kind == RunEventKind.PlanBlocked && e.StepId == 1);
        string report = FleetPlanStore.ToReportMarkdown(after);
        Assert.Contains("Status: **blocked**", report);
        Assert.Contains("## Where it stopped", report);
        Assert.Contains("expected 3 got 2", report);
        Assert.Contains("## Timeline", report);
        Assert.Contains("Answered by: worker1", report);
    }

    [Fact]
    public async Task A_model_failure_and_a_timeout_are_logged_as_such()
    {
        PlanRecord plan = Approved(Step("flaky"));
        int calls = 0;
        var agent = new FakeStepAgent(async (_, _, token) =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("connection reset");
            }

            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        });
        var runner = new PlanRunner(
            Store,
            new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(Pass(command))),
            agent,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(150),
            transientDelay: _ => TimeSpan.Zero);

        await runner.RunPlanAsync(plan.Id, default);

        IReadOnlyList<PlanRunEvent> events = Store.Get(plan.Id)!.Events!;
        Assert.Contains(events, e => e.Kind == RunEventKind.ModelFailed && e.Detail.Contains("connection reset"));
        Assert.Contains(events, e => e.Kind == RunEventKind.Waiting);
        Assert.Contains(events, e => e.Kind == RunEventKind.TimedOut);
    }

    [Fact]
    public async Task A_user_stop_is_logged()
    {
        PlanRecord plan = Approved(Step("slow"));
        var started = new TaskCompletionSource();
        var agent = new FakeStepAgent(async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        });
        PlanRunner runner = new(Store, new PlanTools(Store, Valid, (command, _, _) => Task.FromResult(Pass(command))), agent, NullLogger.Instance);

        Task run = runner.RunPlanAsync(plan.Id, default);
        await started.Task;
        Assert.True(runner.Stop(plan.Id));
        await run;

        Assert.Contains(Store.Get(plan.Id)!.Events!, e => e.Kind == RunEventKind.Stopped);
    }

    [Fact]
    public void The_log_is_bounded_and_keeps_the_first_line_and_the_latest()
    {
        PlanRecord plan = Approved(Step("one"));

        for (int i = 0; i < FleetPlanStore.MaxEvents + 50; i++)
        {
            Store.AddEvent(plan.Id, 1, i, RunEventKind.AttemptEnded, detail: "line " + i);
        }

        IReadOnlyList<PlanRunEvent> events = Store.Get(plan.Id)!.Events!;
        Assert.Equal(FleetPlanStore.MaxEvents, events.Count);
        Assert.Equal("line 0", events[0].Detail);
        Assert.Equal($"line {FleetPlanStore.MaxEvents + 49}", events[^1].Detail);
    }

    [Fact]
    public void A_long_detail_is_cut_so_a_huge_test_output_cannot_bloat_the_file()
    {
        PlanRecord plan = Approved(Step("one"));

        Store.AddEvent(plan.Id, 1, 1, RunEventKind.CheckFailed, detail: new string('x', 20_000));

        Assert.True(Store.Get(plan.Id)!.Events![0].Detail.Length <= FleetPlanStore.MaxEventDetailCharacters + 3);
    }

    [Fact]
    public void A_plan_saved_before_the_log_existed_still_loads_and_reports()
    {
        PlanRecord plan = Approved(Step("one"));
        string path = Path.Combine(PlansDirectory, plan.Id + ".json");
        // A file written by an older build simply has no "events" property (it is the last one).
        string legacy = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), ",\\s*\"events\": null", string.Empty);
        Assert.DoesNotContain("events", legacy);
        File.WriteAllText(path, legacy);

        PlanRecord reloaded = Store.Get(plan.Id)!;

        Assert.Null(reloaded.Events);
        Assert.Contains("Nothing recorded yet.", FleetPlanStore.ToReportMarkdown(reloaded));
    }

    [Fact]
    public void The_router_footer_names_the_machine()
    {
        Assert.Equal("worker1", PlanRunner.ViaNode("Done.\n\n_— via worker1_"));
        Assert.Null(PlanRunner.ViaNode("Done."));
    }
}
