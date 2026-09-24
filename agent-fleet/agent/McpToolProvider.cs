using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentFleet;

internal sealed record McpServerStatus(string Name, string Type, bool Enabled, bool Connected, int ToolCount, string? Error);

internal sealed record McpToolEntry(string Server, McpClientTool Tool);

/// <summary>What a trial connection found: the tools a server offers, or why it would not start.</summary>
internal sealed record McpTestResult(bool Ok, IReadOnlyList<McpToolInfo> Tools, string? Error);

internal sealed record McpToolInfo(string Name, string Description, bool ReadOnly);

/// <summary>
/// Connects to the MCP servers listed in fleet.config.json and exposes their tools as
/// ordinary agent tools, so anyone can add capabilities (docs search, GitHub, a
/// database, a browser) by adding a config entry instead of writing C#. The web UI and
/// @fleet in VS Code then share them.
///
/// Connections are made once at startup, concurrently and with a timeout, like the rest
/// of the agent's tool list. A server that fails to start is logged and skipped; it
/// never stops the backend from coming up. Each tool is renamed "{server}_{tool}"
/// because MCP servers commonly reuse names the built-ins already have (the official
/// filesystem server has read_file, write_file and list_directory).
/// </summary>
internal sealed partial class McpToolProvider : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly List<McpClient> _clients = [];

    private McpToolProvider(IReadOnlyList<McpToolEntry> tools, IReadOnlyList<McpServerStatus> statuses)
    {
        Tools = tools;
        Statuses = statuses;
    }

    public IReadOnlyList<McpToolEntry> Tools { get; }

    public IReadOnlyList<McpServerStatus> Statuses { get; }

    public static McpToolProvider Empty { get; } = new([], []);

    /// <summary>
    /// Starts a server once, lists its tools and shuts it down again, so a new entry can be checked from the
    /// Config panel before it is saved.
    /// </summary>
    public static async Task<McpTestResult> TestAsync(
        string name,
        FleetMcpServerConfig server,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggerFactory.CreateLogger("AgentFleet.Mcp");
        ConnectResult result = await ConnectOneAsync(name, server with { Enabled = true }, loggerFactory, logger, cancellationToken);
        if (result.Client is not null)
        {
            await result.Client.DisposeAsync();
        }

        return new McpTestResult(
            result.Status.Connected,
            result.Tools.Select(entry => new McpToolInfo(
                entry.Tool.Name,
                entry.Tool.Description ?? string.Empty,
                entry.Tool.ProtocolTool.Annotations?.ReadOnlyHint == true)).ToList(),
            result.Status.Error);
    }

    public static async Task<McpToolProvider> ConnectAsync(
        IReadOnlyDictionary<string, FleetMcpServerConfig> servers,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggerFactory.CreateLogger("AgentFleet.Mcp");
        if (servers.Count == 0)
        {
            return Empty;
        }

        ConnectResult[] results = await Task.WhenAll(
            servers.Select(server => ConnectOneAsync(server.Key, server.Value, loggerFactory, logger, cancellationToken)));

        var provider = new McpToolProvider(
            results.SelectMany(result => result.Tools).ToList(),
            results.Select(result => result.Status).ToList());
        provider._clients.AddRange(results.Where(result => result.Client is not null).Select(result => result.Client!));
        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (McpClient client in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception)
            {
                // Shutting down: a server that is already gone is not worth reporting.
            }
        }
    }

    private sealed record ConnectResult(McpServerStatus Status, McpClient? Client, IReadOnlyList<McpToolEntry> Tools);

    private static async Task<ConnectResult> ConnectOneAsync(
        string name,
        FleetMcpServerConfig server,
        ILoggerFactory loggerFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!server.Enabled)
        {
            return new ConnectResult(new McpServerStatus(name, server.Type, false, false, 0, null), null, []);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        McpClient? client = null;
        // What the server printed while starting: the reason it failed is almost always in there
        // ("npx is not recognized", "missing API key", a Python traceback).
        var stderr = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            IClientTransport transport = CreateTransport(name, server, loggerFactory, logger, stderr);
            client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = "agent-fleet", Version = "1.0" } },
                loggerFactory,
                timeout.Token);

            IList<McpClientTool> discovered = await client.ListToolsAsync(cancellationToken: timeout.Token);
            List<McpToolEntry> renamed = discovered
                .Select(tool => new McpToolEntry(name, tool.WithName(ToolName(name, tool.Name))))
                .ToList();

            logger.LogInformation("MCP server {Server} connected with {ToolCount} tool(s).", name, renamed.Count);
            return new ConnectResult(new McpServerStatus(name, server.Type, true, true, renamed.Count, null), client, renamed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            string reason = exception is OperationCanceledException
                ? $"did not start within {ConnectTimeout.TotalSeconds:0} seconds"
                : exception.Message;
            string printed = string.Join("\n", stderr.TakeLast(12));
            if (printed.Length > 0)
            {
                reason += "\nThe server printed:\n" + printed;
            }
            logger.LogWarning("MCP server {Server} could not be started and was skipped: {Reason}", name, reason);
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            return new ConnectResult(new McpServerStatus(name, server.Type, true, false, 0, reason), null, []);
        }
    }

    private static IClientTransport CreateTransport(
        string name,
        FleetMcpServerConfig server,
        ILoggerFactory loggerFactory,
        ILogger logger,
        System.Collections.Concurrent.ConcurrentQueue<string> stderr)
    {
        if (server.Type == "http")
        {
            return new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = name,
                    Endpoint = new Uri(ExpandEnvironment(server.Url!)),
                    AdditionalHeaders = server.Headers?.ToDictionary(header => header.Key, header => ExpandEnvironment(header.Value)),
                    TransportMode = HttpTransportMode.AutoDetect
                },
                loggerFactory);
        }

        string command = ExpandEnvironment(server.Command!);
        List<string> args = (server.Args ?? []).Select(ExpandEnvironment).ToList();

        // npx, npm, yarn and friends are .cmd shims on Windows, which CreateProcess cannot
        // start directly (VS Code runs them through a shell, which is why a pasted
        // mcp.json entry works there). Route those through cmd.exe.
        if (OperatingSystem.IsWindows() && IsWindowsShim(command))
        {
            args.Insert(0, command);
            args.Insert(0, "/c");
            command = "cmd.exe";
        }

        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = name,
                Command = command,
                Arguments = args,
                EnvironmentVariables = server.Env?.ToDictionary(pair => pair.Key, pair => (string?)ExpandEnvironment(pair.Value)),
                StandardErrorLines = line =>
                {
                    logger.LogDebug("[mcp:{Server}] {Line}", name, line);
                    stderr.Enqueue(line);
                    while (stderr.Count > 40 && stderr.TryDequeue(out _))
                    {
                    }
                }
            },
            loggerFactory);
    }

    // Function names sent to a model are limited to letters, digits, underscore and
    // hyphen, 64 characters at most.
    internal static string ToolName(string server, string tool)
    {
        string combined = InvalidNameCharacters().Replace($"{server}_{tool}", "_");
        return combined.Length <= 64 ? combined : combined[..64];
    }

    // Replaces ${env:NAME} with that environment variable's value, so a config entry can
    // point at a secret without containing it. An unset variable becomes empty.
    internal static string ExpandEnvironment(string value) =>
        EnvironmentReference().Replace(
            value,
            match => Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? string.Empty);

    private static bool IsWindowsShim(string command)
    {
        string name = Path.GetFileNameWithoutExtension(command);
        return name.Equals("npx", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("yarn", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex InvalidNameCharacters();

    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvironmentReference();
}
