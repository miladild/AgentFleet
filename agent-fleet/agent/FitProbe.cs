using System.Text.Json;

namespace AgentFleet;

/// <summary>What one measured context size turned out to be.</summary>
internal enum FitStatus
{
    Ok,
    DoesNotFit,
    Error
}

/// <summary>
/// One context size measured for one model on one machine. Speed and load time come from the model's own reply;
/// <see cref="GpuShare"/> is the part of the model that sits in graphics memory (1 means all of it, 0 means none).
/// </summary>
internal sealed record FitPoint(
    int Context,
    FitStatus Status,
    double? GpuShare,
    double? TokensPerSecond,
    double? LoadSeconds,
    string? Detail);

/// <summary>How much speed and graphics-memory fit a recommendation asks for.</summary>
/// <summary>
/// How much speed and graphics-memory fit a recommendation asks for. A model that already spills to the processor
/// at the smallest size is held to the stricter <see cref="ConstrainedSpeedFraction"/>, because each larger size
/// spills more and the speed drops further.
/// </summary>
internal sealed record FitThresholds(double MinSpeedFraction = 0.70, double FullFitShare = 0.90, double ConstrainedSpeedFraction = 0.80);

/// <summary>The context size to use for the one model that was measured. It never names a model.</summary>
internal sealed record FitRecommendation(int? Context, bool DoesNotFullyFit, string Reason);

/// <summary>
/// Measures how one model behaves on one Ollama machine at a given context size, and recommends a context size from
/// those measurements. It only ever looks at the model it was given; it never chooses or ranks models.
/// </summary>
internal sealed class FitProbe(HttpClient client)
{
    private const int UnloadPolls = 10;
    private static readonly TimeSpan UnloadPollDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FinalUnloadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Loads the model at one context size, measures a short reply, and unloads the model again. The unload always
    /// runs, even when the measurement fails or is cancelled, and no other loaded model is touched.
    /// </summary>
    public async Task<FitPoint> MeasureAsync(Uri root, string model, int context, CancellationToken cancellationToken)
    {
        try
        {
            return await MeasureOnceAsync(root, model, context, cancellationToken);
        }
        finally
        {
            await UnloadAfterMeasureAsync(root, model);
        }
    }

