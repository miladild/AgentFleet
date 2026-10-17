using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentFleet.Tests;

// FleetTelemetry keeps process-wide state (the live provider count, and listeners that see every activity in the process),
// so these tests run one after another with the other classes that register telemetry, not beside them.
[Collection(ContextTestBase.SqliteRecordCollection)]
public sealed class FleetTelemetryTests
{
    private sealed class Model(HttpStatusCode? failure = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (failure is { } status) throw new HttpRequestException("synthetic failure", null, status);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent((await GetResponseAsync(messages, options, cancellationToken)).Text)]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ModelInfo : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    private sealed class Shortener : IContextWindowFitter
    {
        public string Name => "test";
        public ValueTask<IReadOnlyList<ChatMessage>> FitAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, int budgetTokens,
            double scale, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<ChatMessage>>([messages[^1]]);
    }

    private static IChatClient Client(IChatClient model) => new ChatClientBuilder(
            new ContextSizeChatClient(model, new HttpClient(new ModelInfo()) { BaseAddress = new Uri("http://127.0.0.1/") },
                "synthetic-model", 8192, NullLogger.Instance, fitter: new Shortener(), nodeName: "worker-a"))
        .UseOpenTelemetry(NullLoggerFactory.Instance, "AgentFleet.Model", client => client.EnableSensitiveData = false)
        .Build();

    [Fact]
    public void With_telemetry_off_a_node_client_has_no_telemetry_layer()
    {
        IChatClient client = OllamaNodeClient.Create(new Uri("http://127.0.0.1:11434/v1"), "synthetic-model", "openai", 8192,
            TimeSpan.FromSeconds(30), NullLogger.Instance, nodeName: "worker-a");

        Assert.Null(client.GetService<NodeTelemetryChatClient>());
    }

    [Fact]
    public void An_unknown_telemetry_setting_is_ignored_with_one_warning_and_does_not_stop_startup()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fleet-telemetry-bogus-" + Guid.NewGuid().ToString("N"));
        var logger = new ContextWindowTestHelper.CollectingLogger();
        try
        {
            IDisposable? provider = null;
            Exception? thrown = Record.Exception(() => provider = FleetTelemetry.Configure("bogus", folder, logger));

            Assert.Null(thrown);
            Assert.Null(provider);
            Assert.False(FleetTelemetry.Enabled);
            Assert.False(Directory.Exists(folder));
            string warning = Assert.Single(logger.Warnings);
            Assert.Contains("off, file or otlp", warning);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Instrument_returns_the_same_instances_while_telemetry_is_off()
    {
        Assert.False(FleetTelemetry.Enabled);
        IChatClient client = new Model();
        AIAgent agent = new ChatClientAgent(client, name: "probe");

        Assert.Same(client, FleetTelemetry.Instrument(client, NullLoggerFactory.Instance));
        Assert.Same(agent, FleetTelemetry.Instrument(agent, new ServiceCollection().BuildServiceProvider()));
    }

    [Fact]
    public void Instrument_wraps_while_a_provider_is_alive_and_stops_when_it_is_disposed()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fleet-telemetry-enabled-" + Guid.NewGuid().ToString("N"));
        try
        {
            IChatClient client = new Model();
            AIAgent agent = new ChatClientAgent(client, name: "probe");
            IServiceProvider services = new ServiceCollection().BuildServiceProvider();
            IDisposable provider = FleetTelemetry.Configure("file", folder)!;

            Assert.True(FleetTelemetry.Enabled);
            Assert.NotSame(client, FleetTelemetry.Instrument(client, NullLoggerFactory.Instance));
            Assert.NotSame(agent, FleetTelemetry.Instrument(agent, services));

            provider.Dispose();

            Assert.False(FleetTelemetry.Enabled);
            Assert.Same(client, FleetTelemetry.Instrument(client, NullLoggerFactory.Instance));
            Assert.Same(agent, FleetTelemetry.Instrument(agent, services));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void File_mode_writes_only_allow_listed_tags_and_caps_long_text()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fleet-telemetry-allow-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FleetTelemetry.Configure("file", folder))
            {
                using Activity? span = FleetTelemetry.Source.StartActivity("allow-list probe", ActivityKind.Internal);
                Assert.NotNull(span);
                span.SetTag("gen_ai.input.messages", "private-x");
                span.SetTag("gen_ai.tool.call.arguments", "private-y");
                span.SetTag("gen_ai.tool.definitions", new string('d', 5000));
                span.SetTag("fleet.node", "n1");
                span.SetTag("gen_ai.usage.input_tokens", 7);
                span.SetTag("fleet.long_value", new string('x', 1000));
            }

            string text = string.Concat(Directory.GetFiles(folder, "telemetry-*.jsonl").Select(File.ReadAllText));
            Assert.DoesNotContain("private-x", text);
            Assert.DoesNotContain("private-y", text);
            Assert.DoesNotContain("gen_ai.tool.definitions", text);

            JsonElement tags = JsonDocument.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(line => line.Contains("allow-list probe")))
                .RootElement.GetProperty("tags");
            Assert.Equal("n1", tags.GetProperty("fleet.node").GetString());
            Assert.Equal(7, tags.GetProperty("gen_ai.usage.input_tokens").GetInt32());
            string longValue = tags.GetProperty("fleet.long_value").GetString()!;
            Assert.True(longValue.Length <= 256 + "...".Length, $"length {longValue.Length}");
            Assert.EndsWith("...", longValue);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    // The otlp mode registers the same processor ahead of its exporter; it is exercised here without starting an exporter.
    [Fact]
    public void The_allow_list_processor_drops_unlisted_tags_and_keeps_fleet_and_usage_tags()
    {
        using var source = new ActivitySource("AllowListProcessorTests");
        var stopped = new List<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = item => item.Name == "AllowListProcessorTests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        using Activity span = source.StartActivity("probe")!;
        span.SetTag("gen_ai.tool.definitions", new string('d', 5000));
        span.SetTag("gen_ai.input.messages", "private-x");
        span.SetTag("gen_ai.tool.description", "schema text");
        span.SetTag("gen_ai.tool.name", "list_directory");
        span.SetTag("fleet.node", "n1");
        span.SetTag("gen_ai.usage.input_tokens", 7);
        span.SetTag("fleet.long_value", new string('x', 1000));
        span.Stop();

        new FleetTelemetry.AllowListActivityProcessor().OnEnd(span);

        Assert.Null(span.GetTagItem("gen_ai.tool.definitions"));
        Assert.Null(span.GetTagItem("gen_ai.input.messages"));
        Assert.Null(span.GetTagItem("gen_ai.tool.description"));
        Assert.Equal("list_directory", span.GetTagItem("gen_ai.tool.name"));
        Assert.Equal("n1", span.GetTagItem("fleet.node"));
        Assert.Equal(7, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.True(((string)span.GetTagItem("fleet.long_value")!).Length <= 256 + "...".Length);
        Assert.DoesNotContain(span.TagObjects, tag => tag.Key == "gen_ai.tool.definitions");
    }

    [Fact]
    public void Off_does_not_build_a_provider()
    {
        Assert.Null(FleetTelemetry.Configure(null, Path.GetTempPath()));
        Assert.Null(FleetTelemetry.Configure("off", Path.GetTempPath()));
    }

    [Fact]
    public async Task Model_call_span_has_fleet_tags_and_does_not_record_prompt_text()
    {
        const string marker = "unique-private-prompt-marker";
        var stopped = new List<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (stopped) stopped.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        ChatOptions options = new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [FleetRoutingChatClient.RunnerTierKey] = "heavy",
                [FleetRoutingChatClient.RunnerRoleKey] = PlanRunnerToolPolicy.StepReviewRole
            }
        };

