using System.ClientModel;
using System.ComponentModel;
using System.Net.Http.Json;
using AgentFleet;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// No-ops when run normally (dotnet run, F5, etc.) - only changes behavior when
// Windows Service Control Manager actually launches this as a service, so dev
// workflow is unaffected. Lets this run as a real background Windows Service
// without IIS: see "Run it all the time" in docs/getting-started.md (scripts/Install-Autostart.ps1).
builder.Host.UseWindowsService();

// Deployed as a Windows Service, stdout goes nowhere and nothing was reaching
// Windows Event Log either - there was no way to see what the backend was
// doing short of attaching a debugger. A plain rolling text file next to the
// exe (dev or C:\AgentFleet\backend, whichever this happens to be running
// from) fixes that: `tail`/open logs\agent-*.log to watch requests, routing
// decisions, and errors as they happen.
builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
    .MinimumLevel.Information()
    // ASP.NET Core's own request/HttpClient logging at Information level floods
    // the file with framework noise for every hop - raised to Warning so the
    // log reads as "what the fleet did," not "what ASP.NET Core did."
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.Services.AddAGUIServer();
var fleetConfigStore = new FleetConfigStore(builder.Configuration);
builder.Services.AddSingleton(fleetConfigStore);
FleetOptions fleetOptions = FleetOptions.Load(builder.Configuration, fleetConfigStore);
builder.Services.AddSingleton(fleetOptions);
builder.Services.AddHttpClient(FleetHealthMonitor.HttpClientName, client =>
    client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient(WebTools.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(20));
// The diagram checker starts a Node process on first use, so allow it some time.
builder.Services.AddHttpClient(FrontendDiagramValidator.HttpClientName, client =>
    client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<FleetPlanStore>();
builder.Services.AddSingleton<FleetHealthMonitor>();
builder.Services.AddHostedService<FleetHealthBackgroundService>();
builder.Services.AddSingleton<FleetModeService>();
builder.Services.AddSingleton<FleetPlanModeService>();
builder.Services.AddSingleton<FleetActivityLog>();
builder.Services.AddSingleton<FleetContextStore>();
builder.Services.AddSingleton<FleetRequestContext>();
builder.Services.AddSingleton<ContextRetentionService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ContextRetentionService>());

static IChatClient CreateOllamaChatClient(Uri baseUrl, string model, TimeSpan networkTimeout, Microsoft.Extensions.Logging.ILogger rescueLogger)
{
    var client = new OpenAIClient(
        new ApiKeyCredential("ollama"), // Ollama ignores the key, but the SDK requires one.
        new OpenAIClientOptions
        {
            Endpoint = baseUrl,
            NetworkTimeout = networkTimeout
        });
    IChatClient raw = client.GetChatClient(model).AsIChatClient();

    // Works around a known, open Ollama bug (ollama/ollama#18530, #18563) where its
    // qwen3-coder tool-call parser can silently fail to structure a tool call, leaking
    // it as plain <function=...> text instead - see ToolCallRescueChatClient. No-op
    // for any node/model that isn't hitting this.
    return new ToolCallRescueChatClient(raw, rescueLogger);
}

var app = builder.Build();
RequestGuard.Use(app, RequestGuard.ParseAllowedNames(builder.Configuration["FLEET_ALLOWED_HOSTS"]));
FleetHealthMonitor healthMonitor = app.Services.GetRequiredService<FleetHealthMonitor>();
FleetModeService modeService = app.Services.GetRequiredService<FleetModeService>();
FleetPlanModeService planModeService = app.Services.GetRequiredService<FleetPlanModeService>();
FleetActivityLog activityLog = app.Services.GetRequiredService<FleetActivityLog>();
FleetContextStore contextStore = app.Services.GetRequiredService<FleetContextStore>();
FleetRequestContext requestContext = app.Services.GetRequiredService<FleetRequestContext>();
if (contextStore.RecoveredFrom is { } damagedDatabase)
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentFleet.Context").LogError(
        "The durable record was damaged and could not be opened. It was moved to {Path} and a new, empty one was started. " +
        "Nothing else was lost: plans, sessions from before the record existed, and the configuration are separate files.", damagedDatabase);
}
FleetPlanStore planStore = app.Services.GetRequiredService<FleetPlanStore>();
ILoggerFactory loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

// The durable record. Every AG-UI run joins the context named by its thread id, and everything the
// run does (routing, tool calls, decisions, plan steps) is written into it. See ContextAssembler.
var contextJournal = new FleetContextJournal(contextStore, requestContext, loggerFactory.CreateLogger("AgentFleet.Context"));
FleetContextMiddleware.Use(app, contextStore, requestContext, loggerFactory.CreateLogger("AgentFleet.Context"));
IHttpClientFactory httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

// Tools that only read, and so stay available while a plan is still awaiting approval. MCP tools
// join this set whenever their servers (re)connect and say which are read-only; the router and the
// tool registry share this one instance.
var readOnlyTools = new SwappableNameSet(PlanGate.BuiltInReadOnlyTools);

// Builds the router from the machines in the config. Called at startup and again whenever the saved
// machines or triage model change, so those apply without a restart (see SwappableChatClient).
//
// Every node comes from fleet.config.json. Text nodes fall back to the fallback node
// when they are down; vision nodes have no fallback: no text node can see images, so
// silently rerouting on failure would produce a confident-sounding answer about an
// image nobody looked at. Better to surface a clear error and let the user retry once
// the vision node is up.
FleetRoutingChatClient BuildRouter(FleetConfig config)
{
    IReadOnlyList<FleetNodeDefinition> nodes = fleetOptions.ReplaceNodes(config);
    healthMonitor.Forget();
    FleetNodeDefinition fallbackNode = nodes.Single(node => node.Fallback);

    IChatClient BuildNodeClient(FleetNodeDefinition node, IChatClient? fallback) =>
        new ResilientChatClient(
            CreateOllamaChatClient(
                node.OpenAiEndpoint,
                node.Model,
                fleetOptions.NetworkTimeout,
                loggerFactory.CreateLogger($"AgentFleet.Node.{node.Name}")),
            node,
            healthMonitor,
            loggerFactory.CreateLogger($"AgentFleet.Node.{node.Name}"),
            fallback);

    IChatClient fallbackClient = BuildNodeClient(fallbackNode, null);

    // The triage classifier runs on the fallback node's machine, with its own small model.
    IChatClient triageClient = new ResilientChatClient(
        CreateOllamaChatClient(fallbackNode.OpenAiEndpoint, config.TriageModel, fleetOptions.NetworkTimeout, loggerFactory.CreateLogger("AgentFleet.Triage")),
        fallbackNode,
        healthMonitor,
        loggerFactory.CreateLogger("AgentFleet.Triage"));

    List<FleetRouteTarget> routeTargets = nodes
        .Select(node => new FleetRouteTarget(
            node,
            node.Fallback
                ? fallbackClient
                : BuildNodeClient(node, node.Vision ? null : fallbackClient)))
        .ToList();

    return new FleetRoutingChatClient(
        triageClient,
        routeTargets,
        healthMonitor,
        modeService,
        planModeService,
        planStore,
        readOnlyTools,
        activityLog,
        loggerFactory.CreateLogger("AgentFleet.Router"),
        contextJournal);
}

