using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>One MCP server found in another app's config, as the Config panel lists it: secrets masked.</summary>
internal sealed record McpImportCandidate(
    string Name,
    string Type,
    string Summary,
    bool HasInlineSecret,
    bool UsesInputs,
    bool AlreadyAdded);

internal sealed record McpImportSource(string App, string Path, IReadOnlyList<McpImportCandidate> Servers, string? Error);

/// <summary>
/// Finds the MCP servers already set up in VS Code, Claude Desktop, Claude Code, Cursor and Windsurf on this
/// computer, so they can be added to the fleet with a click instead of copied by hand. Only these known files
/// are read. What goes to the browser has secret-looking values masked; the real values are copied on the
/// backend when an import is confirmed.
/// </summary>
internal static partial class McpImport
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The config files of other apps that can hold MCP servers, for the account the backend runs as.</summary>
    public static IReadOnlyList<(string App, string Path)> KnownFiles()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support") : Path.Combine(home, ".config");
        return
        [
            ("VS Code", Path.Combine(appData, "Code", "User", "mcp.json")),
            ("VS Code Insiders", Path.Combine(appData, "Code - Insiders", "User", "mcp.json")),
            ("VS Code settings", Path.Combine(appData, "Code", "User", "settings.json")),
            ("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json")),
            ("Claude Code", Path.Combine(home, ".claude.json")),
            ("Cursor", Path.Combine(home, ".cursor", "mcp.json")),
            ("Windsurf", Path.Combine(home, ".codeium", "windsurf", "mcp_config.json")),
        ];
    }

    public static IReadOnlyList<McpImportSource> Scan(FleetConfig current)
    {
        var sources = new List<McpImportSource>();
        foreach ((string app, string path) in KnownFiles())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                IReadOnlyDictionary<string, FleetMcpServerConfig> servers = Read(path);
                if (servers.Count == 0)
                {
                    continue;
                }

                sources.Add(new McpImportSource(app, path, servers.Select(pair => new McpImportCandidate(
                    pair.Key,
                    pair.Value.Type,
                    Summarize(pair.Value),
                    HasInlineSecret(pair.Value),
                    UsesInputs(pair.Value),
                    current.McpServerMap.Values.Any(existing => SameServer(existing, pair.Value)))).ToList(), null));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                sources.Add(new McpImportSource(app, path, [], $"Could not read it: {exception.Message}"));
            }
        }

        return sources;
    }

    /// <summary>The chosen servers from one of the known files, with their real values, renamed where a name is taken.</summary>
    public static IReadOnlyDictionary<string, FleetMcpServerConfig> Take(string path, IReadOnlyCollection<string> names, FleetConfig current)
    {
        if (!KnownFiles().Any(known => string.Equals(known.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("That is not one of the config files the fleet imports from.");
        }

        IReadOnlyDictionary<string, FleetMcpServerConfig> found = Read(path);
        var taken = new Dictionary<string, FleetMcpServerConfig>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!found.TryGetValue(name, out FleetMcpServerConfig? server))
            {
                throw new ArgumentException($"{name} is no longer in that file.");
            }

            string unique = name;
            for (int n = 2; current.McpServerMap.ContainsKey(unique) || taken.ContainsKey(unique); n++)
            {
                unique = $"{name[..Math.Min(name.Length, 29)]}-{n}";
            }

            taken[unique] = server;
        }

        return taken;
    }

    /// <summary>Reads the servers of one file: "servers" (VS Code), "mcpServers" (Claude, Cursor, Windsurf), or "mcp.servers" in VS Code settings.</summary>
    internal static IReadOnlyDictionary<string, FleetMcpServerConfig> Read(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), Lenient);
        return Parse(document.RootElement);
    }

    internal static IReadOnlyDictionary<string, FleetMcpServerConfig> Parse(JsonElement root)
    {
        JsonElement map = default;
        bool found = root.ValueKind == JsonValueKind.Object &&
                     (root.TryGetProperty("servers", out map) || root.TryGetProperty("mcpServers", out map) ||
                      (root.TryGetProperty("mcp", out JsonElement mcp) && mcp.ValueKind == JsonValueKind.Object && mcp.TryGetProperty("servers", out map)));
        var servers = new Dictionary<string, FleetMcpServerConfig>(StringComparer.Ordinal);
        if (!found || map.ValueKind != JsonValueKind.Object)
        {
            return servers;
        }

        foreach (JsonProperty entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string name = NameCleaner().Replace(entry.Name.ToLowerInvariant(), "-").Trim('-');
            if (name.Length == 0)
            {
                continue;
            }

            name = name[..Math.Min(name.Length, 32)].TrimEnd('-');
            JsonElement value = entry.Value;
            string? url = Text(value, "url") ?? Text(value, "serverUrl");
            string? declared = Text(value, "type")?.ToLowerInvariant();
            string type = declared is "http" or "sse" or "streamablehttp" || (declared is null && url is not null) ? "http" : "stdio";
            string? command = Text(value, "command");
            if ((type == "http" && url is null) || (type == "stdio" && string.IsNullOrWhiteSpace(command)))
            {
                continue;
            }

            servers[name] = new FleetMcpServerConfig(
                type,
                type == "stdio" ? command : null,
                type == "stdio" && value.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Array
                    ? args.EnumerateArray().Where(arg => arg.ValueKind == JsonValueKind.String).Select(arg => arg.GetString()!).ToList()
                    : null,
                type == "stdio" ? StringMap(value, "env") : null,
                type == "http" ? url : null,
                type == "http" ? StringMap(value, "headers") : null,
                Enabled: !(value.TryGetProperty("disabled", out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True));
        }

        return servers;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyDictionary<string, string>? StringMap(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement map) || map.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = map.EnumerateObject()
            .Where(pair => pair.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(pair => pair.Name, pair => pair.Value.GetString()!, StringComparer.Ordinal);
        return result.Count == 0 ? null : result;
    }

    private static string Summarize(FleetMcpServerConfig server)
    {
        if (server.Type == "http")
        {
            // Some servers take their key in the query string, so the list never shows one.
            return Uri.TryCreate(server.Url, UriKind.Absolute, out Uri? uri) && uri.Query.Length > 1
                ? $"{uri.GetLeftPart(UriPartial.Path)}?..."
                : server.Url ?? string.Empty;
        }

        IEnumerable<string> args = (server.Args ?? []).Select(arg => LooksSecret(arg) ? "•••" : arg);
        return string.Join(' ', new[] { server.Command ?? string.Empty }.Concat(args));
    }

    /// <summary>A literal secret written into the entry rather than a ${env:NAME} or ${input:...} reference.</summary>
    internal static bool HasInlineSecret(FleetMcpServerConfig server) =>
        (server.Env ?? new Dictionary<string, string>()).Concat(server.Headers ?? new Dictionary<string, string>())
            .Any(pair => SecretKey().IsMatch(pair.Key) && !pair.Value.Contains("${", StringComparison.Ordinal) &&
                         pair.Value.Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().Length >= 16) ||
        (server.Args ?? []).Any(LooksSecret) ||
        (Uri.TryCreate(server.Url, UriKind.Absolute, out Uri? uri) && SecretKey().IsMatch(uri.Query));

    // VS Code's ${input:...} asks the user when the server starts; the fleet cannot ask, so it needs an env variable.
    private static bool UsesInputs(FleetMcpServerConfig server) =>
        new[] { server.Url, server.Command }.Concat(server.Args ?? []).Concat(server.Env?.Values ?? []).Concat(server.Headers?.Values ?? [])
            .Any(value => value?.Contains("${input:", StringComparison.Ordinal) == true);

    private static bool LooksSecret(string value) => TokenLike().IsMatch(value);

    private static bool SameServer(FleetMcpServerConfig a, FleetMcpServerConfig b) =>
        a.Type == b.Type && (a.Type == "http"
            ? string.Equals(a.Url, b.Url, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Summarize(a), Summarize(b), StringComparison.Ordinal));

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NameCleaner();

    [GeneratedRegex("token|secret|key|password|authorization|pat", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    // Long opaque strings with the prefixes common API tokens use (GitHub, OpenAI, Slack, and so on).
    [GeneratedRegex(@"^(gh[pousr]_|github_pat_|sk-|xox[abp]-|glpat-)[A-Za-z0-9_\-]{16,}$")]
    private static partial Regex TokenLike();
}
