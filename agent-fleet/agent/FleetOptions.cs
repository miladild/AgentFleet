using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AgentFleet;

internal sealed record FleetNodeDefinition(
    string Name,
    Uri OpenAiEndpoint,
    string Model,
    string Purpose,
    string? Tier,
    bool Vision,
    bool Fallback,
    int? ContextLength = null,
    string? Api = null)
{
    /// <summary>The machine's Ollama root, http://host:11434/, for the native API.</summary>
    public Uri OllamaRoot => new UriBuilder(OpenAiEndpoint.Scheme, OpenAiEndpoint.Host, OpenAiEndpoint.IsDefaultPort ? -1 : OpenAiEndpoint.Port, "/").Uri;

    public Uri TagsEndpoint => new UriBuilder(
        OpenAiEndpoint.Scheme,
        OpenAiEndpoint.Host,
        OpenAiEndpoint.IsDefaultPort ? -1 : OpenAiEndpoint.Port,
        "api/tags").Uri;
}

/// <summary>
/// Node identity/model data now lives in FleetConfigStore (fleet.config.json) - this
/// just validates/normalizes it into FleetNodeDefinition and still owns the network
/// timeout settings, which remain plain env-var infra config rather than something
/// that needs editing from the UI.
/// </summary>
internal sealed class FleetOptions
{
    private volatile IReadOnlyList<FleetNodeDefinition> _nodes;

    private FleetOptions(
        IReadOnlyList<FleetNodeDefinition> nodes,
        TimeSpan networkTimeout,
        TimeSpan healthProbeTimeout,
        TimeSpan healthCacheDuration)
    {
        _nodes = nodes;
        NetworkTimeout = networkTimeout;
        HealthProbeTimeout = healthProbeTimeout;
        HealthCacheDuration = healthCacheDuration;
    }

    /// <summary>The machines as currently configured. Replaced as a whole when the config is saved.</summary>
    public IReadOnlyList<FleetNodeDefinition> Nodes => _nodes;

    public TimeSpan NetworkTimeout { get; }

    public TimeSpan HealthProbeTimeout { get; }

    public TimeSpan HealthCacheDuration { get; }

    public FleetNodeDefinition GetNode(string name) =>
        Nodes.Single(node => string.Equals(node.Name, name, StringComparison.Ordinal));

    public bool TryGetNode(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FleetNodeDefinition? node)
    {
        node = Nodes.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return node is not null;
    }

    /// <summary>
    /// Takes the machines from a saved config. Everything built from them (the health probes, the plan
    /// runner, a rebuilt router) sees the new list from the next time it looks.
    /// </summary>
    public IReadOnlyList<FleetNodeDefinition> ReplaceNodes(FleetConfig config)
    {
        List<FleetNodeDefinition> nodes = config.Nodes.Select(ToDefinition).ToList();
        _nodes = nodes;
        return nodes;
    }

    public static FleetOptions Load(IConfiguration configuration, FleetConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configStore);

        IReadOnlyList<FleetNodeDefinition> nodes = configStore.Current.Nodes
            .Select(ToDefinition)
            .ToList();

        return new FleetOptions(
            nodes,
            // A planning turn on the heavy model can legitimately generate a long structured plan.
            ReadDuration(configuration, "FLEET_NETWORK_TIMEOUT_SECONDS", 300, minimum: 10, maximum: 900),
            ReadDuration(configuration, "FLEET_HEALTH_PROBE_TIMEOUT_SECONDS", 3, minimum: 1, maximum: 30),
            ReadDuration(configuration, "FLEET_HEALTH_CACHE_SECONDS", 10, minimum: 1, maximum: 300));
    }

    private static FleetNodeDefinition ToDefinition(FleetNodeConfig config)
    {
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(endpoint.Host) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.Equals(endpoint.AbsolutePath.TrimEnd('/'), "/v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Node '{config.Name}' has an invalid Ollama endpoint '{config.Url}' - must be an absolute HTTP(S) URL ending in /v1.");
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            throw new InvalidOperationException($"Node '{config.Name}' must have a model.");
        }

        var normalizedEndpoint = new UriBuilder(endpoint)
        {
            Path = "/v1",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;

        return new FleetNodeDefinition(
            config.Name,
            normalizedEndpoint,
            config.Model.Trim(),
            config.Purpose,
            config.Tier,
            config.Vision,
            config.Fallback,
            config.ContextLength,
            config.Api);
    }

    private static TimeSpan ReadDuration(
        IConfiguration configuration,
        string key,
        int defaultSeconds,
        int minimum,
        int maximum)
    {
        string? rawValue = configuration[key];
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        if (!int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ||
            seconds < minimum ||
            seconds > maximum)
        {
            throw new InvalidOperationException(
                $"{key} must be an integer between {minimum} and {maximum} seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