// What the current router was built from, to tell whether a save needs a new one.
string RoutingFingerprint(FleetConfig config) =>
    System.Text.Json.JsonSerializer.Serialize(new { config.Nodes, config.TriageModel });

var fleetClient = new SwappableChatClient(BuildRouter(fleetConfigStore.Current));
string routedFingerprint = RoutingFingerprint(fleetConfigStore.Current);
var routerLock = new object();
// The sandbox needs Docker somewhere; with none available the tool is simply not offered.
DockerSandboxOptions sandboxOptions = DockerSandboxOptions.Resolve(fleetConfigStore.Current.Sandbox, builder.Configuration);
Microsoft.Extensions.Logging.ILogger sandboxLogger = loggerFactory.CreateLogger("AgentFleet.Sandbox");
sandboxLogger.LogInformation("Sandbox: {Summary}.", sandboxOptions.Summary);
DockerSandboxExecutor? sandboxExecutor = sandboxOptions.Mode == SandboxMode.Off
    ? null
    : new DockerSandboxExecutor(sandboxOptions, sandboxLogger);

HubShellOptions shellOptions = HubShellOptions.Load(builder.Configuration);
Microsoft.Extensions.Logging.ILogger shellLogger = loggerFactory.CreateLogger("AgentFleet.Shell");

[Description("""
    Runs code in a locked-down, throwaway Docker container and returns its combined
    stdout/stderr and exit code. The container has
    no persistent state (destroyed after this call), runs as a non-root user with a
    read-only root filesystem and all Linux capabilities dropped, and is subject to
    a short execution timeout - anything still running when it expires is killed.
    It does have outbound network access, so package installs (pip/npm) work.
    """)]
static async Task<string> RunSandboxedCodeAsync(
    DockerSandboxExecutor executor,
    [Description("One of: python, bash, javascript (or node, same as javascript).")] string language,
    [Description("The code to run. Sent to the interpreter's stdin, not a shell - no need to escape quotes.")] string code,
    CancellationToken cancellationToken)
{
    SandboxExecutionResult result = await executor.ExecuteAsync(language, code, cancellationToken);
    if (result.Error is not null)
    {
        return $"Error: {result.Error}";
    }

    return $"Exit code: {result.ExitCode}\n{result.Output}";
}

// Single source of truth for each tool's description - used both for the AITool
// itself (what the model reads) and the /api/fleet-config response (what the Config
// panel shows), so the UI never drifts from what the model was actually told.
Dictionary<string, string> toolDescriptions = new(StringComparer.Ordinal)
{
    ["run_sandboxed_code"] = "Runs code in a locked-down, throwaway Docker container and returns its output.",
    ["read_file"] = "Reads a text file from the hub machine's local filesystem and returns its contents. For a large file, pass startLine and endLine (1-based, inclusive) to read just that part.",
    ["write_file"] = "Writes (creates or overwrites) a text file on the hub machine's local filesystem, creating parent directories if needed. Use it for new files; to change part of an existing file, use edit_file instead.",
    ["edit_file"] = "Changes part of an existing file by replacing exact text: oldText must match the file exactly (including indentation) and appear only once, unless replaceAll is true. " +
        "Prefer this over write_file for any change to an existing file - it is faster and cannot accidentally drop the rest of the file. Read the file first so oldText is copied exactly.",
    ["list_directory"] = "Lists files and subdirectories at a path on the hub machine's local filesystem.",
    ["find_files"] = "Finds files by name with a glob pattern such as \"*.cs\" or \"src/**/*.tsx\" under a directory and returns their relative paths. Skips node_modules, bin, obj, .git and similar folders.",
    ["search_files"] = "Searches file contents under a directory for a regular expression, like grep -rn, and returns matching lines as path:line: text. " +
        "Skips binary files and folders such as node_modules, bin, obj and .git. Optionally restrict it with fileGlob (for example \"*.cs\") and ignoreCase.",
    ["run_git_command"] = "Runs a git command (e.g. \"status\", \"diff\", \"add -A\", \"commit -m 'message'\", \"log --oneline -10\") in the given repository directory on the hub machine and returns its output.",
    ["run_command"] = "Runs any shell command on the hub machine and returns its output - not limited to a fixed list. " +
        "This includes invoking any CLI toolchain already installed on the hub: dotnet (new/build/run/test, " +
        "including Blazor/ASP.NET Core/console/WPF projects), npm/yarn/pnpm, pip, cargo, go, mvn/gradle, " +
        $"docker (outside the sandbox), or anything else runnable from {HubPlatform.ShellDescription}. If a request needs a " +
        "toolchain's CLI and it's plausibly installed, call this instead of concluding the fleet \"doesn't " +
        "support\" that language or framework. Not sandboxed - runs directly on the hub with the backend's own " +
        "permissions.",
    ["propose_plan"] = "Saves a plan for the user to review and approve before any change is made. Use it in plan mode after exploring the code. It does not start the work; the plan then waits for the user's approval. " +
        "Arguments: title (short), goal (one or two sentences), workingDirectory (the project folder), assumptions (array of strings), openQuestions (array of strings, empty if none), " +
        "risks (array of strings), diagram (optional Mermaid source with every node label in double quotes), and steps: an array of objects, each with title, detail (specific enough for a smaller model to do without the rest of the conversation), " +
        "files (array of the files it touches), verify (a shell command that fails if the step did not work, such as \"dotnet build\" or \"node --test\"), tier (heavy, standard or light), and optional parallelGroup. " +
        "Only label consecutive steps with the same parallelGroup when they are independent and touch disjoint files; otherwise omit it. " +
        "Example: {\"title\":\"Add rate limiter\",\"goal\":\"...\",\"workingDirectory\":\"C:\\\\proj\",\"assumptions\":[\"...\"],\"openQuestions\":[],\"risks\":[\"...\"],\"steps\":[{\"title\":\"Create the class\",\"detail\":\"...\",\"files\":[\"limiter.js\"],\"verify\":\"node --test\",\"tier\":\"standard\"}]} ",
    ["get_plan"] = "Shows a plan, its status and how far each step has got. Without a planId it shows the most recent plan that is waiting or in progress.",
    ["record_decision"] = "Pins a decision or constraint in the durable record of this conversation so every agent that continues the work receives it, even on another machine or after a restart. " +
        "Use it for choices later work must follow (a library, a file layout, a naming rule, an interface, something the user decided). category is decision or constraint. Keep it to one clear sentence. " +
        "Do not pin what a system message already lists as pinned, and do not pin routine facts or guesses. " +
        "Never call it for a greeting, small talk or a question that decides nothing.",
    ["search_context"] = "Searches this conversation's durable record (messages, tool calls and results, decisions, checks) for a word or phrase and returns matching entries with their event ids. Use it to recover something said or done earlier instead of guessing.",
    ["validate_diagram"] = "Checks Mermaid source with the real Mermaid parser, applies safe formatting fixes, and returns the corrected source with a valid, invalid, or unchecked result.",
    ["web_search"] = "Searches the public internet and returns a numbered list of results (title, URL, snippet). " +
        "Use this whenever you need current information, documentation, or anything you're not certain about, " +
        "instead of guessing a URL or answering from memory alone - use web_fetch on the most relevant result " +
        "afterward to actually read it.",
    ["web_fetch"] = "Fetches a URL and returns its readable text content (HTML tags, scripts, and styles stripped) " +
        "- not raw markup. Use this to actually read a page found via web_search, or any URL you already have."
};

