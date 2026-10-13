using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentFleet.Tests;

/// <summary>
/// The fit probe loads one model at one context size, reads its speed and graphics-memory share, unloads it again,
/// and turns a set of measured sizes into one recommended context size. Everything runs against a fake Ollama.
/// </summary>
public sealed class FitProbeTests
{
    private const string ModelName = "demo:8b";
    private static readonly Uri Root = new("http://node.example.test:11434/");
    private const string EmptyLoadedList = "{\"models\":[]}";
    private const string ModelLoaded = "{\"models\":[{\"name\":\"demo:8b\",\"model\":\"demo:8b\",\"size\":8000000000,\"size_vram\":6000000000}]}";
    private const string ChatReply =
        "{\"model\":\"demo:8b\",\"message\":{\"role\":\"assistant\",\"content\":\"1, 2, 3\"},\"done\":true," +
        "\"load_duration\":2000000000,\"eval_count\":64,\"eval_duration\":4000000000}";

    private sealed record Seen(HttpMethod Method, string Path, string Body);

    // A fake Ollama that records every request. The loaded-model list replies run in order and the last one repeats.
    private sealed class FakeOllama : HttpMessageHandler
    {
        public readonly List<Seen> Requests = [];

        public List<string> PsReplies { get; set; } = [EmptyLoadedList];

        public HttpStatusCode ChatStatus { get; set; } = HttpStatusCode.OK;

        public string ChatBody { get; set; } = ChatReply;

        // When set, the chat request cancels this source and then waits, so the caller sees a cancellation mid-measurement.
        public CancellationTokenSource? CancelOnChat { get; set; }

        private int _psCalls;

        public int UnloadCount => Requests.Count(seen => seen.Method == HttpMethod.Post && seen.Path == "/api/generate" && IsUnload(seen.Body));

        public static bool IsUnload(string body) =>
            JsonDocument.Parse(body).RootElement.TryGetProperty("keep_alive", out JsonElement keepAlive) &&
            keepAlive.ValueKind == JsonValueKind.Number && keepAlive.GetInt32() == 0;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            lock (Requests)
            {
                Requests.Add(new Seen(request.Method, path, body));
            }

