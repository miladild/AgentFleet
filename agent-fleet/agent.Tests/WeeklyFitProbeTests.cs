using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class WeeklyFitProbeTests : IDisposable
{
    private static readonly FleetNodeConfig HubNode = new("hub", "http://127.0.0.1:11434/v1", "model-x", "hub", "heavy", Fallback: true);
    private static readonly FleetNodeConfig WorkerA = new("worker-a", "http://192.0.2.10:11434/v1", "model-x", "worker", "standard");
    private static readonly FleetNodeConfig WorkerB = new("worker-b", "http://192.0.2.11:11434/v1", "model-x", "worker", "light");
    private static readonly FleetNodeConfig VisionNode = new("vision", "http://192.0.2.12:11434/v1", "model-x", "vision", Vision: true);
    private static readonly FleetNodeConfig OpenAiNode = new("compatible", "http://192.0.2.13:11434/v1", "model-x", "worker", Api: "openai");
    private static readonly FleetWeeklyProbeConfig SundayMornings = new(true, "sunday", 2, 4);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-weekly-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _configuration;
    private readonly FleetConfigStore _configStore;
    private readonly FleetOptions _options;
    private readonly FleetPlanStore _plans;
    private readonly MeasuringMachines _measuringMachines = new();
    private readonly FakeWeeklyApi _api = new();

    public WeeklyFitProbeTests()
    {
        Directory.CreateDirectory(_directory);
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLEET_CONFIG_PATH"] = Path.Combine(_directory, "fleet.config.json"),
            ["FLEET_PLANS_DIR"] = Path.Combine(_directory, "plans"),
            ["FLEET_LAB_DIR"] = Path.Combine(_directory, "lab")
        }).Build();
        _configStore = new FleetConfigStore(_configuration);
        _configStore.Save(_configStore.Current with { Nodes = [HubNode, WorkerA, WorkerB, VisionNode, OpenAiNode] });
        _options = FleetOptions.Load(_configuration, _configStore);
        _plans = new FleetPlanStore(_configuration);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string StatePath => Path.Combine(_directory, "lab", "weekly-fit-state.json");

    [Fact]
    public void The_test_calendar_has_the_days_the_tests_name()
    {
        Assert.Equal(DayOfWeek.Sunday, SundayAt(0).DayOfWeek);
        Assert.Equal(DayOfWeek.Saturday, SundayAt(0).AddDays(-1).DayOfWeek);
    }

    [Theory]
    [InlineData("2026-10-18T01:59", false)]
    [InlineData("2026-10-18T02:00", true)]
    [InlineData("2026-10-18T05:59", true)]
    [InlineData("2026-10-18T06:00", false)]
    [InlineData("2026-10-17T03:00", false)]
    [InlineData("2026-10-19T03:00", false)]
    public void The_window_opens_on_its_day_at_its_hour_and_closes_after_its_length(string local, bool open) =>
        Assert.Equal(open, WeeklyFitWindow.IsOpen(DateTime.Parse(local, CultureInfo.InvariantCulture), SundayMornings));

    [Theory]
    [InlineData("2026-10-18T21:59", false)]
    [InlineData("2026-10-18T22:00", true)]
    [InlineData("2026-10-18T23:30", true)]
    [InlineData("2026-10-19T00:30", true)]
    [InlineData("2026-10-19T01:59", true)]
    [InlineData("2026-10-19T02:00", false)]
    public void A_window_that_runs_past_midnight_stays_open_into_the_next_day(string local, bool open) =>
        Assert.Equal(open, WeeklyFitWindow.IsOpen(
            DateTime.Parse(local, CultureInfo.InvariantCulture), new FleetWeeklyProbeConfig(true, "sunday", 22, 4)));

    [Theory]
    [InlineData("2026-10-17T22:59", false)]
    [InlineData("2026-10-17T23:00", true)]
    [InlineData("2026-10-17T23:30", true)]
    [InlineData("2026-10-18T00:30", true)]
    [InlineData("2026-10-18T01:59", true)]
    [InlineData("2026-10-18T02:00", false)]
    public void A_saturday_window_runs_on_into_sunday(string local, bool open) =>
        Assert.Equal(open, WeeklyFitWindow.IsOpen(
            DateTime.Parse(local, CultureInfo.InvariantCulture), new FleetWeeklyProbeConfig(true, "saturday", 23, 3)));

    [Theory]
    [InlineData("2026-10-18T01:00", "2026-10-18T02:00")]
    [InlineData("2026-10-18T02:30", "2026-10-25T02:00")]
    [InlineData("2026-10-20T10:00", "2026-10-25T02:00")]
    public void The_next_opening_is_always_later_than_now(string now, string expected) =>
        Assert.Equal(
            DateTime.Parse(expected, CultureInfo.InvariantCulture),
            WeeklyFitWindow.NextOpening(DateTime.Parse(now, CultureInfo.InvariantCulture), SundayMornings));

    [Fact]
    public void On_the_night_the_clocks_go_back_the_window_is_shown_at_its_first_two_oclock()
    {
        // On 2026-10-25 the clocks go back at 03:00 summer time, so 02:00 happens twice. The window opens at the first one.
        TimeZoneInfo central = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        Configure();
        UseNodes(HubNode, WorkerA);

        WeeklyFitStatus status = Weekly(Fit(), Clock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), central)).Status();

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), status.NextWindowStartUtc);
    }

    [Fact]
    public async Task The_window_follows_the_machine_clock_not_utc()
    {
        // 00:30 UTC on a Sunday is 02:30 at UTC+2, inside the window. In UTC it is still before the window opens.
        TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "Plus two", "Plus two");
        Configure();
        UseNodes(HubNode, WorkerA);
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, Clock(SundayAt(0, 30), plusTwo));

        FitProbeJobSnapshot started = await RunWeeklyAsync(weekly, fit);

        Assert.Equal("worker-a", started.Node);
    }

    [Fact]
    public async Task Nothing_starts_once_the_window_has_closed()
    {
        Configure();
        UseNodes(HubNode, WorkerA);
        FitProbeService fit = Fit();

        await Weekly(fit, Clock(SundayAt(6, 0))).TickAsync(CancellationToken.None);

        Assert.Empty(fit.List());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Nothing_starts_once_the_window_has_closed_even_when_checking_an_earlier_machine_runs_past_its_end()
    {
        Configure();
        UseNodes(HubNode, WorkerA, WorkerB);
        WindowClock clock = Clock(SundayAt(5, 59).AddSeconds(50));
        _api.OnPs = () => clock.UtcNow = SundayAt(6, 0).AddSeconds(10);
        FitProbeService fit = Fit();

        await Weekly(fit, clock).TickAsync(CancellationToken.None);

        Assert.Empty(fit.List());
    }

    [Fact]
    public async Task Only_workers_not_measured_in_the_last_six_days_are_measured()
    {
        Configure();
        UseNodes(HubNode, WorkerA, WorkerB, VisionNode, OpenAiNode);
        WindowClock clock = Clock(SundayAt(2, 30));
        SeedHistory("worker-a", "model-x", clock.UtcNow.AddDays(-2));
        FitProbeService fit = Fit();

        FitProbeJobSnapshot started = await RunWeeklyAsync(Weekly(fit, clock), fit);

        Assert.Equal("worker-b", started.Node);
        Assert.All(_api.Requests, request => Assert.Equal("192.0.2.11", request.Host));
    }

    [Fact]
    public async Task The_hub_and_the_other_non_workers_are_never_contacted()
    {
        Configure();
        UseNodes(HubNode, VisionNode, OpenAiNode, WorkerA);
        WindowClock clock = Clock(SundayAt(2, 30));
        SeedHistory("worker-a", "model-x", clock.UtcNow.AddDays(-1));
        FitProbeService fit = Fit();

        await Weekly(fit, clock).TickAsync(CancellationToken.None);

        Assert.Empty(fit.List());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task The_plan_fallback_node_is_the_hub_even_under_another_name()
    {
        Configure();
        FleetNodeConfig fallbackBox = new("heavy-box", "http://192.0.2.20:11434/v1", "model-x", "heavy", "heavy", Fallback: true);
        UseNodes(fallbackBox, WorkerA);
        WindowClock clock = Clock(SundayAt(2, 30));
        SeedHistory("worker-a", "model-x", clock.UtcNow.AddDays(-1));
        FitProbeService fit = Fit();

        await Weekly(fit, clock).TickAsync(CancellationToken.None);

        Assert.Empty(fit.List());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task This_computer_is_never_measured_even_when_it_is_not_the_hub()
    {
        Configure();
        FleetNodeConfig thisComputer = new("workstation", "http://127.0.0.1:11434/v1", "model-x", "worker", "standard");
        UseNodes(HubNode, thisComputer, WorkerA);
        WindowClock clock = Clock(SundayAt(2, 30));
        SeedHistory("worker-a", "model-x", clock.UtcNow.AddDays(-1));
        FitProbeService fit = Fit();

        await Weekly(fit, clock).TickAsync(CancellationToken.None);

        Assert.Empty(fit.List());
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Machines_never_measured_come_first_and_then_the_one_measured_longest_ago()
    {
        Configure();
        UseNodes(HubNode, WorkerA, WorkerB);
        WindowClock clock = Clock(SundayAt(2, 30));
        SeedHistory("worker-a", "model-x", clock.UtcNow.AddDays(-8));
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock);

        Assert.Equal("worker-b", (await RunWeeklyAsync(weekly, fit)).Node);
        Assert.Equal("worker-a", (await RunWeeklyAsync(weekly, fit)).Node);
    }

    [Fact]
    public async Task A_measurement_dated_in_the_future_does_not_keep_its_machine_out_of_the_weekly_run()
    {
        Configure();
        UseNodes(HubNode, WorkerA);
        WindowClock clock = Clock(SundayAt(2, 30));
        SeedHistory("worker-a", "model-x", clock.UtcNow.AddDays(10));
        FitProbeService fit = Fit();

        await Weekly(fit, clock).TickAsync(CancellationToken.None);

        Assert.Equal("worker-a", Assert.Single(fit.List()).Node);
    }

    [Theory]
    [InlineData(PlanStatus.Running)]
    [InlineData(PlanStatus.Approved)]
    public async Task Nothing_is_measured_while_a_plan_is_running_or_approved_and_the_next_wake_up_after_it_measures(string status)
    {
        Configure();
        UseNodes(HubNode, WorkerA);
        WindowClock clock = Clock(SundayAt(2, 30));
        PlanRecord plan = _plans.Create("title", "goal", null, [], [], [], null, null,
            [new PlanStepInput("one", "detail", [], null, null)]);
        _plans.Update(plan.Id, current => current with { Status = status });
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock);

        await weekly.TickAsync(CancellationToken.None);
        Assert.Empty(fit.List());
        Assert.Empty(_api.Requests);

        _plans.Update(plan.Id, current => current with { Status = PlanStatus.Done });
        FitProbeJobSnapshot started = await RunWeeklyAsync(weekly, fit);
        Assert.Equal("worker-a", started.Node);
    }

    [Fact]
    public async Task A_machine_with_a_model_in_memory_is_skipped_and_tried_again_in_the_same_window()
    {
        Configure();
        UseNodes(HubNode, WorkerA);
        WindowClock clock = Clock(SundayAt(2, 30));
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock);
        _api.ExternallyLoaded = true;

        await weekly.TickAsync(CancellationToken.None);

        Assert.Empty(fit.List());
        Assert.DoesNotContain(_api.Requests, request => request.Path is "/api/chat" or "/api/generate");

        _api.ExternallyLoaded = false;
        FitProbeJobSnapshot started = await RunWeeklyAsync(weekly, fit);
        Assert.Equal("worker-a", started.Node);
    }

    [Fact]
    public async Task A_machine_that_does_not_answer_is_skipped_for_this_wake_up_and_the_others_are_still_measured()
    {
        Configure();
        UseNodes(HubNode, WorkerA, WorkerB);
        _api.Unresponsive.Add("192.0.2.10");
        FitProbeService fit = Fit();

        await Weekly(fit, Clock(SundayAt(2, 30))).TickAsync(CancellationToken.None);

        Assert.Equal("worker-b", Assert.Single(fit.List()).Node);
    }

    [Fact]
    public async Task Only_one_measurement_runs_at_a_time()
    {
        Configure();
        UseNodes(HubNode, WorkerA, WorkerB);
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, Clock(SundayAt(2, 30)));
        _api.HoldChat = true;

        await weekly.TickAsync(CancellationToken.None);
        await _api.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await weekly.TickAsync(CancellationToken.None);

        FitProbeJobSnapshot running = Assert.Single(fit.List());
        Assert.Equal("worker-a", running.Node);
        Assert.True(fit.Cancel(running.Id));
        await WaitUntilAsync(() => fit.List().Single().State != "running", "The held measurement did not stop.");
    }

    [Fact]
    public async Task A_stopped_run_is_not_recorded_as_a_weekly_run_and_is_not_announced()
    {
        Configure();
        UseNodes(HubNode, WorkerA);
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, Clock(SundayAt(2, 30)), sender);
        _api.HoldChat = true;
        await weekly.TickAsync(CancellationToken.None);
        await _api.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fit.Cancel(fit.List().Single().Id));
        await WaitUntilAsync(() => fit.List().Single().State == "cancelled", "The stopped measurement did not stop.");

        Assert.Null(weekly.Status().LastRunUtc);
        Assert.Empty(sender.Notices);
    }

    [Fact]
    public async Task The_first_measurement_of_a_machine_is_never_a_regression()
    {
        UseNodes(HubNode, WorkerA);
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, RealWeekClock(), sender);
        _api.Behaviour = _ => (1.0, 50);

        FitProbeJobSnapshot first = await RunWeeklyAsync(weekly, fit);

        Assert.Equal("complete", first.State);
        Assert.Empty(sender.Notices);
        Assert.Null(weekly.Status().Nodes.Single().Regression);
        Assert.Equal(FleetSetupService.Ok, weekly.Check()!.Status);
    }

    [Fact]
    public async Task A_smaller_recommended_context_is_a_regression_with_the_old_and_new_sizes()
    {
        UseNodes(HubNode, WorkerA);
        WindowClock clock = RealWeekClock();
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock, sender);
        await RunWeeklyAsync(weekly, fit);

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = context => (1.0, context == 32768 ? 50 : 100);
        FitProbeJobSnapshot second = await RunWeeklyAsync(weekly, fit);

        Assert.Equal("complete", second.State);
        WeeklyFitNotice notice = Assert.Single(sender.Notices);
        Assert.Equal("worker-a", notice.Node);
        Assert.Equal("model-x", notice.Model);
        Assert.Contains("from 32768 to 24576", notice.Summary);
        WeeklyFitNodeStatus node = weekly.Status().Nodes.Single();
        Assert.Equal(second.FinishedAtUtc, weekly.Status().LastRunUtc);
        Assert.NotNull(node.LastMeasuredUtc);
        Assert.Contains("from 32768 to 24576", node.Regression!.Summary);
        SetupCheck check = weekly.Check()!;
        Assert.Equal(FleetSetupService.Warn, check.Status);
        Assert.Contains("worker-a (model-x)", check.Detail);
        Assert.Contains("Speed only, not coding skill.", check.Detail);
    }

    [Fact]
    public async Task A_model_that_no_longer_fully_fits_is_a_regression()
    {
        UseNodes(HubNode, WorkerA);
        WindowClock clock = RealWeekClock();
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock, sender);
        await RunWeeklyAsync(weekly, fit);

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = _ => (1.0, 50);
        await RunWeeklyAsync(weekly, fit);

        WeeklyFitNotice notice = Assert.Single(sender.Notices);
        Assert.Equal("the model no longer fully fits on the graphics card", notice.Summary);
    }

    [Theory]
    [InlineData(0.20, false)]
    [InlineData(0.25, false)]
    [InlineData(0.26, true)]
    public async Task A_speed_loss_at_one_size_is_a_regression_only_when_it_is_more_than_a_quarter(double loss, bool regressed)
    {
        UseNodes(HubNode, WorkerA);
        WindowClock clock = RealWeekClock();
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock, sender);
        await RunWeeklyAsync(weekly, fit);

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = context => (context == 16384 ? 1.0 / (1 - loss) : 1.0, 100);
        await RunWeeklyAsync(weekly, fit);

        if (regressed)
        {
            WeeklyFitNotice notice = Assert.Single(sender.Notices);
            Assert.Contains("speed at 16384 went from 64.0 to 47.4 tokens per second (down 26 percent)", notice.Summary);
        }
        else
        {
            Assert.Empty(sender.Notices);
            Assert.Null(weekly.Status().Nodes.Single().Regression);
        }
    }

    [Fact]
    public async Task A_different_model_on_the_same_machine_is_not_compared()
    {
        UseNodes(HubNode, WorkerA);
        WindowClock clock = RealWeekClock();
        SeedHistory("worker-a", "model-y", clock.UtcNow.AddDays(-1), tokensPerSecond: 200, context: 32768);
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock, sender);

        FitProbeJobSnapshot started = await RunWeeklyAsync(weekly, fit);

        Assert.Equal("complete", started.State);
        Assert.Empty(sender.Notices);
        Assert.Null(weekly.Status().Nodes.Single().Regression);
    }

    [Fact]
    public async Task A_run_that_was_cut_short_is_not_the_baseline_for_the_next_comparison()
    {
        UseNodes(HubNode, WorkerA);
        WindowClock clock = RealWeekClock();
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock, sender);
        await RunWeeklyAsync(weekly, fit);

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.HoldChatFromContext = 24576;
        await weekly.TickAsync(CancellationToken.None);
        await _api.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string stoppedId = fit.List().Single(job => job.State == "running").Id;
        Assert.True(fit.Cancel(stoppedId));
        await WaitUntilAsync(() => fit.List().Single(job => job.Id == stoppedId).State == "cancelled", "The stopped measurement did not stop.");
        _api.HoldChatFromContext = null;

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = context => (1.0, context == 32768 ? 50 : 100);
        FitProbeJobSnapshot complete = await RunWeeklyAsync(weekly, fit);

        Assert.Equal("complete", complete.State);
        WeeklyFitNotice notice = Assert.Single(sender.Notices);
        Assert.Contains("from 32768 to 24576", notice.Summary);
    }

    [Fact]
    public async Task A_measurement_that_stops_on_an_unexpected_error_is_recorded_as_failed_not_as_a_complete_run()
    {
        Configure();
        UseNodes(HubNode, WorkerA);
        _api.BreakTags = true;
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, Clock(SundayAt(2, 30)));

        await weekly.TickAsync(CancellationToken.None);
        await WaitUntilAsync(() => fit.List().Single().State != "running", "The measurement did not stop.");

        Assert.Equal("failed", fit.List().Single().State);
        Assert.Null(weekly.Status().LastRunUtc);
    }

    [Fact]
    public async Task A_regression_that_stays_open_is_announced_once_and_one_that_clears_is_announced_again_later()
    {
        UseNodes(HubNode, WorkerA);
        WindowClock clock = RealWeekClock();
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock, sender);

        await RunWeeklyAsync(weekly, fit);

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = context => (1.0, context == 32768 ? 50 : 100);
        await RunWeeklyAsync(weekly, fit);
        Assert.Single(sender.Notices);
        DateTimeOffset firstSince = weekly.Status().Nodes.Single().Regression!.Since;

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = context => (1.0, context >= 24576 ? 50 : 100);
        await RunWeeklyAsync(weekly, fit);
        Assert.Single(sender.Notices);
        WeeklyFitRegressionView stillOpen = weekly.Status().Nodes.Single().Regression!;
        Assert.Equal(firstSince, stillOpen.Since);
        Assert.Contains("from 24576 to 16384", stillOpen.Summary);
        Assert.Equal(FleetSetupService.Warn, weekly.Check()!.Status);

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = _ => (1.0, 100);
        await RunWeeklyAsync(weekly, fit);
        Assert.Single(sender.Notices);
        Assert.Null(weekly.Status().Nodes.Single().Regression);
        Assert.Equal(FleetSetupService.Ok, weekly.Check()!.Status);

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = context => (1.0, context == 32768 ? 50 : 100);
        await RunWeeklyAsync(weekly, fit);
        Assert.Equal(2, sender.Notices.Count);
        Assert.NotEqual(firstSince, weekly.Status().Nodes.Single().Regression!.Since);
    }

    [Fact]
    public async Task An_open_regression_survives_a_restart_so_it_is_not_announced_again()
    {
        UseNodes(HubNode, WorkerA);
        WindowClock clock = RealWeekClock();
        var sender = new RecordingSender();
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, clock, sender);
        await RunWeeklyAsync(weekly, fit);
        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = context => (1.0, context == 32768 ? 50 : 100);
        await RunWeeklyAsync(weekly, fit);
        DateTimeOffset since = weekly.Status().Nodes.Single().Regression!.Since;

        FitProbeService restartedFit = Fit();
        WeeklyFitProbeService restarted = Weekly(restartedFit, clock, sender);
        Assert.Equal(since, restarted.Status().Nodes.Single().Regression!.Since);
        Assert.NotNull(restarted.Status().LastRunUtc);

        clock.UtcNow = clock.UtcNow.AddDays(7);
        _api.Behaviour = context => (1.0, context >= 24576 ? 50 : 100);
        await RunWeeklyAsync(restarted, restartedFit);

        Assert.Single(sender.Notices);
        Assert.Equal(since, restarted.Status().Nodes.Single().Regression!.Since);
    }

    [Fact]
    public void While_the_measurement_is_off_the_setup_has_no_line_and_the_status_has_no_next_window()
    {
        Configure(enabled: false);
        UseNodes(HubNode, WorkerA);
        WeeklyFitProbeService weekly = Weekly(Fit(), Clock(SundayAt(0, 0)));

        WeeklyFitStatus status = weekly.Status();

        Assert.Null(weekly.Check());
        Assert.False(status.Enabled);
        Assert.Null(status.NextWindowStartUtc);
    }

    [Fact]
    public async Task With_the_block_absent_nothing_runs_nothing_is_contacted_and_nothing_is_written()
    {
        UseNodes(HubNode, WorkerA);
        FitProbeService fit = Fit();
        WeeklyFitProbeService weekly = Weekly(fit, Clock(SundayAt(2, 30)));

        await weekly.TickAsync(CancellationToken.None);

        Assert.Empty(fit.List());
        Assert.Empty(_api.Requests);
        Assert.False(File.Exists(StatePath));
        Assert.Null(weekly.Check());
        WeeklyFitStatus status = weekly.Status();
        Assert.False(status.Enabled);
        Assert.Null(status.NextWindowStartUtc);
        Assert.Null(status.LastRunUtc);
    }

    [Fact]
    public void A_state_file_with_an_empty_regression_entry_does_not_break_the_status()
    {
        Configure();
        UseNodes(HubNode, WorkerA);
        FitProbeService fit = Fit();
        File.WriteAllText(StatePath, "{\"regressions\":{\"worker-a\":null}}");

        WeeklyFitStatus status = Weekly(fit, Clock(SundayAt(0, 0))).Status();

        Assert.Null(status.Nodes.Single().Regression);
    }

    [Fact]
    public void The_setup_line_says_no_measurement_has_run_yet()
    {
        Configure();
        UseNodes(HubNode, WorkerA);

        SetupCheck check = Weekly(Fit(), Clock(SundayAt(2, 30))).Check()!;

        Assert.Equal(FleetSetupService.Info, check.Status);
        Assert.Equal("Weekly fit measurement", check.Title);
        Assert.Equal("No weekly measurement has run yet.", check.Detail);
    }

    [Fact]
    public void A_regression_is_a_warning_that_carries_the_reason_text()
    {
        DateTimeOffset since = SundayAt(2, 30);

        SetupCheck check = WeeklyFitCheck.Build(
            [new WeeklyFitNodeStatus("worker-a", "model-x", since, new WeeklyFitRegressionView(since, "the recommended context went from 32768 to 24576"))],
            since);

        Assert.Equal(FleetSetupService.Warn, check.Status);
        Assert.Equal(
            "worker-a (model-x): the recommended context went from 32768 to 24576. Speed only, not coding skill.",
            check.Detail);
    }

    [Fact]
    public void Without_a_regression_the_setup_line_is_ok_and_dated_by_the_last_run()
    {
        SetupCheck check = WeeklyFitCheck.Build([new WeeklyFitNodeStatus("worker-a", "model-x", null, null)], SundayAt(2, 30));

        Assert.Equal(FleetSetupService.Ok, check.Status);
        Assert.Equal("No regression in the last weekly measurement (2026-10-18 UTC).", check.Detail);
    }

    [Fact]
    public void The_status_names_the_next_window_and_only_the_machines_it_covers()
    {
        Configure(day: "Sunday", startHour: 2, hours: 4);
        UseNodes(HubNode, WorkerA, WorkerB, VisionNode, OpenAiNode);

        WeeklyFitStatus status = Weekly(Fit(), Clock(SundayAt(0, 0))).Status();

        Assert.True(status.Enabled);
        Assert.Equal("sunday", status.DayOfWeek);
        Assert.Equal(2, status.StartHour);
        Assert.Equal(4, status.Hours);
        Assert.Equal(SundayAt(2, 0), status.NextWindowStartUtc);
        Assert.Null(status.LastRunUtc);
        Assert.Equal(new[] { "worker-a", "worker-b" }, status.Nodes.Select(node => node.Name));
        Assert.All(status.Nodes, node =>
        {
            Assert.Null(node.LastMeasuredUtc);
            Assert.Null(node.Regression);
        });
    }

    [Fact]
    public void The_weekly_notice_is_plain_text_about_speed_with_the_machine_model_and_numbers()
    {
        string body = WebhookPlanNotifier.Body(new WeeklyFitNotice(
            "worker-a", "model-x", "the recommended context went from 32768 to 24576", SundayAt(2, 30)));

        using JsonDocument json = JsonDocument.Parse(body);
        string text = json.RootElement.GetProperty("text").GetString()!;
        Assert.Equal("weekly-fit-regressed", json.RootElement.GetProperty("event").GetString());
        Assert.Contains("worker-a", text);
        Assert.Contains("model-x", text);
        Assert.Contains("from 32768 to 24576", text);
        Assert.Contains("Speed only, not coding skill.", text);
    }

    [Theory]
    [InlineData("Monday", "monday")]
    [InlineData("  FRIDAY ", "friday")]
    [InlineData("someday", "sunday")]
    [InlineData("1", "sunday")]
    [InlineData(null, "sunday")]
    public void A_day_name_is_kept_in_lower_case_and_anything_else_means_sunday(string? day, string expected) =>
        Assert.Equal(expected, new FleetWeeklyProbeConfig(true, day!, 2, 4).Normalized().DayOfWeek);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(23, 23)]
    [InlineData(24, 2)]
    [InlineData(-1, 2)]
    public void The_start_hour_is_kept_between_0_and_23_and_otherwise_means_2(int hour, int expected) =>
        Assert.Equal(expected, new FleetWeeklyProbeConfig(StartHour: hour).Normalized().StartHour);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(12, 12)]
    [InlineData(0, 4)]
    [InlineData(13, 4)]
    public void The_window_length_is_kept_between_1_and_12_hours_and_otherwise_means_4(int hours, int expected) =>
        Assert.Equal(expected, new FleetWeeklyProbeConfig(Hours: hours).Normalized().Hours);

    [Fact]
    public void The_weekly_probe_is_off_with_defaults_when_the_file_has_no_block()
    {
        File.WriteAllText(Path.Combine(_directory, "fleet.config.json"), ConfigJson(string.Empty));

        FleetWeeklyProbeConfig settings = new FleetConfigStore(_configuration).Current.WeeklyProbeSettings;

        Assert.False(settings.Enabled);
        Assert.Equal(new FleetWeeklyProbeConfig(), settings);
    }

    [Fact]
    public void Invalid_values_in_the_file_fall_back_to_defaults_and_a_saved_block_survives_a_reopen()
    {
        File.WriteAllText(
            Path.Combine(_directory, "fleet.config.json"),
            ConfigJson(",\"weeklyProbe\":{\"enabled\":true,\"dayOfWeek\":\"Friday\",\"startHour\":99,\"hours\":0}"));
        Assert.Equal(
            new FleetWeeklyProbeConfig(true, "friday", 2, 4),
            new FleetConfigStore(_configuration).Current.WeeklyProbeSettings);

        var store = new FleetConfigStore(_configuration);
        store.Save(store.Current with { WeeklyProbe = new FleetWeeklyProbeConfig(true, "saturday", 23, 12) });

        Assert.Equal(
            new FleetWeeklyProbeConfig(true, "saturday", 23, 12),
            new FleetConfigStore(_configuration).Current.WeeklyProbeSettings);
    }

    private static string ConfigJson(string extra) =>
        "{\"triageModel\":\"t\",\"mode\":\"conservative\",\"planModeEnabled\":false,\"nodes\":[{\"name\":\"hub\"," +
        "\"url\":\"http://127.0.0.1:11434/v1\",\"model\":\"m\",\"purpose\":\"local\",\"tier\":\"heavy\",\"fallback\":true}]," +
        "\"tools\":{}" + extra + "}";

    private void UseNodes(params FleetNodeConfig[] nodes) =>
        _options.ReplaceNodes(_configStore.Save(_configStore.Current with { Nodes = nodes }));

    private void Configure(bool enabled = true, string day = "sunday", int startHour = 2, int hours = 4) =>
        _configStore.Save(_configStore.Current with { WeeklyProbe = new FleetWeeklyProbeConfig(enabled, day, startHour, hours) });

    private FitProbeService Fit() => new(_options, _plans, _configStore, new Factory(_api), _configuration,
        NullLogger<FitProbeService>.Instance, _measuringMachines);

    private WeeklyFitProbeService Weekly(FitProbeService fit, TimeProvider clock, IWeeklyFitNoticeSender? sender = null) =>
        new(fit, _configStore, _options, _plans, new Factory(_api), clock, sender, NullLogger.Instance);

    // A clock at the real time, with the window set to open at this hour on this weekday for twelve hours. The probe stamps
    // its history with the real time, so a clock started from it keeps the history's age in step, and each week after is
    // inside the window too.
    private WindowClock RealWeekClock()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Configure(true, now.DayOfWeek.ToString(), now.Hour, 12);
        return Clock(now);
    }

    private static WindowClock Clock(DateTimeOffset utc, TimeZoneInfo? zone = null) => new(zone ?? TimeZoneInfo.Utc) { UtcNow = utc };

    private static DateTimeOffset SundayAt(int hour, int minute = 0) => new(2026, 10, 18, hour, minute, 0, TimeSpan.Zero);

    private void SeedHistory(string node, string model, DateTimeOffset when, double tokensPerSecond = 64, int context = 12288)
    {
        var entry = new FitProbeHistoryEntry("seeded-" + node, when, "192.0.2.1:11434", node, model, "sha256:first", "1.2.3",
            context, "ok", 1.0, tokensPerSecond, 2.0, null);
        Directory.CreateDirectory(Path.Combine(_directory, "lab"));
        File.AppendAllText(
            Path.Combine(_directory, "lab", "fit-history.jsonl"),
            JsonSerializer.Serialize(entry, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + Environment.NewLine);
    }

    // One wake-up of the weekly service. Returns the measurement it started, once that measurement has been recorded.
    private static async Task<FitProbeJobSnapshot> RunWeeklyAsync(WeeklyFitProbeService weekly, FitProbeService fit)
    {
        HashSet<string> known = fit.List().Select(job => job.Id).ToHashSet(StringComparer.Ordinal);
        DateTimeOffset? lastRun = weekly.Status().LastRunUtc;
        await weekly.TickAsync(CancellationToken.None);
        string id = fit.List().Single(job => !known.Contains(job.Id)).Id;
        await WaitUntilAsync(() => weekly.Status().LastRunUtc != lastRun, "The weekly run was not recorded.");
        return fit.List().Single(job => job.Id == id);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(because);
    }

    private sealed class WindowClock(TimeZoneInfo zone) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class RecordingSender : IWeeklyFitNoticeSender
    {
        private readonly List<WeeklyFitNotice> _notices = [];

        public IReadOnlyList<WeeklyFitNotice> Notices
        {
            get
            {
                lock (_notices) return _notices.ToArray();
            }
        }

        public Task SendAsync(WeeklyFitNotice notice)
        {
            lock (_notices) _notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    // Stands in for an Ollama machine. Each size's speed and graphics-memory share come from Behaviour, so a test can
    // make a machine slower or spill a size onto the processor from one week to the next.
    private sealed class FakeWeeklyApi : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(string Host, string Path)> _requests = [];
        private bool _loaded;
        private int _context;

        public Func<int, (double EvalSeconds, int GpuPercent)> Behaviour { get; set; } = _ => (1.0, 100);

        public int MaximumContext { get; set; } = 32768;

        public bool ExternallyLoaded { get; set; }

        public bool HoldChat { get; set; }

        // Holds the reply to a size from this context up, so a measurement can be stopped part way through.
        public int? HoldChatFromContext { get; set; }

        public HashSet<string> Unresponsive { get; } = new(StringComparer.Ordinal);

        public Action? OnPs { get; set; }

        public bool BreakTags { get; set; }

        public TaskCompletionSource ChatStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(string Host, string Path)> Requests
        {
            get
            {
                lock (_gate) return _requests.ToArray();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            lock (_gate) _requests.Add((request.RequestUri.Host, path));
            if (path == "/api/tags" && BreakTags)
            {
                throw new NotSupportedException("The server answered in a form this probe does not expect.");
            }

            if (path == "/api/ps")
            {
                OnPs?.Invoke();
                if (Unresponsive.Contains(request.RequestUri.Host))
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
            }

            if (path == "/api/chat" && (HoldChat || (HoldChatFromContext is { } from && ContextOf(body) >= from)))
            {
                ChatStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            string json = path switch
            {
                "/api/tags" => "{\"models\":[{\"name\":\"model-x:latest\",\"digest\":\"sha256:first\"}]}",
                "/api/version" => "{\"version\":\"1.2.3\"}",
                "/api/show" => "{\"model_info\":{\"test.context_length\":" + MaximumContext.ToString(CultureInfo.InvariantCulture) + "}}",
                "/api/ps" => PsJson(),
                "/api/chat" => Chat(body),
                "/api/generate" => Generate(body),
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private string PsJson()
        {
            if (ExternallyLoaded)
            {
                return "{\"models\":[{\"name\":\"other-model:latest\",\"size\":10,\"size_vram\":10}]}";
            }

            lock (_gate)
            {
                if (!_loaded)
                {
                    return "{\"models\":[]}";
                }

                int share = Behaviour(_context).GpuPercent;
                return "{\"models\":[{\"name\":\"model-x:latest\",\"size\":100,\"size_vram\":" +
                       share.ToString(CultureInfo.InvariantCulture) + "}]}";
            }
        }

        private static int ContextOf(string body)
        {
            using JsonDocument request = JsonDocument.Parse(body);
            return request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32();
        }

        private string Chat(string body)
        {
            using JsonDocument request = JsonDocument.Parse(body);
            int context = request.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32();
            (double seconds, _) = Behaviour(context);
            lock (_gate)
            {
                _loaded = true;
                _context = context;
            }

            long nanoseconds = (long)Math.Round(seconds * 1_000_000_000d);
            return "{\"done\":true,\"eval_count\":64,\"eval_duration\":" + nanoseconds.ToString(CultureInfo.InvariantCulture) +
                   ",\"load_duration\":2000000000}";
        }

        private string Generate(string body)
        {
            if (body.Contains("\"keep_alive\":0", StringComparison.Ordinal))
            {
                lock (_gate) _loaded = false;
            }

            return "{}";
        }
    }
}
