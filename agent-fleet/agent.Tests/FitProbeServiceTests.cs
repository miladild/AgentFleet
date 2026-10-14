using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class FitProbeServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-fit-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _configuration;
    private readonly FleetConfigStore _configStore;
    private readonly FleetOptions _options;
    private readonly FleetPlanStore _plans;
    private readonly MeasuringMachines _measuringMachines = new();

    public FitProbeServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLEET_CONFIG_PATH"] = Path.Combine(_directory, "fleet.config.json"),
            ["FLEET_PLANS_DIR"] = Path.Combine(_directory, "plans"),
            ["FLEET_LAB_DIR"] = Path.Combine(_directory, "lab")
        }).Build();
        _configStore = new FleetConfigStore(_configuration);
        _configStore.Save(_configStore.Current with
        {
            Nodes =
            [
                new("worker-a", "http://worker-a.example.test:11434/v1", "model-x", "worker", "standard"),
                new("worker-b", "http://worker-a.example.test:11434/v1", "model-x", "worker", "light"),
                new("local-node", "http://127.0.0.1:11434/v1", "model-x", "local", "heavy", Fallback: true),
                new("vision-node", "http://vision.example.test:11434/v1", "model-x", "vision", Vision: true),
                new("compatible-node", "http://compatible.example.test:11434/v1", "model-x", "worker", Api: "openai")
            ]
        });
        _options = FleetOptions.Load(_configuration, _configStore);
        _plans = new FleetPlanStore(_configuration);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Seen(HttpMethod Method, string Host, string Path, string Body);

    private sealed class FakeApi : HttpMessageHandler
    {
        public readonly ConcurrentQueue<Seen> Requests = new();
        public readonly TaskCompletionSource ChatStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldChat { get; set; }
        public int? MaximumContext { get; set; } = 32768;
        public string Digest { get; set; } = "sha256:first";
        public string Version { get; set; } = "1.2.3";

        public int ChatCount => Requests.Count(request => request.Path == "/api/chat");
        public int UnloadCount => Requests.Count(request => request.Path == "/api/generate" && request.Body.Contains("\"keep_alive\":0", StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            Requests.Enqueue(new Seen(request.Method, request.RequestUri.Host, path, body));
            if (path == "/api/chat" && HoldChat)
            {
                ChatStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            string json = path switch
            {
                "/api/tags" => $"{{\"models\":[{{\"name\":\"model-x:latest\",\"digest\":\"{Digest}\"}}]}}",
                "/api/version" => $"{{\"version\":\"{Version}\"}}",
                "/api/show" => MaximumContext is int maximum
                    ? $"{{\"model_info\":{{\"test.context_length\":{maximum}}}}}"
                    : "{}",
                "/api/ps" => "{\"models\":[]}",
                "/api/chat" => "{\"done\":true,\"eval_count\":64,\"eval_duration\":4000000000,\"load_duration\":2000000000}",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private FitProbeService Service(FakeApi api, TimeSpan? sizeCap = null, TimeSpan? jobCap = null) => new(
        _options,
        _plans,
        _configStore,
        new Factory(api),
        _configuration,
        NullLogger<FitProbeService>.Instance,
        _measuringMachines,
        sizeCap,
        jobCap);

    private static async Task<FitProbeJobSnapshot> WaitFor(FitProbeService service, string id)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            FitProbeJobSnapshot? job = service.List().SingleOrDefault(candidate => candidate.Id == id);
            if (job is not null && job.State != "running") return job;
            await Task.Delay(10);
        }

        throw new TimeoutException("The fake measurement did not finish.");
    }

    private PlanRecord ApprovedPlan()
    {
        PlanRecord plan = _plans.Create("title", "goal", null, [], [], [], null, null,
            [new PlanStepInput("one", "detail", [], null, null)]);
        return _plans.Approve(plan.Id)!;
    }

    [Theory]
    [InlineData(PlanStatus.Running)]
    [InlineData(PlanStatus.Approved)]
    public void A_running_or_approved_plan_refuses_a_measurement(string status)
    {
        PlanRecord plan = _plans.Create("title", "goal", null, [], [], [], null, null,
            [new PlanStepInput("one", "detail", [], null, null)]);
        _plans.Update(plan.Id, current => current with { Status = status });
        FitProbeService service = Service(new FakeApi());

        FitProbeStartResult result = service.Start("worker-a");

        Assert.Null(result.Job);
        Assert.Contains("plan", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_second_node_at_the_same_address_is_refused()
    {
        FakeApi api = new() { HoldChat = true };
        FitProbeService service = Service(api);
        FitProbeStartResult first = service.Start("worker-a");
        await api.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        FitProbeStartResult second = service.Start("worker-b");

        Assert.Null(second.Job);
        Assert.Contains("already running", second.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.Cancel(first.Job!.Id));
        Assert.Equal("cancelled", (await WaitFor(service, first.Job.Id)).State);
    }

    [Fact]
    public async Task A_measurement_marks_its_machine_until_the_job_finishes()
    {
        FakeApi api = new() { HoldChat = true };
        FitProbeService service = Service(api);
        Uri root = new("http://worker-a.example.test:11434/");

        FitProbeStartResult result = service.Start("worker-a");
        await api.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_measuringMachines.IsMeasuring(root));
        Assert.True(service.Cancel(result.Job!.Id));
        Assert.Equal("cancelled", (await WaitFor(service, result.Job.Id)).State);
        Assert.False(_measuringMachines.IsMeasuring(root));
    }

    [Theory]
    [InlineData("compatible-node", "compatible API", false)]
    [InlineData("vision-node", "vision", false)]
    [InlineData("missing-node", "not in the saved settings", false)]
    [InlineData("local-node", "needs confirmation", true)]
    public void Unsupported_or_unconfirmed_nodes_are_refused(string node, string reason, bool needsConfirmation)
    {
        FitProbeStartResult result = Service(new FakeApi()).Start(node);

        Assert.Null(result.Job);
        Assert.Contains(reason, result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(needsConfirmation, result.NeedsConfirmation);
    }

    [Fact]
    public async Task Only_a_node_on_this_computer_asks_for_confirmation()
    {
        FitProbeService service = Service(new FakeApi { MaximumContext = 12288 });

        FitProbeStartResult confirmed = service.Start("local-node", confirmHub: true);
        Assert.NotNull(confirmed.Job);
        Assert.False(confirmed.NeedsConfirmation);
        Assert.Equal("complete", (await WaitFor(service, confirmed.Job.Id)).State);

        ApprovedPlan();
        FitProbeStartResult remote = service.Start("worker-a");
        Assert.Null(remote.Job);
        Assert.Contains("plan", remote.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(remote.NeedsConfirmation);
    }

    [Fact]
    public async Task A_confirmed_local_node_can_be_tested_through_a_fake_handler()
    {
        FakeApi api = new() { MaximumContext = 12288 };
        FitProbeService service = Service(api);

        FitProbeStartResult result = service.Start("local-node", confirmHub: true);
        FitProbeJobSnapshot finished = await WaitFor(service, result.Job!.Id);

        Assert.Equal("complete", finished.State);
        Assert.Equal([12288], finished.Results.Select(point => point.Context));
        Assert.All(api.Requests, request => Assert.Equal("127.0.0.1", request.Host));
    }

    [Fact]
    public async Task The_model_maximum_limits_the_sizes_that_are_measured()
    {
        FakeApi api = new() { MaximumContext = 16384 };
        FitProbeService service = Service(api);

        FitProbeStartResult result = service.Start("worker-a");
        FitProbeJobSnapshot finished = await WaitFor(service, result.Job!.Id);

        Assert.Equal([12288, 16384], finished.Results.Select(point => point.Context));
        Assert.Equal(2, api.ChatCount);
    }

    [Fact]
    public async Task An_unknown_model_maximum_uses_every_candidate_size()
    {
        FakeApi api = new() { MaximumContext = null };
        FitProbeService service = Service(api);

        FitProbeJobSnapshot finished = await WaitFor(service, service.Start("worker-a").Job!.Id);

        Assert.Equal([12288, 16384, 24576, 32768], finished.Results.Select(point => point.Context));
        Assert.Equal(4, api.ChatCount);
    }

    [Fact]
    public async Task A_size_timeout_is_recorded_and_the_job_moves_to_the_next_size()
    {
        FakeApi api = new() { HoldChat = true };
        FitProbeService service = Service(api, sizeCap: TimeSpan.FromMilliseconds(40), jobCap: TimeSpan.FromSeconds(2));

        FitProbeStartResult result = service.Start("worker-a");
        FitProbeJobSnapshot finished = await WaitFor(service, result.Job!.Id);

        Assert.Equal("complete", finished.State);
        Assert.Equal(4, finished.Results.Count);
        Assert.All(finished.Results, point =>
        {
            Assert.Equal("error", point.Status);
            Assert.Equal("timed out", point.Detail);
        });
    }

    [Fact]
    public async Task The_job_cap_ends_a_measurement_as_incomplete()
    {
        FakeApi api = new() { HoldChat = true };
        FitProbeService service = Service(api, sizeCap: TimeSpan.FromSeconds(2), jobCap: TimeSpan.FromMilliseconds(50));

        FitProbeStartResult result = service.Start("worker-a");
        FitProbeJobSnapshot finished = await WaitFor(service, result.Job!.Id);

        Assert.Equal("incomplete", finished.State);
        Assert.Contains("20 minute limit", finished.Reason);
        Assert.Empty(finished.Results);
    }

    [Fact]
    public async Task Cancelling_a_measurement_unloads_it_and_records_the_reason()
    {
        FakeApi api = new() { HoldChat = true };
        FitProbeService service = Service(api);
        FitProbeStartResult started = service.Start("worker-a");
        await api.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.Cancel(started.Job!.Id));
        FitProbeJobSnapshot finished = await WaitFor(service, started.Job.Id);

        Assert.Equal("cancelled", finished.State);
        Assert.Equal("Stopped by request.", finished.Reason);
        Assert.Equal(2, api.UnloadCount);
    }

    [Fact]
    public async Task A_plan_start_cancels_a_measurement_and_unloads_it()
    {
        FakeApi api = new() { HoldChat = true };
        FitProbeService service = Service(api);
        FitProbeStartResult started = service.Start("worker-a");
        await api.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        PlanRecord approved = ApprovedPlan();
        var tools = new PlanTools(_plans, new FakeValidator(_ => new DiagramCheck(true, true, string.Empty, null)),
            (_, _, _) => Task.FromResult("Exit code: 0\n--- stdout ---\nok"));
        var agent = new FakeStepAgent((_, _, _) => Task.FromResult("done"));
        var runner = new PlanRunner(_plans, tools, agent, NullLogger<PlanRunner>.Instance, fitProbes: service);

        await runner.RunPlanAsync(approved.Id, CancellationToken.None);
        FitProbeJobSnapshot finished = await WaitFor(service, started.Job!.Id);

        Assert.Equal("cancelled", finished.State);
        Assert.Equal("Stopped: a plan needed this machine. Nothing changed.", finished.Reason);
        Assert.Equal(2, api.UnloadCount);
    }

    [Fact]
    public async Task Finished_sizes_are_reused_until_forced_and_history_survives_a_new_service()
    {
        FakeApi api = new() { MaximumContext = 12288 };
        FitProbeService firstService = Service(api);
        FitProbeJobSnapshot first = await WaitFor(firstService, firstService.Start("worker-a").Job!.Id);
        FitProbeJobSnapshot reused = await WaitFor(firstService, firstService.Start("worker-a").Job!.Id);
        FitProbeJobSnapshot forced = await WaitFor(firstService, firstService.Start("worker-a", force: true).Job!.Id);

        Assert.Equal("complete", first.State);
        Assert.True(Assert.Single(reused.Results).Reused);
        Assert.Equal(2, api.ChatCount);
        Assert.False(Assert.Single(forced.Results).Reused);
        Assert.Equal(2, api.ChatCount);
        Assert.Equal(2, File.ReadLines(Path.Combine(_directory, "lab", "fit-history.jsonl")).Count());

        FitProbeService afterRestart = Service(api);
        FitProbeLatestResult? latest = await afterRestart.LatestAsync("worker-a", CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal("model-x", latest.Model);
        Assert.Equal([12288], latest.Results.Select(point => point.Context));
        Assert.False(latest.Stale);
    }

    [Fact]
    public async Task A_service_restart_recovers_a_partial_job_as_incomplete()
    {
        FakeApi api = new() { HoldChat = true };
        FitProbeService runningService = Service(api);
        FitProbeStartResult started = runningService.Start("worker-a");
        await api.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        FitProbeService restartedService = Service(api);
        FitProbeJobSnapshot recovered = Assert.Single(restartedService.List());

        Assert.Equal(started.Job!.Id, recovered.Id);
        Assert.Equal("incomplete", recovered.State);
        Assert.Contains("service restarted", recovered.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(runningService.Cancel(started.Job.Id));
        Assert.Equal("cancelled", (await WaitFor(runningService, started.Job.Id)).State);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("version")]
    public async Task A_changed_digest_or_server_version_marks_history_stale(string changed)
    {
        FakeApi api = new() { MaximumContext = 12288 };
        FitProbeService service = Service(api);
        await WaitFor(service, service.Start("worker-a").Job!.Id);
        if (changed == "digest") api.Digest = "sha256:second";
        else api.Version = "1.2.4";

        FitProbeLatestResult latest = (await service.LatestAsync("worker-a", CancellationToken.None))!;

        Assert.True(latest.Stale);
        Assert.Contains(changed == "digest" ? "data changed" : "updated", latest.StaleReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_changed_configured_model_marks_history_stale()
    {
        FakeApi api = new() { MaximumContext = 12288 };
        FitProbeService service = Service(api);
        await WaitFor(service, service.Start("worker-a").Job!.Id);
        FleetConfig updated = _configStore.Save(_configStore.Current with
        {
            Nodes = _configStore.Current.Nodes
                .Select(node => node.Name == "worker-a" ? node with { Model = "model-y" } : node)
                .ToArray()
        });
        _options.ReplaceNodes(updated);

        FitProbeLatestResult latest = (await service.LatestAsync("worker-a", CancellationToken.None))!;

        Assert.True(latest.Stale);
        Assert.Contains("model changed", latest.StaleReason, StringComparison.OrdinalIgnoreCase);
    }
}
