using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// A set of names that can be replaced as a whole while other threads read it. The router and the
/// plan gate read "which tools only read" on every request; an MCP reload swaps the set in one step.
/// </summary>
internal sealed class SwappableNameSet : IReadOnlySet<string>
{
    private volatile ImmutableHashSet<string> _names;

    public SwappableNameSet(IEnumerable<string> names) =>
        _names = ImmutableHashSet.CreateRange(StringComparer.Ordinal, names);

    public void Replace(IEnumerable<string> names) =>
        _names = ImmutableHashSet.CreateRange(StringComparer.Ordinal, names);

    public int Count => _names.Count;

    public bool Contains(string item) => _names.Contains(item);

    public bool IsProperSubsetOf(IEnumerable<string> other) => _names.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<string> other) => _names.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<string> other) => _names.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<string> other) => _names.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<string> other) => _names.Overlaps(other);

    public bool SetEquals(IEnumerable<string> other) => _names.SetEquals(other);

    public IEnumerator<string> GetEnumerator() => _names.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The tools the agent may use right now. Built-in tools are fixed at startup, but whether each is on,
/// and which MCP servers are connected, follow fleet.config.json and change when it is saved: adding a
/// server in the Config panel takes effect on Save, without restarting the backend.
/// </summary>
internal sealed class FleetToolRegistry : IAsyncDisposable
{
    private static readonly TimeSpan RetireDelay = TimeSpan.FromMinutes(2);

    private readonly FleetConfigStore _config;
    private readonly IReadOnlySet<string> _builtInReadOnly;
    private readonly SwappableNameSet _readOnly;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly Func<string, bool> _available;
    private McpToolProvider _mcp = McpToolProvider.Empty;
    private string _connectedFingerprint = string.Empty;
    private volatile IReadOnlyList<CustomCommandTool> _custom = [];
    private volatile IReadOnlyList<CustomToolStatus> _customStatuses = [];

    /// <param name="available">
    /// Whether a built-in tool can work right now (the sandbox tool needs a sandbox). A tool that cannot is
    /// left out of the request whatever the config says. Missing means every built-in is available.
    /// </param>
    public FleetToolRegistry(
        FleetConfigStore config,
        IEnumerable<string> builtInNames,
        SwappableNameSet readOnly,
        ILoggerFactory loggerFactory,
        Func<string, bool>? available = null)
    {
        _config = config;
        _available = available ?? (_ => true);
        BuiltInNames = builtInNames.ToHashSet(StringComparer.Ordinal);
        // What is read-only among the built-ins is fixed; MCP tools are added to this on each reload.
        _builtInReadOnly = readOnly.ToHashSet(StringComparer.Ordinal);
        _readOnly = readOnly;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("AgentFleet.Tools");
    }

    public IReadOnlySet<string> BuiltInNames { get; }

    /// <summary>Tools that only read, and so stay available while a plan awaits approval.</summary>
    public IReadOnlySet<string> ReadOnlyTools => _readOnly;

    public McpToolProvider Mcp => _mcp;

    /// <summary>The command tools made in the Config panel that are switched on and usable.</summary>
    public IReadOnlyList<CustomCommandTool> Custom => _custom;

    /// <summary>Every command tool in the config, and why one is not offered.</summary>
    public IReadOnlyList<CustomToolStatus> CustomStatuses => _customStatuses;

    /// <summary>A tool the registry offers besides the built-ins, by name: a command tool or an MCP tool.</summary>
    public AIFunction? FindAdded(string name) =>
        (AIFunction?)_custom.FirstOrDefault(tool => tool.Name == name) ?? _mcp.Tools.FirstOrDefault(entry => entry.Tool.Name == name)?.Tool;

    /// <summary>
    /// Connects to the servers in the current config if they differ from what is connected. New
    /// connections are made before the old ones are dropped, so a request in flight keeps working, and
    /// the old ones are closed a little later for the same reason.
    /// </summary>
    public async Task<IReadOnlyList<McpServerStatus>> ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            // Command tools cost nothing to rebuild, so they always follow the config.
            ReloadCustom();

