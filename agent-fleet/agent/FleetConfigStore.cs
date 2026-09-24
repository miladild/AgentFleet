using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace AgentFleet;

/// <summary>
/// Loads and persists FleetConfig to a JSON file next to the running exe
/// (AppContext.BaseDirectory/fleet.config.json - dev bin folder, or wherever the
/// backend is published to - deliberately not inside anything a "dotnet publish"
/// redeploy would overwrite, same convention as sessions/ and logs/).
///
/// First run writes a minimal single-machine config (one local Ollama node). Add more
/// machines from the web UI's Config panel, by editing the file, or with the setup
/// scripts. After that first write the file is the sole source of truth.
/// </summary>
internal sealed class FleetConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Regex NodeNamePattern = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled);

    private readonly string _path;

    // Writers take this lock; readers never do. Almost every request reads the config (mode, plan
    // mode, tools), and a disk write that stalls - a slow disk, or antivirus scanning the file -
    // must not be able to freeze them.
    private readonly object _writeLock = new();
    private volatile FleetConfig _current;

    public FleetConfigStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _path = configuration["FLEET_CONFIG_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "fleet.config.json");
        _current = LoadOrSeed(configuration);
    }

    public FleetConfig Current => _current;

    public FleetConfig Save(FleetConfig config)
    {
        FleetConfig prepared = Prepare(config);
        lock (_writeLock)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(prepared, SerializerOptions));
            _current = prepared;
            return prepared;
        }
    }

    public FleetConfig UpdateMode(string mode)
    {
        lock (_writeLock)
        {
            return Save(_current with { Mode = mode });
        }
    }

    public FleetConfig UpdatePlanMode(bool enabled)
    {
        lock (_writeLock)
        {
            return Save(_current with { PlanModeEnabled = enabled });
        }
    }

    private FleetConfig LoadOrSeed(IConfiguration configuration)
    {
        if (File.Exists(_path))
        {
            FleetConfig? loaded;
            try
            {
                loaded = JsonSerializer.Deserialize<FleetConfig>(File.ReadAllText(_path), SerializerOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"{_path} exists but is not valid JSON.", exception);
            }

            if (loaded is null)
            {
                throw new InvalidOperationException($"{_path} exists but parsed to null.");
            }

            return Prepare(loaded);
        }

        // One local node and nothing machine-specific: the file is created so it can be
        // edited, and any further machines are added by whoever owns this deployment.
        FleetConfig seeded = new(
            TriageModel: configuration["TRIAGE_OLLAMA_MODEL"] ?? "llama3.2:latest",
            Mode: "conservative",
            PlanModeEnabled: false,
            Nodes:
            [
                new FleetNodeConfig(
                    "hub",
                    configuration["HUB_OLLAMA_URL"] ?? "http://127.0.0.1:11434/v1",
                    configuration["HUB_OLLAMA_MODEL"] ?? "qwen2.5-coder:7b",
                    "this machine: coding, tools and fallback",
                    Tier: FleetTiers.Heavy,
                    Fallback: true)
            ],
            Tools: new Dictionary<string, FleetToolConfig>(StringComparer.Ordinal)
            {
                ["run_sandboxed_code"] = new FleetToolConfig(true),
                ["read_file"] = new FleetToolConfig(true),
                ["write_file"] = new FleetToolConfig(true),
                ["edit_file"] = new FleetToolConfig(true),
                ["list_directory"] = new FleetToolConfig(true),
                ["find_files"] = new FleetToolConfig(true),
                ["search_files"] = new FleetToolConfig(true),
                ["run_git_command"] = new FleetToolConfig(true),
                ["run_command"] = new FleetToolConfig(true),
                ["web_search"] = new FleetToolConfig(true),
                ["web_fetch"] = new FleetToolConfig(true)
            });

        FleetConfig prepared = Prepare(seeded);
        File.WriteAllText(_path, JsonSerializer.Serialize(prepared, SerializerOptions));
        return prepared;
    }

    private static FleetConfig Prepare(FleetConfig config)
    {
        FleetConfig normalized = Normalize(config);
        Validate(normalized);
        return normalized;
    }

    // Fills in the roles a config file may leave out, so a hand-written or older file
    // still loads: no tier means standard, vision nodes carry no tier and never act as
    // the fallback, and with no explicit fallback the first heavy text node takes it
    // (else the first text node).
    private static FleetConfig Normalize(FleetConfig config)
    {
        List<FleetNodeConfig> nodes = config.Nodes
            .Select(node => node with { Url = NormalizeUrl(node.Url) })
            .Select(node => node.Vision
                ? node with { Tier = null, Fallback = false }
                : node with
                {
                    Tier = string.IsNullOrWhiteSpace(node.Tier) ? FleetTiers.Standard : node.Tier.Trim().ToLowerInvariant()
                })
            .ToList();

        if (!nodes.Any(node => node.Fallback))
        {
            int index = nodes.FindIndex(node => !node.Vision && node.Tier == FleetTiers.Heavy);
            if (index < 0)
            {
                index = nodes.FindIndex(node => !node.Vision);
            }

            if (index >= 0)
            {
                nodes[index] = nodes[index] with { Fallback = true };
            }
        }

        return config with
        {
            Nodes = nodes,
            McpServers = config.McpServers is null
                ? new Dictionary<string, FleetMcpServerConfig>(StringComparer.Ordinal)
                : config.McpServers.ToDictionary(
                    server => server.Key,
                    server => server.Value with { Type = server.Value.Type.Trim().ToLowerInvariant() },
                    StringComparer.Ordinal)
        };
    }

    // People write the address Ollama prints ("http://host:11434"), but the backend talks to its
    // OpenAI-compatible endpoint under /v1. Adding it here means a node added by hand, from the
    // Config panel or by a script cannot stop the backend from starting over a missing suffix.
    internal static string NormalizeUrl(string url)
    {
        string trimmed = (url ?? string.Empty).Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
               uri.Scheme is "http" or "https" &&
               uri.AbsolutePath is "" or "/"
            ? trimmed.TrimEnd('/') + "/v1"
            : trimmed;
    }

    private static void Validate(FleetConfig config)
    {
        if (config.Nodes.Count == 0 || config.Nodes.All(node => node.Vision))
        {
            throw new InvalidOperationException(
                "fleet.config.json needs at least one text node (a node that is not a vision node).");
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (FleetNodeConfig node in config.Nodes)
        {
            if (!NodeNamePattern.IsMatch(node.Name))
            {
                throw new InvalidOperationException(
                    $"Node name '{node.Name}' is invalid - use 1 to 32 lowercase letters, digits or hyphens, starting with a letter or digit.");
            }

            if (!seenNames.Add(node.Name))
            {
                throw new InvalidOperationException($"Node name '{node.Name}' is used more than once.");
            }

            if (!Uri.TryCreate(node.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException($"Node '{node.Name}' has an invalid URL: '{node.Url}'.");
            }

            if (!string.Equals(uri.AbsolutePath.TrimEnd('/'), "/v1", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException(
                    $"Node '{node.Name}' has the URL '{node.Url}', which must be the machine's address with /v1 at the end, for example http://192.168.1.20:11434/v1.");
            }

            if (string.IsNullOrWhiteSpace(node.Model))
            {
                throw new InvalidOperationException($"Node '{node.Name}' must have a model.");
            }

            if (!node.Vision && !FleetTiers.All.Contains(node.Tier!, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Node '{node.Name}' has tier '{node.Tier}' - it must be one of: {string.Join(", ", FleetTiers.All)}.");
            }
        }

        int fallbackCount = config.Nodes.Count(node => node.Fallback);
        if (fallbackCount != 1)
        {
            throw new InvalidOperationException(
                $"Exactly one node must be the fallback, found {fallbackCount}.");
        }

        foreach ((string serverName, FleetMcpServerConfig server) in config.McpServerMap)
        {
            if (!NodeNamePattern.IsMatch(serverName))
            {
                throw new InvalidOperationException(
                    $"MCP server name '{serverName}' is invalid - use 1 to 32 lowercase letters, digits or hyphens, starting with a letter or digit.");
            }

            if (server.Type == "stdio")
            {
                if (string.IsNullOrWhiteSpace(server.Command))
                {
                    throw new InvalidOperationException($"MCP server '{serverName}' is type stdio, so it needs a command.");
                }
            }
            else if (server.Type == "http")
            {
                // A ${env:...} placeholder is expanded later, so only check plain URLs here.
                if (string.IsNullOrWhiteSpace(server.Url) ||
                    (!server.Url.Contains("${env:", StringComparison.Ordinal) &&
                     (!Uri.TryCreate(server.Url, UriKind.Absolute, out Uri? mcpUri) || mcpUri.Scheme is not ("http" or "https"))))
                {
                    throw new InvalidOperationException($"MCP server '{serverName}' is type http, so it needs an absolute http(s) url.");
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"MCP server '{serverName}' has type '{server.Type}' - it must be stdio or http.");
            }
        }

        if (config.History?.DeleteAfterDays is < 0 or > FleetConfig.MaxHistoryDays)
        {
            throw new InvalidOperationException(
                $"history.deleteAfterDays must be between 0 (keep everything) and {FleetConfig.MaxHistoryDays}.");
        }

        if (string.IsNullOrWhiteSpace(config.TriageModel))
        {
            throw new InvalidOperationException("triageModel must not be empty.");
        }

        if (!string.Equals(config.Mode, "conservative", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.Mode, "aggressive", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("mode must be 'conservative' or 'aggressive'.");
        }
    }
}
