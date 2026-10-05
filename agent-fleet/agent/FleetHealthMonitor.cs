using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentFleet;

internal sealed record NodeHealthSnapshot(
    string Name,
    string Model,
    bool Reachable,
    bool ModelAvailable,
    bool Answers,
    DateTimeOffset CheckedAtUtc,
    string? Failure)
{
    public bool Ready => Reachable && ModelAvailable && Answers;
}

internal sealed class FleetHealthMonitor
{
    public const string HttpClientName = "fleet-health";
    internal static readonly TimeSpan InferenceProbeInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan InferenceProbeTimeout = TimeSpan.FromSeconds(120);

    private readonly FleetOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, NodeHealthSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset AtUtc, bool Answers, string? Failure)> _inference = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _probeLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Failure)> _coolingDown = new(StringComparer.Ordinal);

    public FleetHealthMonitor(FleetOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<NodeHealthSnapshot> GetNodeAsync(
        string nodeName,
        CancellationToken cancellationToken = default,
        bool forceProbe = false,
        bool forceInferenceProbe = false)
    {
        // A request that started before a machine was removed from the config can still ask about it.
        if (!_options.TryGetNode(nodeName, out FleetNodeDefinition? node))
        {
            return new NodeHealthSnapshot(nodeName, string.Empty, false, false, false, DateTimeOffset.UtcNow, "not_configured");
        }

        // A machine that just failed a real request (it timed out, or Ollama broke) rests for a while even though its
        // model list still answers: otherwise every model call of a plan step would wait out the same timeout again.
        if (!forceInferenceProbe && _coolingDown.TryGetValue(node.Name, out (DateTimeOffset Until, string Failure) cooling))
        {
            if (cooling.Until > DateTimeOffset.UtcNow)
            {
                return new NodeHealthSnapshot(node.Name, node.Model, Reachable: false, ModelAvailable: false, Answers: false, DateTimeOffset.UtcNow, cooling.Failure);
            }

            _coolingDown.TryRemove(node.Name, out _);
        }

        if (!forceProbe && !forceInferenceProbe && TryGetFreshSnapshot(node.Name, out NodeHealthSnapshot? snapshot))
        {
            return snapshot;
        }

        SemaphoreSlim probeLock = _probeLocks.GetOrAdd(node.Name, _ => new SemaphoreSlim(1, 1));
        await probeLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceProbe && !forceInferenceProbe && TryGetFreshSnapshot(node.Name, out snapshot))
            {
                return snapshot;
            }

            NodeHealthSnapshot probedSnapshot = await ProbeAsync(node, forceInferenceProbe, cancellationToken);
            _snapshots[node.Name] = probedSnapshot;
            if (forceInferenceProbe && probedSnapshot.Ready) _coolingDown.TryRemove(node.Name, out _);
            return probedSnapshot;
        }
        finally
        {
            probeLock.Release();
        }
    }

    public async Task<IReadOnlyList<NodeHealthSnapshot>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        Task<NodeHealthSnapshot>[] probes = _options.Nodes
            .Select(node => GetNodeAsync(node.Name, cancellationToken))
            .ToArray();

        return await Task.WhenAll(probes);
    }

    /// <summary>Drops every cached result, so a machine whose address or model changed is probed afresh.</summary>
    public void Forget()
    {
        _snapshots.Clear();
        _inference.Clear();
        _coolingDown.Clear();
    }

    /// <summary>Takes a machine out of use for a while after a real request failed on it, whatever its probes say.</summary>
    public void CoolDown(string nodeName, string failure, TimeSpan duration)
    {
        MarkUnavailable(nodeName, failure);
        if (_options.TryGetNode(nodeName, out FleetNodeDefinition? node))
        {
            _coolingDown[node.Name] = (DateTimeOffset.UtcNow + duration, failure);
        }
    }

    public void MarkUnavailable(string nodeName, string failure)
    {
        if (!_options.TryGetNode(nodeName, out FleetNodeDefinition? node))
        {
            return;
        }

        _snapshots[node.Name] = new NodeHealthSnapshot(
            node.Name,
            node.Model,
            Reachable: false,
            ModelAvailable: false,
            Answers: false,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Failure: failure);
        _inference.TryRemove(node.Name, out _);
    }

    /// <summary>A real response proves the configured model can answer without an extra generation request.</summary>
    public void MarkAnswered(string nodeName)
    {
        if (!_options.TryGetNode(nodeName, out FleetNodeDefinition? node)) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _coolingDown.TryRemove(node.Name, out _);
        _inference[node.Name] = (now, true, null);
        _snapshots[node.Name] = new NodeHealthSnapshot(node.Name, node.Model, true, true, true, now, null);
    }

    public void MarkInferenceFailed(string nodeName, string failure)
    {
        if (!_options.TryGetNode(nodeName, out FleetNodeDefinition? node)) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _inference[node.Name] = (now, false, failure);
        _snapshots[node.Name] = new NodeHealthSnapshot(node.Name, node.Model, true, true, false, now, failure);
    }

    private bool TryGetFreshSnapshot(string nodeName, [NotNullWhen(true)] out NodeHealthSnapshot? snapshot)
    {
        if (_snapshots.TryGetValue(nodeName, out NodeHealthSnapshot? cachedSnapshot) &&
            DateTimeOffset.UtcNow - cachedSnapshot.CheckedAtUtc <= _options.HealthCacheDuration)
        {
            snapshot = cachedSnapshot;
            return true;
        }

        snapshot = null;
        return false;
    }

    private async Task<NodeHealthSnapshot> ProbeAsync(FleetNodeDefinition node, bool forceInferenceProbe, CancellationToken cancellationToken)
    {
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(_options.HealthProbeTimeout);
        bool inferenceStarted = false;
        bool modelListed = false;

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(node.TagsEndpoint, probeCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                _inference.TryRemove(node.Name, out _);
                return Unavailable(node, "http_error", reachable: true);
            }

            OllamaTagsResponse? tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
                cancellationToken: probeCancellation.Token);
            string expectedModel = NormalizeModelTag(node.Model);
            modelListed = tags?.Models?.Any(model =>
                string.Equals(NormalizeModelTag(model.Name), expectedModel, StringComparison.Ordinal)) == true;
            if (!modelListed)
            {
                _inference.TryRemove(node.Name, out _);
                return new NodeHealthSnapshot(node.Name, node.Model, true, false, false, DateTimeOffset.UtcNow, "model_missing");
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!forceInferenceProbe && _inference.TryGetValue(node.Name, out var recent) &&
                now - recent.AtUtc < InferenceProbeInterval)
            {
                return new NodeHealthSnapshot(node.Name, node.Model, true, true, recent.Answers, now, recent.Failure);
            }

            using var inferenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inferenceCancellation.CancelAfter(InferenceProbeTimeout);
            using HttpRequestMessage? request = await InferenceProbeRequestAsync(client, node, forceInferenceProbe, inferenceCancellation.Token);
            if (request is null)
            {
                // Loading this model just to ask is not worth it (another model is working on this machine, or this is the hub's model: see
                // InferenceProbeRequestAsync): the node is taken to answer until a real request or a forced probe says it does not.
                _inference[node.Name] = (now, true, null);
                return new NodeHealthSnapshot(node.Name, node.Model, true, true, true, now, null);
            }

            inferenceStarted = true;
            using HttpResponseMessage inferenceResponse = await client.SendAsync(request, inferenceCancellation.Token);
            if (!inferenceResponse.IsSuccessStatusCode)
            {
                string failure = $"inference_http_{(int)inferenceResponse.StatusCode}";
                _inference[node.Name] = (DateTimeOffset.UtcNow, false, failure);
                return new NodeHealthSnapshot(node.Name, node.Model, true, true, false, DateTimeOffset.UtcNow, failure);
            }

            using JsonDocument? completion = await inferenceResponse.Content.ReadFromJsonAsync<JsonDocument>(
                cancellationToken: inferenceCancellation.Token);
            JsonElement choices = default;
            bool hasChoice = completion is not null && completion.RootElement.TryGetProperty("choices", out choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0;
            bool hasFinishReason = hasChoice && choices[0].TryGetProperty("finish_reason", out JsonElement finishReason) &&
                finishReason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(finishReason.GetString());
            bool hasMessage = hasChoice && choices[0].TryGetProperty("message", out JsonElement message) &&
                (message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString()) ||
                 message.TryGetProperty("reasoning", out JsonElement reasoning) && reasoning.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reasoning.GetString()));
            // Ollama's native API ends its answer with "done": true; an OpenAI-compatible server answers with choices.
            bool nativeAnswer = completion is not null && completion.RootElement.TryGetProperty("done", out JsonElement done) &&
                done.ValueKind == JsonValueKind.True;
            bool answered = nativeAnswer || hasChoice && (hasFinishReason || hasMessage);
            string? inferenceFailure = answered ? null : "inference_empty";
            DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
            _inference[node.Name] = (checkedAt, answered, inferenceFailure);
            return new NodeHealthSnapshot(node.Name, node.Model, true, true, answered, checkedAt, inferenceFailure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string failure = inferenceStarted ? "inference_timeout" : "timeout";
            if (inferenceStarted) _inference[node.Name] = (DateTimeOffset.UtcNow, false, failure);
            else _inference.TryRemove(node.Name, out _);
            return Unavailable(node, failure, reachable: inferenceStarted, modelAvailable: modelListed);
        }
        catch (HttpRequestException)
        {
            if (inferenceStarted) _inference[node.Name] = (DateTimeOffset.UtcNow, false, "unreachable");
            else _inference.TryRemove(node.Name, out _);
            return Unavailable(node, "unreachable", modelAvailable: modelListed);
        }
        catch (JsonException)
        {
            string failure = inferenceStarted ? "inference_invalid_response" : "invalid_response";
            if (inferenceStarted) _inference[node.Name] = (DateTimeOffset.UtcNow, false, failure);
            else _inference.TryRemove(node.Name, out _);
            return Unavailable(node, failure, reachable: true, modelAvailable: modelListed);
        }
    }

    // Ollama's OpenAI-compatible endpoint takes no num_ctx, so a probe through it loads the model at Ollama's own default
    // window (measured on a worker: 262144 tokens, two thirds of the model out of graphics memory and slow), and the next
    // real request reloads it at the size the work needs, 10 to 25 seconds each way. A probe went out every five minutes.
    // Through the native API, at the window the model already has, it changes nothing; with nothing loaded it asks for the
    // smallest window the fleet uses. A server that is not Ollama (api "openai") is probed through its OpenAI endpoint.
    // A routine probe returns null when a different model is loaded on the machine and this one is not: loading a second
    // model to ask it a question can push the working one out of graphics memory (measured: the vision model and the coding
    // model on one worker took turns). A forced probe, which a plan asks for after an outage, always goes through.
    internal static async Task<HttpRequestMessage?> InferenceProbeRequestAsync(
        HttpClient client, FleetNodeDefinition node, bool force, CancellationToken cancellationToken)
    {
        if (string.Equals(node.Api, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpRequestMessage(HttpMethod.Post, new Uri(node.OpenAiEndpoint.ToString().TrimEnd('/') + "/chat/completions"))
            {
                Content = JsonContent.Create(new
                {
                    model = node.Model,
                    messages = new[] { new { role = "user", content = "Reply only OK." } },
                    max_tokens = 32,
                    stream = false
                })
            };
        }

        (int? loadedWindow, bool otherModelLoaded) = await LoadedAsync(client, node, cancellationToken);
        if (loadedWindow is null && otherModelLoaded && !force)
        {
            return null;
        }

        // The hub's model is the largest on the fleet and sits on the owner's own machine: a routine probe does not load it. Measured: a probe
        // every five minutes against Ollama's five-minute keep-alive kept a 22 GB model resident, and the graphics memory full, all day
        // while nobody used it. It is asked when it is loaded anyway, and when a plan asks after an outage.
        if (loadedWindow is null && node.Fallback && !force)
        {
            return null;
        }

        int window = loadedWindow ?? ContextSizeChatClient.SmallestSize;
        return new HttpRequestMessage(HttpMethod.Post, new Uri(node.OllamaRoot, "api/chat"))
        {
            Content = JsonContent.Create(new
            {
                model = node.Model,
                messages = new[] { new { role = "user", content = "Reply only OK." } },
                stream = false,
                think = false,
                options = new { num_ctx = window, num_predict = 16 }
            })
        };
    }

    // What Ollama's /api/ps says is loaded: the window this node's model has right now (null when it is not loaded), and
    // whether some other model is loaded. Ollama that cannot say counts as nothing loaded.
    private static async Task<(int? Window, bool OtherModelLoaded)> LoadedAsync(
        HttpClient client, FleetNodeDefinition node, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(node.OllamaRoot, "api/ps"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            using JsonDocument? loaded = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
            if (loaded is null || !loaded.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return (null, false);
            }

            string expected = NormalizeModelTag(node.Model);
            int? window = null;
            bool other = false;
            foreach (JsonElement model in models.EnumerateArray())
            {
                string? name = model.TryGetProperty("name", out JsonElement nameValue) && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString()
                    : null;
                if (!string.Equals(NormalizeModelTag(name), expected, StringComparison.Ordinal))
                {
                    other |= !string.IsNullOrEmpty(name);
                }
                else if (model.TryGetProperty("context_length", out JsonElement length) && length.ValueKind == JsonValueKind.Number &&
                         length.TryGetInt32(out int size) && size > 0)
                {
                    window = size;
                }
            }

            return (window, other);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return (null, false);
        }
    }

    // Ollama's /api/tags returns the full "name:tag" form (e.g. "rnj-1:latest") even
    // when a model was pulled and referenced without an explicit tag. Comparing raw
    // strings made every untagged model in FleetOptions register as "missing" even
    // though it was pulled and working - this normalizes both sides the same way
    // Ollama itself treats a missing tag (defaulting to "latest").
    private static string NormalizeModelTag(string? modelName) =>
        string.IsNullOrEmpty(modelName)
            ? string.Empty
            : modelName.Contains(':') ? modelName : $"{modelName}:latest";

    private static NodeHealthSnapshot Unavailable(FleetNodeDefinition node, string failure, bool reachable = false, bool modelAvailable = false) =>
        new(
            node.Name,
            node.Model,
            Reachable: reachable,
            ModelAvailable: modelAvailable,
            Answers: false,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Failure: failure);

    private sealed record OllamaTagsResponse(IReadOnlyList<OllamaModel>? Models);

    private sealed record OllamaModel(string? Name);
}
