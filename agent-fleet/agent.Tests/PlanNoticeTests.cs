using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class PlanNoticeTests : PlanTestBase
{
    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status);
        }
    }

    private static PlanNotice Notice(string kind = "step-parked") =>
        new(kind, "abc", "Fix the widget", 1, 1, 4, 2, "Second step", "check-kept-failing", DateTimeOffset.Parse("2026-10-02T03:04:05Z"));

    [Theory]
    [InlineData("http://127.0.0.1:9000/hook")]
    [InlineData("http://192.168.1.50:8080/notify")]
    [InlineData("https://10.0.0.5/x")]
    [InlineData("http://172.16.4.1/x")]
    [InlineData("http://172.31.255.1/x")]
    [InlineData("http://100.101.102.103/x")]
    [InlineData("http://169.254.10.10/x")]
    [InlineData("http://[::1]:5000/x")]
    [InlineData("http://[fd12:3456::1]/x")]
    [InlineData("http://homeserver:5000/x")]
    [InlineData("http://ntfy.lan/fleet")]
    [InlineData("")]
    [InlineData(null)]
    public void An_address_on_the_private_network_or_none_at_all_is_accepted(string? url) =>
        Assert.Null(WebhookPlanNotifier.Problem(url));

    [Theory]
    [InlineData("http://8.8.8.8/hook", "private network")]
    [InlineData("https://203.0.113.9/hook", "private network")]
    [InlineData("http://172.32.0.1/x", "private network")]
    [InlineData("http://100.128.0.1/x", "private network")]
    [InlineData("ftp://192.168.1.5/x", "http or https")]
    [InlineData("not a url", "http or https")]
    [InlineData("http://user:pass@192.168.1.5/x", "user name or password")]
    public void A_public_address_or_an_odd_url_is_refused_with_the_reason(string url, string reason) =>
        Assert.Contains(reason, WebhookPlanNotifier.Problem(url));

    [Fact]
    public void The_message_holds_titles_counts_and_a_cause_word_and_nothing_from_a_check_or_a_file()
    {
        string body = WebhookPlanNotifier.Body(Notice());
        using var document = System.Text.Json.JsonDocument.Parse(body);
        System.Text.Json.JsonElement root = document.RootElement;
        Assert.Equal("step-parked", root.GetProperty("event").GetString());
        Assert.Equal("abc", root.GetProperty("planId").GetString());
        Assert.Equal("Fix the widget", root.GetProperty("plan").GetString());
        Assert.Equal(2, root.GetProperty("stepId").GetInt32());
        Assert.Equal("Second step", root.GetProperty("step").GetString());
        Assert.Equal((1, 1, 4), (root.GetProperty("stepsDone").GetInt32(), root.GetProperty("stepsParked").GetInt32(), root.GetProperty("stepsTotal").GetInt32()));
        Assert.Equal("check-kept-failing", root.GetProperty("cause").GetString());
        Assert.Equal(
            ["at", "cause", "event", "plan", "planId", "step", "stepId", "stepsDone", "stepsParked", "stepsTotal"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_notice_is_posted_to_a_private_address_and_never_to_a_public_one()
    {
        var handler = new RecordingHandler();
        var privateOne = new WebhookPlanNotifier(new HttpClient(handler), "http://192.168.1.50:8080/notify", NullLogger.Instance);
        await privateOne.SendAsync(Notice());
        (Uri? uri, string body) = Assert.Single(handler.Requests);
        Assert.Equal("http://192.168.1.50:8080/notify", uri?.ToString());
        Assert.Contains("\"event\":\"step-parked\"", body);

        var publicOne = new WebhookPlanNotifier(new HttpClient(handler), "http://8.8.8.8/notify", NullLogger.Instance);
        await publicOne.SendAsync(Notice());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_to_a_private_address_is_not_called()
    {
        var handler = new RecordingHandler();
        // Resolves on any machine, and never to a private address.
        var notifier = new WebhookPlanNotifier(new HttpClient(handler), "http://one.one.one.one/notify", NullLogger.Instance);
        await notifier.SendAsync(Notice());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_handler_refuses_to_connect_to_a_public_address_before_any_connection_is_made()
    {
        // The check is made where the connection is made, so a name whose answer changes after an earlier check cannot get through.
        using var client = new HttpClient(WebhookPlanNotifier.CreateHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://8.8.8.8:9/hook"));
        Assert.Contains("private network", refused.InnerException?.Message ?? refused.Message);
        Assert.False(client.DefaultRequestHeaders.Contains("Location"));
    }

    private sealed class BrokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidCastException("something unexpected");
    }

    [Fact]
    public async Task Whatever_goes_wrong_while_sending_stays_inside_the_task()
    {
        var notifier = new WebhookPlanNotifier(new HttpClient(new BrokenHandler()), "http://127.0.0.1:1/notify", NullLogger.Instance);
        await notifier.SendAsync(Notice());
    }

    [Fact]
    public async Task A_failing_webhook_is_logged_and_never_thrown_into_the_run()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        var notifier = new WebhookPlanNotifier(new HttpClient(handler), "http://127.0.0.1:1/notify", NullLogger.Instance);
        await notifier.SendAsync(Notice());
        Assert.Single(handler.Requests);

        var unreachable = new WebhookPlanNotifier(new HttpClient(new ThrowingHandler()), "http://127.0.0.1:1/notify", NullLogger.Instance);
        await unreachable.SendAsync(Notice());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    [Fact]
    public void The_config_refuses_a_public_notice_address_and_keeps_a_private_one()
    {
        string directory = Path.Combine(PlansDirectory, "cfg");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fleet.config.json");
        string Write(string notifyUrl)
        {
            File.WriteAllText(path, "{\"triageModel\":\"t\",\"mode\":\"conservative\",\"planModeEnabled\":false,\"nodes\":[{\"name\":\"hub\",\"url\":\"http://127.0.0.1:11434/v1\",\"model\":\"m\",\"purpose\":\"p\",\"tier\":\"heavy\",\"fallback\":true}],\"tools\":{}," + notifyUrl + "}");
            return path;
        }

        FleetConfigStore Open() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = path }).Build());

        Write("\"notifyUrl\":\"http://192.168.1.50:8080/notify\"");
        Assert.Equal("http://192.168.1.50:8080/notify", Open().Current.NotifyUrl);

        Write("\"notifyUrl\":\"http://8.8.8.8/notify\"");
        Assert.Contains("private network", Assert.Throws<InvalidOperationException>(() => Open()).Message);

        Write("\"notifyUrl\":null");
        Assert.Null(Open().Current.NotifyUrl);
    }

    [Fact]
    public void The_summary_counts_parked_steps_for_the_extension()
    {
        PlanRecord plan = NewPlan(Step("a"), Step("b"), Step("c"));
        Store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, 2, step => step with { Status = StepStatus.Parked }));
        PlanSummary summary = Store.List().Single(candidate => candidate.Id == plan.Id);
        Assert.Equal(1, summary.StepsParked);
        Assert.Equal(0, summary.StepsDone);
    }

    [Fact]
    public void The_summary_identifies_each_currently_parked_step_without_exposing_its_output()
    {
        PlanRecord plan = NewPlan(Step("Base"), Step("Feature A"), Step("Feature B"));
        Store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, 2, step => step with { Status = StepStatus.Parked }));
        Store.AddEvent(plan.Id, 2, null, RunEventKind.StepParked, detail: "private check output", failureSignature: "environment");

        PlanSummary summary = Store.List().Single(candidate => candidate.Id == plan.Id);
        ParkedStepSummary parked = Assert.Single(summary.ParkedSteps!);

        Assert.Equal(2, parked.StepId);
        Assert.Equal("Feature A", parked.Title);
        Assert.Equal("environment", parked.Cause);
        Assert.NotNull(parked.ParkedAtUtc);
        Assert.DoesNotContain("private check output", System.Text.Json.JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_file_can_say_what_a_step_depends_on()
    {
        string folder = Path.Combine(PlansDirectory, "plan-file");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "plan.md");
        File.WriteAllText(path, string.Join("\n",
            "# Plan: two features",
            "",
            "Working directory: `" + folder + "`",
            "",
            "## Step 1: Base",
            "- Tier: light",
            "- Check: `npm test`",
            "Do the base.",
            "",
            "## Step 2: Feature A",
            "- Tier: light",
            "- Depends: 1",
            "- Check: `npm test`",
            "Do A.",
            "",
            "## Step 3: Feature B",
            "- Tier: light",
            "- Depends on: Step 1, 2",
            "- Check: `npm test`",
            "Do B.",
            "",
            "## Step 4: Everything",
            "- Tier: light",
            "- Depends: none",
            "- Check: `npm test`",
            "Check all.",
            ""));

        PlanFileContent content = PlanFile.Read(path);

        Assert.Null(content.Steps[0].DependsOn);
        Assert.Equal([1], content.Steps[1].DependsOn);
        Assert.Equal([1, 2], content.Steps[2].DependsOn);
        Assert.Null(content.Steps[3].DependsOn);
    }
}