        Activity? round = FleetTelemetry.StartPlanRound("synthetic-plan", 1, 2, 3, 4, "worker-a", "worker-a");
        await Client(new Model()).GetResponseAsync([new ChatMessage(ChatRole.User, new string('x', 20_000) + marker)], options);
        round?.Dispose();

        Activity span = Assert.Single(stopped, item => item.DisplayName == "chat");
        Assert.Equal("AgentFleet.Model", span.Source.Name);
        Activity roundSpan = Assert.Single(stopped, item => item.DisplayName == "plan.round");
        Assert.Equal(round?.SpanId, span.ParentSpanId);
        Assert.Equal("synthetic-plan", roundSpan.GetTagItem("fleet.plan_id"));
        Assert.Equal(1, roundSpan.GetTagItem("fleet.step_id"));
        Assert.Equal(2, roundSpan.GetTagItem("fleet.attempt"));
        Assert.Equal(3, roundSpan.GetTagItem("fleet.round"));
        Assert.Equal(4, roundSpan.GetTagItem("fleet.rung"));
        Assert.Equal("worker-a", span.GetTagItem("fleet.node"));
        Assert.Equal("synthetic-model", span.GetTagItem("fleet.model"));
        Assert.Equal(8192, span.GetTagItem("fleet.num_ctx"));
        Assert.Equal(true, span.GetTagItem("fleet.followed_trim"));
        Assert.Equal("heavy", span.GetTagItem("fleet.tier"));
        Assert.Equal(PlanRunnerToolPolicy.StepReviewRole, span.GetTagItem("fleet.role"));
        Assert.DoesNotContain(marker, string.Join(" ", span.TagObjects.Select(tag => tag.Value)));
    }

    [Fact]
    public async Task Model_failure_span_has_http_status()
    {
        var stopped = new List<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(new Model(HttpStatusCode.BadGateway))
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal("502", Assert.Single(stopped, item => item.DisplayName == "chat").GetTagItem("fleet.http_status"));
    }

    [Fact]
    public void File_exporter_writes_json_lines_and_uses_a_dated_file()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fleet-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FleetTelemetry.Configure("file", folder))
            {
                using Activity? round = FleetTelemetry.StartPlanRound("synthetic-plan", 1, 2, 3, 4, "worker-a", "worker-a");
                using (Activity? check = FleetTelemetry.StartPlanCheck("synthetic-plan", 1, 2, 3, 4, "worker-a", "worker-a")) { }
            }

            string file = Assert.Single(Directory.GetFiles(folder, "telemetry-????????.jsonl"));
            string[] lines = File.ReadAllLines(file);
            Assert.Equal(2, lines.Length);
            JsonElement[] rows = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            JsonElement roundRow = Assert.Single(rows, row => row.GetProperty("name").GetString() == "plan.round");
            JsonElement checkRow = Assert.Single(rows, row => row.GetProperty("name").GetString() == "plan.check");
            Assert.Equal("synthetic-plan", roundRow.GetProperty("tags").GetProperty("fleet.plan_id").GetString());
            Assert.Equal(roundRow.GetProperty("span_id").GetString(), checkRow.GetProperty("parent_span_id").GetString());
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