            switch (path)
            {
                case "/api/ps":
                    int index = Math.Min(_psCalls++, PsReplies.Count - 1);
                    return Json(HttpStatusCode.OK, PsReplies[index]);
                case "/api/chat":
                    if (CancelOnChat is not null)
                    {
                        CancelOnChat.Cancel();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

                    return Json(ChatStatus, ChatBody);
                default:
                    return Json(HttpStatusCode.OK, "{}");
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static (FitProbe Probe, FakeOllama Fake) Probe(FakeOllama fake) => (new FitProbe(new HttpClient(fake)), fake);

    private static JsonDocument Parse(string body) => JsonDocument.Parse(body);

    private static FitPoint Ok(int context, double share, double speed) => new(context, FitStatus.Ok, share, speed, 2, null);

    [Fact]
    public async Task Measures_speed_load_time_and_graphics_share_from_a_successful_run()
    {
        FakeOllama fake = new() { PsReplies = [EmptyLoadedList, ModelLoaded] };
        (FitProbe probe, _) = Probe(fake);

        FitPoint point = await probe.MeasureAsync(Root, ModelName, 16384, CancellationToken.None);

        Assert.Equal(FitStatus.Ok, point.Status);
        Assert.Equal(0.75, point.GpuShare);
        Assert.Equal(16.0, point.TokensPerSecond);
        Assert.Equal(2.0, point.LoadSeconds);
        Assert.Equal(16384, point.Context);
    }

    [Fact]
    public async Task Unloads_first_then_chats_with_the_expected_body_and_unloads_last()
    {
        FakeOllama fake = new() { PsReplies = [EmptyLoadedList, ModelLoaded] };
        (FitProbe probe, _) = Probe(fake);

        await probe.MeasureAsync(Root, ModelName, 16384, CancellationToken.None);

        Seen first = fake.Requests[0];
        Assert.Equal(HttpMethod.Post, first.Method);
        Assert.Equal("/api/generate", first.Path);
        using (JsonDocument unload = Parse(first.Body))
        {
            Assert.Equal(ModelName, unload.RootElement.GetProperty("model").GetString());
            Assert.Equal(0, unload.RootElement.GetProperty("keep_alive").GetInt32());
        }

        Seen chat = fake.Requests.Single(seen => seen.Path == "/api/chat");
        using (JsonDocument body = Parse(chat.Body))
        {
            JsonElement root = body.RootElement;
            Assert.Equal(ModelName, root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.False(root.GetProperty("think").GetBoolean());
            Assert.Equal("1m", root.GetProperty("keep_alive").GetString());
            Assert.Equal(16384, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
            Assert.Equal(64, root.GetProperty("options").GetProperty("num_predict").GetInt32());
            string content = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Assert.StartsWith("Ref ", content);
        }

        Seen last = fake.Requests[^1];
        Assert.Equal(HttpMethod.Post, last.Method);
        Assert.Equal("/api/generate", last.Path);
        Assert.True(FakeOllama.IsUnload(last.Body));
    }

    [Fact]
    public async Task Two_measurements_of_the_same_size_use_different_nonces()
    {
        FakeOllama fake = new();
        (FitProbe probe, _) = Probe(fake);

        await probe.MeasureAsync(Root, ModelName, 8192, CancellationToken.None);
        await probe.MeasureAsync(Root, ModelName, 8192, CancellationToken.None);

        List<string> prompts = fake.Requests
            .Where(seen => seen.Path == "/api/chat")
            .Select(seen => Parse(seen.Body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!)
            .ToList();
        Assert.Equal(2, prompts.Count);
        Assert.NotEqual(prompts[0], prompts[1]);
    }

    [Fact]
    public async Task A_memory_error_reports_does_not_fit_and_still_unloads_the_model()
    {
        FakeOllama fake = new()
        {
            ChatStatus = HttpStatusCode.InternalServerError,
            ChatBody = "{\"error\":\"model requires more system memory (2.1 GiB) than is available\"}"
        };
        (FitProbe probe, _) = Probe(fake);

        FitPoint point = await probe.MeasureAsync(Root, ModelName, 32768, CancellationToken.None);

        Assert.Equal(FitStatus.DoesNotFit, point.Status);
        Assert.NotNull(point.Detail);
        Assert.Contains("more system memory", point.Detail);
        Assert.Equal(2, fake.UnloadCount);
    }

    [Fact]
    public async Task Any_other_server_error_reports_an_error_status()
    {
        FakeOllama fake = new() { ChatStatus = HttpStatusCode.InternalServerError, ChatBody = "boom" };
        (FitProbe probe, _) = Probe(fake);

        FitPoint point = await probe.MeasureAsync(Root, ModelName, 8192, CancellationToken.None);

        Assert.Equal(FitStatus.Error, point.Status);
        Assert.NotNull(point.Detail);
    }

    [Fact]
    public async Task Cancelling_during_the_chat_throws_and_still_unloads_the_model()
    {
        using CancellationTokenSource cancellation = new();
        FakeOllama fake = new() { CancelOnChat = cancellation };
        (FitProbe probe, _) = Probe(fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.MeasureAsync(Root, ModelName, 8192, cancellation.Token));

        Assert.Equal(2, fake.UnloadCount);
    }

    [Fact]
    public void Recommends_the_largest_size_that_keeps_most_speed_on_a_six_gigabyte_card()
    {
        FitRecommendation result = FitProbe.Recommend(
        [
            Ok(12288, 1.00, 21),
            Ok(16384, 0.92, 15),
            Ok(24576, 0.75, 11),
            Ok(32768, 0.60, 8)
        ]);

        Assert.Equal(16384, result.Context);
        Assert.False(result.DoesNotFullyFit);
    }

    [Fact]
    public void Recommends_the_largest_size_that_keeps_most_speed_on_an_eight_gigabyte_card()
    {
        FitRecommendation result = FitProbe.Recommend(
        [
            Ok(12288, 1.00, 30),
            Ok(16384, 0.98, 29),
            Ok(24576, 0.95, 26),
            Ok(32768, 0.70, 18)
        ]);

        Assert.Equal(24576, result.Context);
    }

    [Fact]
    public void Flags_a_smallest_size_that_does_not_fully_fit_on_the_card()
    {
        FitRecommendation result = FitProbe.Recommend(
        [
            Ok(12288, 0.80, 30),
            Ok(16384, 0.80, 28),
            Ok(24576, 0.50, 20)
        ]);

        Assert.True(result.DoesNotFullyFit);
        Assert.Contains("does not fully fit", result.Reason);
    }

    [Fact]
    public void A_model_that_runs_only_on_the_processor_gets_no_context_recommendation()
    {
        FitRecommendation result = FitProbe.Recommend(
        [
            Ok(12288, 0, 6),
            Ok(16384, 0, 5)
        ]);

        Assert.Null(result.Context);
        Assert.Equal(
            "This machine runs the model on its processor; there is no graphics-card fit to recommend.",
            result.Reason);
    }

    [Fact]
    public void No_measured_size_gives_no_recommendation()
    {
        FitRecommendation result = FitProbe.Recommend(
        [
            new FitPoint(12288, FitStatus.DoesNotFit, null, null, null, "memory"),
            new FitPoint(16384, FitStatus.Error, null, null, null, "boom")
        ]);

        Assert.Null(result.Context);
        Assert.Equal("No size could be measured.", result.Reason);
    }

    [Fact]
    public void The_order_of_the_points_does_not_change_the_recommendation()
    {
        List<FitPoint> sorted =
        [
            Ok(12288, 1.00, 21),
            Ok(16384, 0.92, 15),
            Ok(24576, 0.75, 11),
            Ok(32768, 0.60, 8)
        ];
        List<FitPoint> shuffled = [sorted[2], sorted[0], sorted[3], sorted[1]];

        FitRecommendation expected = FitProbe.Recommend(sorted);
        FitRecommendation actual = FitProbe.Recommend(shuffled);

        Assert.Equal(expected, actual);
        Assert.Equal(16384, actual.Context);
    }

    [Fact]
    public void The_speed_threshold_argument_is_used()
    {
        FitRecommendation result = FitProbe.Recommend(
        [
            Ok(12288, 1.00, 21),
            Ok(16384, 0.92, 15),
            Ok(24576, 0.75, 11),
            Ok(32768, 0.60, 8)
        ], new FitThresholds(MinSpeedFraction: 0.5));

        Assert.Equal(24576, result.Context);
    }
}