            IReadOnlyDictionary<string, FleetMcpServerConfig> servers = _config.Current.McpServerMap;
            string fingerprint = JsonSerializer.Serialize(servers.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            if (fingerprint == _connectedFingerprint)
            {
                return _mcp.Statuses;
            }

            McpToolProvider next = await McpToolProvider.ConnectAsync(servers, _loggerFactory, cancellationToken);
            McpToolProvider previous = _mcp;
            _mcp = next;
            _connectedFingerprint = fingerprint;
            // Again, now that the servers' tool names are known (a command tool must not take one of them).
            ReloadCustom();
            _logger.LogInformation(
                "MCP tools reloaded: {Servers} server(s), {Tools} tool(s).",
                next.Statuses.Count(status => status.Connected),
                next.Tools.Count);

            if (!ReferenceEquals(previous, McpToolProvider.Empty))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(RetireDelay);
                    await previous.DisposeAsync();
                });
            }

            return next.Statuses;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private void ReloadCustom()
    {
        var tools = new List<CustomCommandTool>();
        var statuses = new List<CustomToolStatus>();
        foreach ((string name, FleetCustomToolConfig config) in _config.Current.CustomToolMap.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string? problem = BuiltInNames.Contains(name)
                ? $"{name} is the name of a built-in tool. Rename it."
                : _mcp.Tools.Any(entry => entry.Tool.Name == name) ? $"An MCP server already offers a tool called {name}. Rename it." : null;
            if (problem is null && config.Enabled)
            {
                tools.Add(new CustomCommandTool(name, config));
            }

            statuses.Add(new CustomToolStatus(name, config.Enabled, problem is null && config.Enabled, problem));
        }

        _custom = tools;
        _customStatuses = statuses;
        UpdateReadOnly();
    }

    private void UpdateReadOnly() =>
        _readOnly.Replace(_builtInReadOnly
            .Concat(_mcp.Tools.Where(entry => entry.Tool.ProtocolTool.Annotations?.ReadOnlyHint == true).Select(entry => entry.Tool.Name))
            .Concat(_custom.Where(tool => tool.Config.ReadOnly).Select(tool => tool.Name)));

    /// <summary>
    /// The request's tool list as it should be now: built-in tools that are switched off removed, the
    /// connected MCP servers' enabled tools and the command tools added. Tools the client declared (VS Code's) are kept.
    /// </summary>
    public IList<AITool> Apply(IList<AITool>? requested)
    {
        FleetConfig config = _config.Current;
        var tools = new List<AITool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (AITool tool in requested ?? [])
        {
            if (BuiltInNames.Contains(tool.Name) && (!config.IsToolEnabled(tool.Name) || !_available(tool.Name)))
            {
                continue;
            }

            if (names.Add(tool.Name))
            {
                tools.Add(tool);
            }
        }

        foreach (McpToolEntry entry in _mcp.Tools)
        {
            if (config.IsToolEnabled(entry.Tool.Name) && names.Add(entry.Tool.Name))
            {
                tools.Add(entry.Tool);
            }
        }

        foreach (CustomCommandTool tool in _custom)
        {
            if (names.Add(tool.Name))
            {
                tools.Add(tool);
            }
        }

        return tools;
    }

    public ValueTask DisposeAsync() => _mcp.DisposeAsync();
}

internal sealed record CustomToolStatus(string Name, bool Enabled, bool Offered, string? Problem);

/// <summary>
/// Sits in front of the function-invocation layer and gives every request the current tool list, so a
/// change in the Config panel reaches the very next message.
/// </summary>
/// <param name="offer">
/// Optional per-request veto: given the conversation, the options and a tool name, false leaves that
/// tool out of this request. Used for tools that only make sense in some situations.
/// </param>
internal sealed class DynamicToolsChatClient(
    IChatClient inner,
    FleetToolRegistry registry,
    Func<IReadOnlyList<ChatMessage>, ChatOptions?, string, bool>? offer = null) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithCurrentTools(messages, options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithCurrentTools(messages, options), cancellationToken);

    private ChatOptions WithCurrentTools(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ChatOptions result = options?.Clone() ?? new ChatOptions();
        IList<AITool> tools = registry.Apply(options?.Tools);
        if (offer is not null)
        {
            IReadOnlyList<ChatMessage> history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
            tools = tools.Where(tool => offer(history, options, tool.Name)).ToList();
        }

        result.Tools = tools;
        return result;
    }
}