AITool? sandboxTool = sandboxExecutor is null
    ? null
    : AIFunctionFactory.Create(
        (string language, string code, CancellationToken cancellationToken) =>
            RunSandboxedCodeAsync(sandboxExecutor, language, code, cancellationToken),
        name: "run_sandboxed_code",
        description: toolDescriptions["run_sandboxed_code"]);

var contextTools = new ContextTools(contextJournal);

AITool recordDecisionTool = AIFunctionFactory.Create(
    (string decision, string? reason = null, string? category = null) => contextTools.RecordDecision(decision, reason, category),
    name: "record_decision",
    description: toolDescriptions["record_decision"]);

AITool searchContextTool = AIFunctionFactory.Create(
    (string query, int? limit = null) => contextTools.SearchContext(query, limit),
    name: "search_context",
    description: toolDescriptions["search_context"]);

AITool readFileTool = AIFunctionFactory.Create(
    (string path, int? startLine = null, int? endLine = null, CancellationToken cancellationToken = default) =>
        HubFileSystemTools.ReadFileAsync(path, startLine, endLine, cancellationToken),
    name: "read_file",
    description: toolDescriptions["read_file"]);

AITool editFileTool = AIFunctionFactory.Create(
    (string path, string oldText, string newText, bool? replaceAll = null, CancellationToken cancellationToken = default) =>
        HubFileSystemTools.EditFileAsync(path, oldText, newText, replaceAll, cancellationToken),
    name: "edit_file",
    description: toolDescriptions["edit_file"]);

AITool findFilesTool = AIFunctionFactory.Create(
    (string pattern, string? path = null) => HubFileSystemTools.FindFiles(pattern, path),
    name: "find_files",
    description: toolDescriptions["find_files"]);

AITool searchFilesTool = AIFunctionFactory.Create(
    (string pattern, string? path = null, string? fileGlob = null, bool? ignoreCase = null) =>
        HubFileSystemTools.SearchFiles(pattern, path, fileGlob, ignoreCase),
    name: "search_files",
    description: toolDescriptions["search_files"]);

AITool writeFileTool = AIFunctionFactory.Create(
    (string path, string content, CancellationToken cancellationToken) =>
        HubFileSystemTools.WriteFileAsync(path, content, cancellationToken),
    name: "write_file",
    description: toolDescriptions["write_file"]);

AITool listDirectoryTool = AIFunctionFactory.Create(
    (string path) => HubFileSystemTools.ListDirectory(path),
    name: "list_directory",
    description: toolDescriptions["list_directory"]);

AITool runGitCommandTool = AIFunctionFactory.Create(
    (string repositoryPath, string arguments, CancellationToken cancellationToken) =>
        HubShellTools.RunGitCommandAsync(repositoryPath, arguments, shellOptions, shellLogger, cancellationToken),
    name: "run_git_command",
    description: toolDescriptions["run_git_command"]);

