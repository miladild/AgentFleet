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
/// <param name="ContextLength">
/// How many tokens of conversation the model may see (Ollama's num_ctx). Missing means the fleet's default (32768, or
/// FLEET_CONTEXT_LENGTH), capped at what the model supports. Ollama's own default, 4096, is smaller than the fleet's
/// instructions and tool list together, so without this Ollama silently cuts off the start of every request.
/// </param>
/// <param name="Api">
/// "ollama" (default): Ollama's native API, which is what lets the fleet set the context size. "openai": any
/// OpenAI-compatible server (LM Studio, vLLM, llama.cpp), which gets requests the way it expects and sets its own size.
/// </param>
/// <param name="Caveman">Optional concise-response preset: off, lite, full, or ultra.</param>
/// <param name="Ponytail">Optional minimal-coding preset: off, lite, full, or ultra.</param>
/// <param name="Thinking">
/// "off" (default): a model that thinks before it answers is told not to. Measured on a 9B worker: one turn in three
/// spent its whole 4096-token answer on thinking and ended with no tool call and no text, minutes lost on a slow
/// machine. "model": the model's own behaviour, for a machine whose model is better thinking.
/// </param>
internal sealed record FleetNodeConfig(
    string Name,
    string Url,
    string Model,
    string Purpose,
    string? Tier = null,
    bool Vision = false,
    bool Fallback = false,
    int? ContextLength = null,
    string? Api = null,
    string? Caveman = null,
    string? Ponytail = null,
    FleetWorkerWorkspaceConfig? Workspace = null,
    string? Thinking = null);

/// <summary>SSH workspace for approved plan work. Configure only a dedicated, non-admin worker account.</summary>
internal sealed record FleetWorkerWorkspaceConfig(
    string Host,
    string User,
    string KeyPath,
    string HostKey,
    string Root,
    string Platform,
    int Port = 22);

internal sealed record FleetToolConfig(
    bool Enabled,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? AllowedInternalHosts = null);

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
    FleetHistoryConfig? History = null,
    IReadOnlyDictionary<string, FleetCustomToolConfig>? CustomTools = null,
    string? NotifyUrl = null,
    FleetLabConfig? Lab = null,
    FleetChatApprovalConfig? ChatApprovals = null,
    FleetWeeklyProbeConfig? WeeklyProbe = null)
{
    public const int MaxHistoryDays = 3650;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, FleetMcpServerConfig> McpServerMap =>
        McpServers ?? new Dictionary<string, FleetMcpServerConfig>(StringComparer.Ordinal);

    /// <summary>What applies when the file has no chatApprovals block: approval off, and the default wait.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public FleetChatApprovalConfig ChatApprovalSettings => ChatApprovals ?? new FleetChatApprovalConfig();

    /// <summary>What applies when the file has no weeklyProbe block: the weekly measurement is off.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public FleetWeeklyProbeConfig WeeklyProbeSettings => WeeklyProbe ?? new FleetWeeklyProbeConfig();

    /// <summary>Tools made from commands in the Config panel (see CustomTools.cs).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, FleetCustomToolConfig> CustomToolMap =>
        CustomTools ?? new Dictionary<string, FleetCustomToolConfig>(StringComparer.Ordinal);

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> WebFetchAllowedInternalHosts =>
        Tools.TryGetValue("webFetch", out FleetToolConfig? webFetch) && webFetch is not null
            ? webFetch.AllowedInternalHosts ?? []
            : [];

    public bool IsToolEnabled(string name) =>
        !Tools.TryGetValue(name, out FleetToolConfig? tool) || tool.Enabled;
}

internal sealed record FleetLabConfig(double MinSpeedFraction = 0.70, double FullFitShare = 0.90, double ConstrainedSpeedFraction = 0.80);

/// <summary>
/// Whether a chat tool that can change something waits for the user before it runs (see ChatApprovals). Off by
/// default: a chat that runs tools without a question is how the fleet has always behaved. Plan runs are never asked.
/// </summary>
/// <param name="Enabled">Ask before a chat tool call that is not read-only runs.</param>
/// <param name="TimeoutSeconds">
/// How long a call waits for an answer before it is refused. Kept between 30 and 280 seconds: the web proxy drops a
/// stream that stays silent for 300 seconds, and the chat is silent while a call waits.
/// </param>
internal sealed record FleetChatApprovalConfig(bool Enabled = false, int TimeoutSeconds = 240)
{
    public const int MinTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 280;

    public static int ClampTimeout(int seconds) => Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds);
}

/// <summary>
/// Measures the worker machines' context fit once a week, inside a window the owner sets (see WeeklyFitProbeService).
/// Off by default. The hub is never measured this way: only an explicit measurement, started by the owner, touches it.
/// </summary>
/// <param name="Enabled">Measure the workers once a week, and only inside the window.</param>
/// <param name="DayOfWeek">The day the window opens, as an English name such as "sunday", in any letter case.</param>
/// <param name="StartHour">The hour the window opens, 0 to 23, in the machine's local time.</param>
/// <param name="Hours">How long the window stays open, 1 to 12 hours. It may run past midnight.</param>
internal sealed record FleetWeeklyProbeConfig(bool Enabled = false, string DayOfWeek = "sunday", int StartHour = 2, int Hours = 4)
{
    public const string DefaultDayOfWeek = "sunday";
    public const int DefaultStartHour = 2;
    public const int DefaultHours = 4;

    /// <summary>The same block with every field in its canonical form. A field that is not valid falls back to its default.</summary>
    public FleetWeeklyProbeConfig Normalized() => this with
    {
        DayOfWeek = ParseDay(DayOfWeek) is { } day ? day.ToString().ToLowerInvariant() : DefaultDayOfWeek,
        StartHour = StartHour is >= 0 and <= 23 ? StartHour : DefaultStartHour,
        Hours = Hours is >= 1 and <= 12 ? Hours : DefaultHours
    };

    /// <summary>The day an English day name stands for, in any letter case, or null for anything else.</summary>
    public static System.DayOfWeek? ParseDay(string? value) => Enum.GetValues<System.DayOfWeek>()
        .Cast<System.DayOfWeek?>()
        .FirstOrDefault(day => string.Equals(day.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase));
}