    /// <summary>Recommends the largest context size that keeps enough speed, judged against the smallest size measured.</summary>
    public static FitRecommendation Recommend(IReadOnlyList<FitPoint> points, FitThresholds? thresholds = null)
    {
        thresholds ??= new FitThresholds();
        List<FitPoint> ok = points.Where(point => point.Status == FitStatus.Ok).OrderBy(point => point.Context).ToList();
        if (ok.Count == 0)
        {
            return new FitRecommendation(null, false, "No size could be measured.");
        }

        if (ok.All(point => point.GpuShare == 0))
        {
            return new FitRecommendation(
                null,
                false,
                "This machine runs the model on its processor; there is no graphics-card fit to recommend.");
        }

        FitPoint? baseline = ok.FirstOrDefault(point => point.TokensPerSecond is not null);
        if (baseline is null)
        {
            return new FitRecommendation(null, false, "No speed could be measured.");
        }

        bool doesNotFullyFit = baseline.GpuShare is double share && share < thresholds.FullFitShare;
        double fraction = doesNotFullyFit
            ? Math.Max(thresholds.ConstrainedSpeedFraction, thresholds.MinSpeedFraction)
            : thresholds.MinSpeedFraction;
        double floor = baseline.TokensPerSecond!.Value * fraction;
        // A model that fits at the smallest size is only offered sizes that still fit on the card; one that already
        // spills is judged on speed alone, against the stricter floor.
        // The baseline always keeps its own speed, so it is the fallback when no other size is listed as a candidate.
        FitPoint largest = ok
            .Where(point => point.TokensPerSecond is double speed && speed >= floor &&
                            (doesNotFullyFit || point.GpuShare is null || point.GpuShare >= thresholds.FullFitShare))
            .MaxBy(point => point.Context) ?? baseline;

        string percent = Math.Round(fraction * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        string reason =
            $"{largest.Context} keeps at least {percent}% of the speed at the smallest size measured ({baseline.Context})";
        reason += doesNotFullyFit ? "." : " and stays on the graphics card.";
        if (doesNotFullyFit)
        {
            reason += " The model does not fully fit on the graphics card even at the smallest size, so a stricter speed floor was used.";
        }

        reason += " Speed only, not coding skill.";
        return new FitRecommendation(largest.Context, doesNotFullyFit, reason);
    }

    private async Task<FitPoint> MeasureOnceAsync(Uri root, string model, int context, CancellationToken cancellationToken)
    {
        try
        {
            await UnloadAsync(root, model, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failed unload is not fatal: the measurement still runs and reports what it sees.
        }

        await WaitUntilUnloadedAsync(root, model, cancellationToken);

        try
        {
            string nonce = Guid.NewGuid().ToString("D");
            using HttpRequestMessage chat = new(HttpMethod.Post, new Uri(root, "api/chat"))
            {
                Content = JsonContent.Create(new
                {
                    model,
                    messages = new[]
                    {
                        new { role = "user", content = $"Ref {nonce}. Count from 1 to 60 separated by commas and write nothing else." }
                    },
                    stream = false,
                    think = false,
                    keep_alive = "1m",
                    options = new { num_ctx = context, num_predict = 64 }
                })
            };

            using HttpResponseMessage response = await client.SendAsync(chat, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (body.Contains("memory", StringComparison.OrdinalIgnoreCase))
                {
                    return new FitPoint(context, FitStatus.DoesNotFit, null, null, null, Shorten(body));
                }

                return Failed(context, $"HTTP {(int)response.StatusCode}");
            }

            using JsonDocument reply = JsonDocument.Parse(body);
            long? evalCount = ReadLong(reply.RootElement, "eval_count");
            long? evalDuration = ReadLong(reply.RootElement, "eval_duration");
            long? loadDuration = ReadLong(reply.RootElement, "load_duration");

            double? tokensPerSecond = evalCount is > 0 && evalDuration is > 0
                ? evalCount.Value / (evalDuration.Value / 1e9)
                : null;
            double? loadSeconds = loadDuration is long load ? load / 1e9 : null;

            double? gpuShare = await MeasureGpuShareAsync(root, model, cancellationToken);
            return new FitPoint(context, FitStatus.Ok, gpuShare, tokensPerSecond, loadSeconds, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A timeout or a broken reply is a measurement error, not a crash of the whole probe.
            return Failed(context, exception is JsonException ? "reply was not JSON" : exception is TaskCanceledException ? "timed out" : Shorten(exception.Message));
        }
    }

    private async Task<double?> MeasureGpuShareAsync(Uri root, string model, CancellationToken cancellationToken)
    {
        try
        {
            LoadedState? state = await ReadLoadedStateAsync(root, model, cancellationToken);
            if (state is not { Listed: true, Size: > 0 } listed)
            {
                return null;
            }

            return Math.Clamp((double)listed.SizeVram / listed.Size, 0, 1);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Polls the loaded list until the model is gone. A poll that cannot be read simply stops the waiting.
    private async Task WaitUntilUnloadedAsync(Uri root, string model, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < UnloadPolls; attempt++)
        {
            LoadedState? state = await ReadLoadedStateAsync(root, model, cancellationToken);
            if (state is not { Listed: true })
            {
                return;
            }

            await Task.Delay(UnloadPollDelay, cancellationToken);
        }
    }

    // Asks Ollama to drop this one model from memory. Other loaded models are left alone.
    private async Task UnloadAsync(Uri root, string model, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(root, "api/generate"))
        {
            Content = JsonContent.Create(new { model, keep_alive = 0 })
        };
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
    }

    // Runs after every measurement, including failed and cancelled ones. It uses its own short timeout so a cancelled
    // caller still gets the model unloaded, and it ignores errors because there is nothing left to report them to.
    private async Task UnloadAfterMeasureAsync(Uri root, string model)
    {
        try
        {
            using CancellationTokenSource timeout = new(FinalUnloadTimeout);
            await UnloadAsync(root, model, timeout.Token);
        }
        catch (Exception)
        {
            // Ignored on purpose: see the comment above.
        }
    }

    private async Task<LoadedState?> ReadLoadedStateAsync(Uri root, string model, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await ReadPsAsync(root, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string expected = NormalizeTag(model);
            foreach (JsonElement entry in models.EnumerateArray())
            {
                string? name = entry.TryGetProperty("name", out JsonElement nameValue) && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString()
                    : null;
                if (name is null || !string.Equals(NormalizeTag(name), expected, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new LoadedState(true, ReadLong(entry, "size") ?? 0, ReadLong(entry, "size_vram") ?? 0);
            }

            return new LoadedState(false, 0, 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> ReadPsAsync(Uri root, CancellationToken cancellationToken) =>
        await client.GetAsync(new Uri(root, "api/ps"), cancellationToken);

    /// <summary>
    /// Whether Ollama holds any model in memory: true or false, or null when the list cannot be read. A caller that must
    /// not disturb a busy machine should treat null as "busy", since it cannot tell.
    /// </summary>
    public async Task<bool?> AnyModelLoadedAsync(Uri root, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await ReadPsAsync(root, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array
                ? models.GetArrayLength() > 0
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static FitPoint Failed(int context, string detail) => new(context, FitStatus.Error, null, null, null, Shorten(detail));

    private static string Shorten(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    // "name" without a tag means the ":latest" tag, the same way Ollama reads it.
    private static string NormalizeTag(string name)
    {
        string trimmed = name.Trim();
        return trimmed.LastIndexOf(':') > trimmed.LastIndexOf('/') ? trimmed : trimmed + ":latest";
    }

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : null;

    private sealed record LoadedState(bool Listed, long Size, long SizeVram);
}