AITool runCommandTool = AIFunctionFactory.Create(
    (string command, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        HubShellTools.RunCommandAsync(command, workingDirectory, shellOptions, shellLogger, cancellationToken),
    name: "run_command",
    description: toolDescriptions["run_command"]);

Microsoft.Extensions.Logging.ILogger webLogger = loggerFactory.CreateLogger("AgentFleet.Web");

AITool webSearchTool = AIFunctionFactory.Create(
    (string query, int? maxResults = null, CancellationToken cancellationToken = default) =>
        WebTools.SearchWebAsync(query, maxResults, httpClientFactory, webLogger, cancellationToken),
    name: "web_search",
    description: toolDescriptions["web_search"]);

AITool webFetchTool = AIFunctionFactory.Create(
    (string url, CancellationToken cancellationToken) =>
        WebTools.FetchUrlAsync(url, httpClientFactory, webLogger, cancellationToken),
    name: "web_fetch",
    description: toolDescriptions["web_fetch"]);

// Plan tools. They are offered only in plan mode (PlanGate hides them otherwise), and the
// rules that matter - approval before any step completes, steps in order, a step only done
// when its own verify command passes - live in PlanTools, not in the prompt.
string frontendUrl = builder.Configuration["FLEET_FRONTEND_URL"] ?? "http://localhost:3000";
var planTools = new PlanTools(
    planStore,
    new FrontendDiagramValidator(httpClientFactory, frontendUrl),
    (command, workingDirectory, cancellationToken) =>
        HubShellTools.RunCommandAsync(command, workingDirectory, shellOptions, shellLogger, cancellationToken),
    contextJournal);

AITool proposePlanTool = AIFunctionFactory.Create(
    // The descriptions are not decoration: a raw JsonElement parameter is described to the model as
    // the schema literal "true" (anything), and Ollama refuses a tool whose property is not an
    // object. A description makes each one an object, and tells the model what to send.
    (string title,
        string goal,
        string? workingDirectory = null,
        [Description("Array of strings: what you are assuming")] System.Text.Json.JsonElement? assumptions = null,
        [Description("Array of strings: questions for the user, empty if none")] System.Text.Json.JsonElement? openQuestions = null,
        [Description("Array of strings: what could go wrong")] System.Text.Json.JsonElement? risks = null,
        string? diagram = null,
        [Description("Array of step objects, each with title, detail, files (array), verify and tier")] System.Text.Json.JsonElement? steps = null,
        CancellationToken cancellationToken = default) =>
        planTools.ProposePlanAsync(title, goal, workingDirectory, assumptions, openQuestions, risks, diagram, steps, cancellationToken),
    name: "propose_plan",
    description: toolDescriptions["propose_plan"]);

AITool getPlanTool = AIFunctionFactory.Create(
    (string? planId = null) => planTools.GetPlan(planId),
    name: "get_plan",
    description: toolDescriptions["get_plan"]);

AITool validateDiagramTool = AIFunctionFactory.Create(
    (string code, CancellationToken cancellationToken) => planTools.ValidateDiagramAsync(code, cancellationToken),
    name: "validate_diagram",
    description: toolDescriptions["validate_diagram"]);

// Every built-in tool is given to the agent; which of them are switched on, and which MCP servers are
// connected, is decided per request by the tool registry (DynamicToolsChatClient), so a change saved in
// the Config panel applies to the next message without a restart. A switched-off tool is removed from
// the request itself, so the model cannot call it whatever the instructions say.
(string Name, AITool Tool)[] sandboxEntry = sandboxTool is null ? [] : [("run_sandboxed_code", sandboxTool)];
(string Name, AITool Tool)[] builtInTools =
[
    .. sandboxEntry,
    ("read_file", readFileTool),
    ("write_file", writeFileTool),
    ("edit_file", editFileTool),
    ("list_directory", listDirectoryTool),
    ("find_files", findFilesTool),
    ("search_files", searchFilesTool),
    ("run_git_command", runGitCommandTool),
    ("run_command", runCommandTool),
    ("web_search", webSearchTool),
    ("web_fetch", webFetchTool),
    ("propose_plan", proposePlanTool),
    ("get_plan", getPlanTool),
    ("validate_diagram", validateDiagramTool),
    ("record_decision", recordDecisionTool),
    ("search_context", searchContextTool)
];

// MCP servers from fleet.config.json contribute their tools alongside the built-ins. Connected at
// startup and again whenever the saved server list changes; a server that will not start is logged,
// reported in the Config panel and skipped, never blocking the backend.
var toolRegistry = new FleetToolRegistry(
    fleetConfigStore,
    builtInTools.Select(tool => tool.Name),
    readOnlyTools,
    loggerFactory);
await toolRegistry.ReloadAsync(app.Lifetime.ApplicationStopping);
app.Lifetime.ApplicationStopping.Register(() => toolRegistry.DisposeAsync().AsTask().GetAwaiter().GetResult());

// Everything the Config panel can list: each tool's description and where it comes from.
// Built from the code's own tool list, not just the persisted file, so a tool added after
// the config file was first written still shows up (IsToolEnabled defaults it to on).
object ToolsPayload(FleetConfig config) =>
    toolDescriptions
        .Where(pair => pair.Key != "run_sandboxed_code" || sandboxTool is not null)
        .Select(pair => (Name: pair.Key, Description: pair.Value, Source: "built-in"))
        .Concat(toolRegistry.Mcp.Tools.Select(entry => (
            Name: entry.Tool.Name,
            Description: entry.Tool.Description ?? string.Empty,
            Source: entry.Server)))
        .ToDictionary(
            entry => entry.Name,
            entry => new
            {
                enabled = config.IsToolEnabled(entry.Name),
                description = entry.Description,
                source = entry.Source
            },
            StringComparer.Ordinal);

// The stand-in plan mode swaps in for a call it refuses (see PlanGate). It is always present
// and never offered to the model, so it is not part of the user-facing tool list.
AITool blockedByPlanTool = AIFunctionFactory.Create(
    (string tool) => PlanGate.BlockedMessage(tool),
    name: PlanGate.BlockedToolName,
    description: "Internal stand-in for a call that plan mode refused. Never call this yourself.");

AITool[] agentTools = builtInTools
    .Select(tool => tool.Tool)
    .Append(blockedByPlanTool)
    .ToArray();

// The layer that actually runs tool calls, configured explicitly instead of left to the
// agent's defaults, for three reasons:
//  - IncludeDetailedErrors: by default a failed call comes back as the opaque "Function
//    failed.", so a model that sent a badly shaped argument could not see what to fix.
//  - A higher iteration cap: the default stops a request after a few dozen calls, which a
//    plan with several steps of edit, run and verify can exceed.
//  - The plan gate, again. The router already refuses disallowed calls in the planning phase;
//    checking here too, where execution happens and with the whole conversation in hand,
//    means no code path can run a write tool before a plan is approved.
// What ran, with what and what came back, goes into the durable record. The two context tools are
// left out: the decision they pin is itself the record.
void RecordToolCall(FunctionInvocationContext context, string toolName, string result, bool isError)
{
    if (toolName is "record_decision" or "search_context")
    {
        return;
    }

    string arguments;
    try
    {
        arguments = System.Text.Json.JsonSerializer.Serialize(context.Arguments);
    }
    catch (Exception)
    {
        arguments = "(arguments could not be serialised)";
    }

    contextJournal.RecordToolExecution(context.CallContent?.CallId, toolName, arguments, result, isError);
}

IChatClient agentClient = new ChatClientBuilder(fleetClient)
    // Outermost: the current tool list goes onto the request before anything looks for a tool by name.
    .Use(inner => new DynamicToolsChatClient(inner, toolRegistry, (messages, options, toolName) =>
        ContextTools.ShouldOffer(
            toolName,
            hasContext: contextJournal.Current is not null,
            planModeOn: planModeService.Enabled,
            fromPlanRunner: options?.AdditionalProperties?.ContainsKey(FleetRoutingChatClient.RunnerTierKey) == true,
            lastUserText: messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text)))
    .UseFunctionInvocation(loggerFactory, invocation =>
    {
        invocation.IncludeDetailedErrors = true;
        invocation.MaximumIterationsPerRequest = 200;
        invocation.MaximumConsecutiveErrorsPerRequest = 6;
        invocation.FunctionInvoker = async (context, cancellationToken) =>
        {
            string toolName = context.Function.Name;
            // The plan runner is the executor: its requests carry the runner key and are exempt.
            bool fromRunner = context.Options?.AdditionalProperties?.ContainsKey(FleetRoutingChatClient.RunnerTierKey) == true;
            if (toolName != PlanGate.BlockedToolName && !fromRunner)
            {
                PlanGateResult gate = PlanGate.Evaluate(
                    planModeService.Enabled,
                    context.Messages as IReadOnlyList<ChatMessage> ?? context.Messages.ToList(),
                    planStore);
                if (gate.Phase != PlanPhase.Off && !PlanGate.IsAllowed(gate.Phase, toolName, readOnlyTools))
                {
                    string refusal = PlanGate.BlockedMessage(toolName);
                    RecordToolCall(context, toolName, refusal, isError: true);
                    return refusal;
                }
            }

            try
            {
                object? result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
                RecordToolCall(context, toolName, result?.ToString() ?? string.Empty, isError: false);
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordToolCall(context, toolName, exception.Message, isError: true);
                throw;
            }
        };
    })
    .Build();

var fleetAgent = new ChatClientAgent(
    agentClient,
    instructions: $"""
        You are a local coding assistant. Give direct, accurate answers for the user's
        coding request.

        {HubPlatform.PathInstructions}

        Available tools (only the ones actually offered to you this run are callable -
        fleet.config.json can disable any of these, so don't assume all are present):
        - run_sandboxed_code: executes code in an isolated, throwaway Docker container
          with no access to the user's real files - use it for quick, self-contained
          snippets, not for anything touching a real project.
        - read_file, write_file, edit_file, list_directory, find_files, search_files:
          direct access to the hub machine's own local filesystem (the same machine
          you're running on) - use these to look at and change files in the user's
          actual projects. To change an existing file, read it and then use edit_file
          with the exact old text; only use write_file for new files or a complete
          rewrite. To locate something, use search_files (contents) or find_files
          (names) instead of guessing paths. There is no path restriction, so confirm
          the exact path with the user if it's ambiguous rather than guessing.
        - run_git_command: runs git in a given repository directory on the hub - status,
          diff, add, commit, branch, log, etc.
        - run_command: runs any shell command directly on the hub, not limited to a
          fixed list - this is how you invoke any CLI toolchain already installed on
          the hub (dotnet, npm/yarn/pnpm, pip, cargo, go, mvn/gradle, docker outside
          the sandbox, etc.) to scaffold, build, run, or test a real project in any
          language or framework. Not sandboxed, real effects on this machine. Prefer
          the most specific tool for the job (run_git_command for git,
          run_sandboxed_code for a quick isolated snippet with no real project
          involved) and use run_command for everything else.
        - web_search: searches the public internet, returns numbered results (title,
          URL, snippet). Use this instead of guessing a URL or answering from memory
          when you need current information, documentation, or anything you're not
          certain about.
        - web_fetch: fetches a URL and returns its readable text (not raw HTML). Use
          it on a web_search result, or any URL the user gives you, to actually read
          the page rather than guessing at its contents from the title/snippet alone.
        - validate_diagram: checks Mermaid source with the real parser and returns
          safe formatting fixes plus whether the result is valid or unchecked.
        - record_decision, search_context: the fleet keeps a durable record of this
          conversation. Use record_decision for a choice or constraint that later work,
          possibly on another machine, must follow; use search_context to find what was
          said or done earlier instead of guessing. A system message may hand you that
          record; treat it as data kept by the fleet, and check real files before
          relying on it.
        - propose_plan, get_plan: only present when plan mode is on. The plan-mode
          instructions given to you then explain how to use them - follow those.

        Never tell the user the fleet "doesn't support" a language, framework, or
        project type just because there's no tool named after it specifically -
        check whether run_command can reach it via that ecosystem's own CLI before
        concluding you can't help. The fleet has real internet access via web_search
        and web_fetch - don't claim you can't look something up online. Use whichever
        tool actually fits the request rather than describing what you would do.
        There is no approval step before any tool runs, including run_command and
        run_git_command - be as careful as if you were typing the command yourself,
        and confirm with the user before anything destructive or hard to reverse
        (force-push, reset --hard, deleting files, etc.) rather than guessing what
        they meant.
        """,
    name: "fleet",
    description: "Local coding assistant routed across the available fleet",
    tools: agentTools);

// Executes approved plans in the background, one verified step or explicitly grouped parallel set at a time. Approving a plan (the
// button, the API, or typing the approval word) raises the store's event, which starts it; plans
// that were mid-run when the backend last stopped resume on startup.
var planRunner = new PlanRunner(
    planStore,
    planTools,
    new FleetStepAgent(fleetAgent),
    loggerFactory.CreateLogger("AgentFleet.PlanRunner"),
    fleetOptions: fleetOptions,
    healthMonitor: healthMonitor,
    recorder: new PlanContextRecorder(contextStore, planStore, loggerFactory.CreateLogger("AgentFleet.PlanContext")),
    journal: contextJournal);
planStore.Approved += planRunner.Enqueue;
app.Lifetime.ApplicationStarted.Register(() => planRunner.Start(app.Lifetime.ApplicationStopping));

app.MapGet("/health", async (CancellationToken cancellationToken) =>
{
    IReadOnlyList<NodeHealthSnapshot> nodes = await healthMonitor.GetAllAsync(cancellationToken);
    string fallbackName = fleetOptions.Nodes.Single(node => node.Fallback).Name;
    bool hubReady = nodes.Single(node => node.Name == fallbackName).Ready;
    bool allReady = nodes.All(node => node.Ready);

    return Results.Json(
        new
        {
            status = hubReady ? (allReady ? "ok" : "degraded") : "unhealthy",
            nodes = nodes.Select(node => new
            {
                name = node.Name,
                model = node.Model,
                ready = node.Ready,
                reachable = node.Reachable,
                checkedAtUtc = node.CheckedAtUtc,
                reason = node.Failure
            })
        },
        statusCode: hubReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/fleet-mode", () => Results.Json(new { mode = modeService.Mode.ToString().ToLowerInvariant() }));

app.MapPost("/api/fleet-mode", (FleetModeRequest request) =>
{
    if (!Enum.TryParse(request.Mode, ignoreCase: true, out FleetMode parsed))
    {
        return Results.BadRequest(new { error = "mode must be 'conservative' or 'aggressive'." });
    }

    modeService.SetMode(parsed);
    return Results.Json(new { mode = modeService.Mode.ToString().ToLowerInvariant() });
});

app.MapGet("/api/plan-mode", () => Results.Json(new { enabled = planModeService.Enabled }));

app.MapPost("/api/plan-mode", (FleetPlanModeRequest request) =>
{
    planModeService.SetEnabled(request.Enabled);
    return Results.Json(new { enabled = planModeService.Enabled });
});

app.MapGet("/api/fleet-status", async (CancellationToken cancellationToken) =>
{
    IReadOnlyList<NodeHealthSnapshot> nodes = await healthMonitor.GetAllAsync(cancellationToken);
    return Results.Json(new
    {
        mode = modeService.Mode.ToString().ToLowerInvariant(),
        planMode = planModeService.Enabled,
        nodes = nodes.Select(node => new
        {
            name = node.Name,
            model = node.Model,
            ready = node.Ready,
            reachable = node.Reachable
        }),
        recentActivity = activityLog.Recent().Select(entry => new
        {
            timestampUtc = entry.TimestampUtc,
            node = entry.Node,
            reason = entry.Reason
        })
    });
});

app.MapGet("/api/fleet-config", () =>
{
    FleetConfig config = fleetConfigStore.Current;
    return Results.Json(new
    {
        triageModel = config.TriageModel,
        nodes = config.Nodes.Select(node => new { node.Name, node.Url, node.Model, node.Purpose, node.Tier, node.Vision, node.Fallback }),
        tools = ToolsPayload(config),
        mcpServers = config.McpServerMap,
        // What actually happened at the last startup, which can differ from the saved
        // config (edited since, or a server that failed to start).
        mcpStatus = toolRegistry.Mcp.Statuses,
        history = new { deleteAfterDays = config.History?.DeleteAfterDays ?? 0 }
    });
});

// VS Code's extension uses this narrow projection to avoid offering the same MCP
// server tool twice when a server is configured both here and in VS Code. Do not
// expose the full fleet config (which can contain server headers and URLs).
app.MapGet("/api/fleet-tools", () =>
{
    FleetConfig config = fleetConfigStore.Current;
    return Results.Json(new
    {
        tools = toolRegistry.Mcp.Tools
            .Where(entry => config.IsToolEnabled(entry.Tool.Name))
            .Select(entry => new
            {
                name = entry.Tool.Name,
                source = entry.Server,
                description = entry.Tool.Description ?? string.Empty,
                inputSchema = entry.Tool.ProtocolTool.InputSchema
            })
    });
});

// Starts a server once and lists its tools, so the Config panel can check an entry before it is saved.
// It runs the given command on this machine, which the tools already allow; RequestGuard keeps browsers
// on other sites out.
app.MapPost("/api/mcp/test", async (McpTestRequest request, CancellationToken cancellationToken) =>
{
    string name = string.IsNullOrWhiteSpace(request.Name) ? "test" : request.Name.Trim();
    FleetMcpServerConfig server = request.Server with { Type = (request.Server.Type ?? string.Empty).Trim().ToLowerInvariant() };
    string? problem = server.Type switch
    {
        "stdio" when string.IsNullOrWhiteSpace(server.Command) => "A stdio server needs a command.",
        "http" when string.IsNullOrWhiteSpace(server.Url) => "An http server needs a URL.",
        "stdio" or "http" => null,
        _ => "Type must be stdio or http."
    };
    if (problem is not null)
    {
        return Results.BadRequest(new { error = problem });
    }

    McpTestResult result = await McpToolProvider.TestAsync(name, server, loggerFactory, cancellationToken);
    return Results.Json(result);
});

app.MapGet("/api/fleet-config/models", async (string url, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https"))
    {
        return Results.BadRequest(new { error = "url must be an absolute http(s) URL." });
    }

    Uri tagsEndpoint = new UriBuilder(
        endpoint.Scheme,
        endpoint.Host,
        endpoint.IsDefaultPort ? -1 : endpoint.Port,
        "api/tags").Uri;

    using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeoutSource.CancelAfter(TimeSpan.FromSeconds(5));

    try
    {
        HttpClient client = httpClientFactory.CreateClient(FleetHealthMonitor.HttpClientName);
        using HttpResponseMessage response = await client.GetAsync(tagsEndpoint, timeoutSource.Token);
        if (!response.IsSuccessStatusCode)
        {
            return Results.Json(
                new { error = $"HTTP {(int)response.StatusCode} from {tagsEndpoint}" },
                statusCode: StatusCodes.Status502BadGateway);
        }

        OllamaModelsResponse? tags = await response.Content.ReadFromJsonAsync<OllamaModelsResponse>(timeoutSource.Token);
        return Results.Json(new
        {
            models = (tags?.Models ?? [])
                .Where(model => model.Name is not null)
                .Select(model => new { name = model.Name, sizeBytes = model.Size })
                .OrderBy(model => model.name, StringComparer.Ordinal)
        });
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
    {
        return Results.Json(
            new { error = $"Could not reach {tagsEndpoint}: {exception.Message}" },
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPut("/api/fleet-config", async (FleetConfigUpdateRequest request, CancellationToken cancellationToken) =>
{
    try
    {
        FleetConfig current = fleetConfigStore.Current;
        FleetConfig updated = current with
        {
            TriageModel = request.TriageModel?.Trim() is { Length: > 0 } triageModel ? triageModel : current.TriageModel,
            Nodes = request.Nodes is { Count: > 0 }
                ? request.Nodes.Select(node => new FleetNodeConfig(
                    node.Name.Trim(),
                    node.Url.Trim(),
                    node.Model.Trim(),
                    node.Purpose,
                    node.Tier,
                    node.Vision,
                    node.Fallback)).ToList()
                : current.Nodes,
            Tools = request.Tools is { Count: > 0 }
                ? request.Tools.ToDictionary(kv => kv.Key, kv => new FleetToolConfig(kv.Value), StringComparer.Ordinal)
                : current.Tools,
            // Unlike nodes and tools, an empty object here is meaningful: it removes every server.
            McpServers = request.McpServers is not null
                ? request.McpServers.ToDictionary(kv => kv.Key.Trim(), kv => kv.Value, StringComparer.Ordinal)
                : current.McpServers,
            History = request.History ?? current.History
        };

        FleetConfig saved = fleetConfigStore.Save(updated);

        // Everything here applies now: tools and MCP servers through the tool registry, machines and the
        // triage model by building a new router and swapping it in.
        await toolRegistry.ReloadAsync(cancellationToken);
        bool machinesChanged = false;
        lock (routerLock)
        {
            string fingerprint = RoutingFingerprint(saved);
            if (fingerprint != routedFingerprint)
            {
                fleetClient.Swap(BuildRouter(saved));
                routedFingerprint = fingerprint;
                machinesChanged = true;
            }
        }

        if (machinesChanged)
        {
            loggerFactory.CreateLogger("AgentFleet.Router").LogInformation(
                "Machines reloaded from the config: {Nodes}.", string.Join(", ", saved.Nodes.Select(node => $"{node.Name} ({node.Model})")));
        }

        const bool restartRequired = false;
        return Results.Json(new
        {
            triageModel = saved.TriageModel,
            nodes = saved.Nodes.Select(node => new { node.Name, node.Url, node.Model, node.Purpose, node.Tier, node.Vision, node.Fallback }),
            tools = ToolsPayload(saved),
            mcpServers = saved.McpServerMap,
            mcpStatus = toolRegistry.Mcp.Statuses,
            history = new { deleteAfterDays = saved.History?.DeleteAfterDays ?? 0 },
            restartRequired,
            message = machinesChanged
                ? "Saved and applied. The machines are live now; their status updates within a few seconds."
                : "Saved and applied."
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/api/plans", () => Results.Json(planStore.List()));

app.MapGet("/api/plans/{id}", (string id) =>
    planStore.Get(id) is { } plan ? Results.Json(plan) : Results.NotFound());

app.MapGet("/api/plans/{id}/markdown", (string id) =>
    planStore.Get(id) is { } plan
        ? Results.Text(FleetPlanStore.ToMarkdown(plan), "text/markdown")
        : Results.NotFound());

// What happened while the plan ran, for reading in the morning.
app.MapGet("/api/plans/{id}/report", (string id) =>
    planStore.Get(id) is { } plan
        ? Results.Text(FleetPlanStore.ToReportMarkdown(plan), "text/markdown")
        : Results.NotFound());

app.MapPost("/api/plans/{id}/approve", (string id, bool exportToProject = false) =>
{
    PlanRecord? plan = planStore.Get(id);
    if (plan is null)
    {
        return Results.NotFound();
    }

    if (plan.Status is not (PlanStatus.AwaitingApproval or PlanStatus.Blocked))
    {
        return Results.BadRequest(new { error = $"This plan is {plan.Status.Replace('-', ' ')}, so it cannot be approved." });
    }

    try
    {
        PlanRecord? approved = planStore.Approve(id, exportToProject);
        return approved is null
            ? Results.NotFound()
            : Results.Json(approved);
    }
    catch (PlanExportException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapPost("/api/plans/{id}/reject", (string id) =>
{
    PlanRecord? plan = planStore.Get(id);
    if (plan is null)
    {
        return Results.NotFound();
    }

    return plan.Status == PlanStatus.Done
        ? Results.BadRequest(new { error = "This plan is already done." })
        : Results.Json(planStore.Reject(id));
});

app.MapPost("/api/plans/{id}/stop", (string id) =>
    planStore.Get(id) is null
        ? Results.NotFound()
        : planRunner.Stop(id)
            ? Results.Json(planStore.Get(id))
            : Results.BadRequest(new { error = "This plan is not running." }));

app.MapGet("/api/logs/tail", (int? lines) =>
{
    int count = Math.Clamp(lines ?? 150, 10, 1000);
    string logsDir = Path.Combine(AppContext.BaseDirectory, "logs");

    string? latestLogFile = Directory.Exists(logsDir)
        ? Directory.EnumerateFiles(logsDir, "agent-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
        : null;

    if (latestLogFile is null)
    {
        return Results.Json(new { file = (string?)null, lines = Array.Empty<string>() });
    }

    // Explicit FileShare.ReadWrite: Serilog's own file sink has this file open
    // for writing the whole time, so a plain File.OpenRead here would throw.
    using var stream = new FileStream(latestLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    var allLines = new List<string>();
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        allLines.Add(line);
    }

    return Results.Json(new { file = Path.GetFileName(latestLogFile), lines = allLines.TakeLast(count) });
});

// Sessions are the visible chats of the durable journal: the same record the AG-UI runs write
// into, so a conversation started in one surface can be resumed in another.
app.MapGet("/api/sessions", () => Results.Json(contextStore.ListSessions()));

app.MapGet("/api/sessions/{id}", (string id) =>
{
    SessionRecord? record = contextStore.GetSessionRecord(id);
    return record is null ? Results.NotFound() : Results.Json(record);
});

app.MapPut("/api/sessions/{id}", (string id, SessionUpsertRequest request) =>
{
    try
    {
        return Results.Json(contextStore.UpsertMessages(id, request.Title, request.Messages, "session-api"));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

// Deleting a chat that a plan runs in only removes it from the list: the plan's record must survive.
app.MapDelete("/api/sessions/{id}", (string id) =>
    planStore.UsesContext(id)
        ? (contextStore.ClearMessages(id) ? Results.Ok() : Results.NotFound())
        : (contextStore.DeleteContext(id) ? Results.Ok() : Results.NotFound()));

// The durable record itself: what an agent was handed, what happened, what is pinned.
app.MapGet("/api/contexts", () => Results.Json(contextStore.ListContexts()));

// How big the record is, and which chats a cleanup would delete (nothing is deleted here).
app.MapGet("/api/contexts/storage", (int? olderThanDays) =>
{
    int configured = fleetConfigStore.Current.History?.DeleteAfterDays ?? 0;
    int days = Math.Clamp(olderThanDays ?? configured, 0, FleetConfig.MaxHistoryDays);
    return Results.Json(new
    {
        storage = contextStore.Storage(),
        deleteAfterDays = configured,
        keepDeliveriesPerChat = ContextRetention.KeepDeliveriesPerContext,
        preview = ContextRetention.Run(contextStore, days, planStore.UnfinishedContextIds(), DateTimeOffset.UtcNow, dryRun: true)
    });
});

// Deletes the chats not used for the given number of days now, then shrinks the file.
app.MapPost("/api/contexts/cleanup", (ContextCleanupRequest request) =>
{
    if (request.OlderThanDays is < 1 or > FleetConfig.MaxHistoryDays)
    {
        return Results.BadRequest(new { error = $"olderThanDays must be between 1 and {FleetConfig.MaxHistoryDays}." });
    }

    ContextCleanupResult result = ContextRetention.Run(
        contextStore, request.OlderThanDays, planStore.UnfinishedContextIds(), DateTimeOffset.UtcNow, dryRun: false, shrink: true);
    app.Logger.LogInformation(
        "History cleanup from the web UI: deleted {Chats} chat(s) not used for {Days} days, trimmed {Deliveries} delivered context block(s).",
        result.Chats.Count, request.OlderThanDays, result.DeliveriesTrimmed);
    return Results.Json(result);
});

app.MapGet("/api/contexts/{id}", (string id, string? taskId) =>
{
    if (contextStore.GetContext(id) is not { } summary)
    {
        return Results.NotFound();
    }

    return Results.Json(new FleetContextInspection(
        summary,
        contextStore.RecentEvents(id, 80).Where(e => e.Kind != FleetContextEventKind.ContextAssembled).ToList(),
        contextStore.ListHandoffs(id, 10),
        contextStore.LatestArtifacts(id, 100),
        contextStore.LatestCheckpoint(id),
        contextStore.LatestSummary(id),
        ContextAssembler.Assemble(contextStore, new ContextAssemblyRequest(id, taskId, MaxCharacters: 6000)),
        // Not just the latest events: a decision pinned a long time ago must still be listed (and unpinnable).
        contextStore.EventsOfKinds(id, [FleetContextEventKind.Decision], 100, pinnedOnly: true)));
});

app.MapGet("/api/contexts/{id}/events", (string id, int? limit, string? kind) =>
{
    if (contextStore.GetContext(id) is null)
    {
        return Results.NotFound();
    }

    return Results.Json(kind is { Length: > 0 }
        ? contextStore.EventsOfKinds(id, [kind], limit ?? 100)
        : contextStore.RecentEvents(id, limit ?? 100));
});

// What each agent actually received: the assembled blocks the router handed to a model, newest last.
app.MapGet("/api/contexts/{id}/deliveries", (string id, int? limit) =>
    contextStore.GetContext(id) is null
        ? Results.NotFound()
        : Results.Json(contextStore.EventsOfKinds(id, [FleetContextEventKind.ContextAssembled], limit ?? 20)));

app.MapGet("/api/contexts/{id}/artifacts", (string id) =>
    contextStore.GetContext(id) is null
        ? Results.NotFound()
        : Results.Json(contextStore.VerifyArtifacts(id)));

app.MapPost("/api/contexts/{id}/decisions", (string id, ContextDecisionRequest request) =>
{
    if (contextStore.GetContext(id) is null)
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { error = "text is required." });
    }

    string category = string.Equals(request.Category?.Trim(), "constraint", StringComparison.OrdinalIgnoreCase) ? "constraint" : "decision";
    long eventId = contextStore.AppendEvent(
        id,
        FleetContextEventKind.Decision,
        new { text = ContextText.Clip(request.Text, 600), reason = string.IsNullOrWhiteSpace(request.Reason) ? null : ContextText.Clip(request.Reason, 400), category },
        actor: "user", agent: "api", pinned: true);
    return Results.Json(new { eventId });
});

app.MapPost("/api/contexts/{id}/events/{eventId:long}/pin", (string id, long eventId, bool? pinned) =>
    contextStore.PinEvent(id, eventId, pinned ?? true) ? Results.Ok() : Results.NotFound());

app.MapPost("/api/contexts/{id}/compact", (string id) =>
{
    if (contextStore.GetContext(id) is null)
    {
        return Results.NotFound();
    }

    FleetContextSummaryRecord? summary = ContextCompactor.Compact(contextStore, id);
    return summary is null
        ? Results.Json(new { compacted = false, message = "There is not enough older history to summarise yet." })
        : Results.Json(new { compacted = true, summary });
});

// A plain JSON export of the whole record, for backup or moving a conversation elsewhere.
app.MapGet("/api/contexts/{id}/export", (string id) =>
{
    if (contextStore.GetContext(id) is not { } summary)
    {
        return Results.NotFound();
    }

    return Results.Json(new
    {
        context = summary,
        session = contextStore.GetSessionRecord(id),
        tasks = contextStore.ListTasks(id),
        events = contextStore.AllEvents(id),
        handoffs = contextStore.ListHandoffs(id, 100),
        artifacts = contextStore.ListArtifacts(id, 500),
        summary = contextStore.LatestSummary(id)
    });
});

app.MapAGUIServer("/", fleetAgent);

await app.RunAsync();

internal sealed record FleetModeRequest(string Mode);

internal sealed record FleetPlanModeRequest(bool Enabled);

internal sealed record SessionUpsertRequest(string? Title, System.Text.Json.JsonElement Messages);

internal sealed record McpTestRequest(string? Name, FleetMcpServerConfig Server);

internal sealed record ContextDecisionRequest(string Text, string? Reason = null, string? Category = null);

internal sealed record ContextCleanupRequest(int OlderThanDays);

internal sealed record FleetConfigNodeUpdate(
    string Name,
    string Url,
    string Model,
    string Purpose,
    string? Tier = null,
    bool Vision = false,
    bool Fallback = false);

internal sealed record FleetConfigUpdateRequest(
    string? TriageModel,
    IReadOnlyList<FleetConfigNodeUpdate>? Nodes,
    IReadOnlyDictionary<string, bool>? Tools,
    IReadOnlyDictionary<string, FleetMcpServerConfig>? McpServers = null,
    FleetHistoryConfig? History = null);

internal sealed record OllamaModelsResponse(IReadOnlyList<OllamaModelSummary>? Models);

internal sealed record OllamaModelSummary(string? Name, long Size);
