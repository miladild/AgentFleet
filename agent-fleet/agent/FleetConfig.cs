namespace AgentFleet;

/// <summary>
/// The three routing tiers a text node can serve. The triage classifier picks a tier
/// (by choosing a node in it); how many machines sit in each tier, and what they are
/// called, is entirely up to the config.
/// </summary>
internal static class FleetTiers
{
    public const string Heavy = "heavy";
    public const string Standard = "standard";
    public const string Light = "light";

    public static readonly IReadOnlyList<string> All = [Heavy, Standard, Light];
}

/// <param name="Tier">
/// heavy, standard or light. Ignored for vision nodes. Missing means standard.
/// </param>
/// <param name="Vision">
/// Handles image requests. Never routed to by the classifier and never used as the
/// fallback, since it cannot serve text-only work with tools.
/// </param>
/// <param name="Fallback">
/// The node that answers when triage fails, when a tool round continues, and when
/// another node is down. Triage itself runs against it. Missing means the first heavy
/// text node, or failing that the first text node.
/// </param>
internal sealed record FleetNodeConfig(
    string Name,
    string Url,
    string Model,
    string Purpose,
    string? Tier = null,
    bool Vision = false,
    bool Fallback = false);

internal sealed record FleetToolConfig(bool Enabled);

/// <summary>
/// One MCP server whose tools join the fleet's built-in ones. Field names match VS
/// Code's mcp.json so an entry can be pasted across. Values in Args, Env, Url and
/// Headers may reference machine environment variables as ${env:NAME}, so secrets
/// never have to be written into this file.
/// </summary>
/// <param name="Type">"stdio" (a local process) or "http" (a remote server).</param>
internal sealed record FleetMcpServerConfig(
    string Type,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool Enabled = true);

/// <summary>
/// Where the run_sandboxed_code tool runs its throwaway Docker container. Every field is
/// optional; the SANDBOX_* environment variables fill in what the file leaves out.
/// </summary>
/// <param name="Mode">
/// "off" (no sandbox tool), "local" (Docker on the hub itself) or "ssh" (Docker on another
/// machine, reached over SSH with a key). Missing means auto: ssh if a host is given,
/// otherwise local if Docker is installed, otherwise off.
/// </param>
/// <param name="Sudo">Prefix the remote docker command with sudo (ssh mode, when the account is not in the docker group).</param>
/// <param name="HostKey">
/// The ssh machine's host key fingerprint ("SHA256:..."), remembered when the sandbox is tested and saved in
/// the web UI. When set, a machine answering with a different key is refused. Missing means any key is accepted.
/// </param>
internal sealed record FleetSandboxConfig(
    string? Mode = null,
    string? Host = null,
    int? Port = null,
    string? User = null,
    string? KeyPath = null,
    bool? Sudo = null,
    int? TimeoutSeconds = null,
    string? HostKey = null);

/// <summary>
/// How long the durable record keeps chats. The record holds everything the assistant saw,
/// including file contents it read, so old chats are worth deleting on a schedule.
/// </summary>
/// <param name="DeleteAfterDays">
/// Delete a chat and its record once nothing has happened in it for this many days. Missing or 0
/// means keep everything. A chat an unfinished plan runs in is never deleted.
/// </param>
internal sealed record FleetHistoryConfig(int? DeleteAfterDays = null);

/// <summary>
/// The fleet's own runtime configuration - which machines/models exist, what each is
/// for, which tools are on, and the current mode/plan-mode toggles - as data, not
/// hardcoded C#. Backed by a JSON file (see FleetConfigStore) so it can be edited by
/// hand or from the web UI without a rebuild. A save through PUT /api/fleet-config
/// applies at once (the router and the tool registry are rebuilt, the rest is read per
/// request or per cleanup, and the sandbox is swapped); a hand edit to the file applies at startup.
/// </summary>
internal sealed record FleetConfig(
    string TriageModel,
    string Mode,
    bool PlanModeEnabled,
    IReadOnlyList<FleetNodeConfig> Nodes,
    IReadOnlyDictionary<string, FleetToolConfig> Tools,
    IReadOnlyDictionary<string, FleetMcpServerConfig>? McpServers = null,
    FleetSandboxConfig? Sandbox = null,
    FleetHistoryConfig? History = null)
{
    public const int MaxHistoryDays = 3650;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, FleetMcpServerConfig> McpServerMap =>
        McpServers ?? new Dictionary<string, FleetMcpServerConfig>(StringComparer.Ordinal);

    public bool IsToolEnabled(string name) =>
        !Tools.TryGetValue(name, out FleetToolConfig? tool) || tool.Enabled;
}
