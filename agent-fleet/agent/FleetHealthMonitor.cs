using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;

namespace AgentFleet;

internal sealed record NodeHealthSnapshot(
    string Name,
    string Model,
    bool Reachable,
    bool ModelAvailable,
    DateTimeOffset CheckedAtUtc,
    string? Failure)
{
    public bool Ready => Reachable && ModelAvailable;
}

internal sealed class FleetHealthMonitor
{
    public const string HttpClientName = "fleet-health";

    private readonly FleetOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, NodeHealthSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _probeLocks = new(StringComparer.Ordinal);

    public FleetHealthMonitor(FleetOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<NodeHealthSnapshot> GetNodeAsync(
        string nodeName,
        CancellationToken cancellationToken = default,
        bool forceProbe = false)
    {
        // A request that started before a machine was removed from the config can still ask about it.
        if (!_options.TryGetNode(nodeName, out FleetNodeDefinition? node))
        {
            return new NodeHealthSnapshot(nodeName, string.Empty, false, false, DateTimeOffset.UtcNow, "not_configured");
        }

        if (!forceProbe && TryGetFreshSnapshot(node.Name, out NodeHealthSnapshot? snapshot))
        {
            return snapshot;
        }

        SemaphoreSlim probeLock = _probeLocks.GetOrAdd(node.Name, _ => new SemaphoreSlim(1, 1));
        await probeLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceProbe && TryGetFreshSnapshot(node.Name, out snapshot))
            {
                return snapshot;
            }

            NodeHealthSnapshot probedSnapshot = await ProbeAsync(node, cancellationToken);
            _snapshots[node.Name] = probedSnapshot;
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
    public void Forget() => _snapshots.Clear();

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
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Failure: failure);
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

    private async Task<NodeHealthSnapshot> ProbeAsync(FleetNodeDefinition node, CancellationToken cancellationToken)
    {
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(_options.HealthProbeTimeout);

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(node.TagsEndpoint, probeCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(node, "http_error");
            }

            OllamaTagsResponse? tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
                cancellationToken: probeCancellation.Token);
            string expectedModel = NormalizeModelTag(node.Model);
            bool modelAvailable = tags?.Models?.Any(model =>
                string.Equals(NormalizeModelTag(model.Name), expectedModel, StringComparison.Ordinal)) == true;

            return new NodeHealthSnapshot(
                node.Name,
                node.Model,
                Reachable: true,
                ModelAvailable: modelAvailable,
                CheckedAtUtc: DateTimeOffset.UtcNow,
                Failure: modelAvailable ? null : "model_missing");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(node, "timeout");
        }
        catch (HttpRequestException)
        {
            return Unavailable(node, "unreachable");
        }
        catch (System.Text.Json.JsonException)
        {
            return Unavailable(node, "invalid_response");
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

    private static NodeHealthSnapshot Unavailable(FleetNodeDefinition node, string failure) =>
        new(
            node.Name,
            node.Model,
            Reachable: false,
            ModelAvailable: false,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Failure: failure);

    private sealed record OllamaTagsResponse(IReadOnlyList<OllamaModel>? Models);

    private sealed record OllamaModel(string? Name);
}
