using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

internal sealed record FleetRouteTarget(FleetNodeDefinition Node, IChatClient Client);

/// <summary>
/// Sends each request to one of the configured nodes. Which nodes exist, what tier each
/// serves, which one handles images and which one is the fallback all come from
/// fleet.config.json - nothing here knows a machine's name.
/// </summary>
internal sealed class FleetRoutingChatClient : IChatClient
{
    // Matches the marker the Next.js proxy (src/app/api/copilotkit/[[...slug]]/route.ts)
    // repacks an image attachment into, working around the AG-UI hosting library
    // dropping the protocol's native "image" message part during deserialization.
    private static readonly Regex ImageMarker = new(
        @"\[\[FLEET_IMAGE:(?<mime>[\w/+.-]+):(?<data>[A-Za-z0-9+/=]+)\]\]",
        RegexOptions.Compiled);

    /// <summary>
    /// Set by the plan runner to preserve the step's requested tier. Conservative mode routes by tier;
    /// Aggressive mode prefers the hub unless the step has an explicit machine choice.
    /// </summary>
    public const string RunnerTierKey = "fleet.runner.tier";
    public const string RunnerMachineKey = "fleet.runner.machine";

    /// <summary>Set on a plan runner request that is not carrying out a step (see IStepAgent.OpenSession(role)).</summary>
    public const string RunnerRoleKey = "fleet.runner.role";

    private readonly IChatClient _triageClient;
    private readonly IReadOnlyList<FleetRouteTarget> _targets;
    private readonly IReadOnlyList<FleetRouteTarget> _textTargets;
    private readonly IReadOnlyList<FleetRouteTarget> _visionTargets;
    private readonly IReadOnlyDictionary<string, FleetRouteTarget> _byName;
    private readonly FleetRouteTarget _fallback;
    private readonly FleetHealthMonitor _healthMonitor;
    private readonly FleetModeService _modeService;
    private readonly FleetPlanModeService _planModeService;
    private readonly FleetPlanStore _planStore;
    private readonly IReadOnlySet<string> _readOnlyTools;
    private readonly FleetActivityLog _activityLog;
    private readonly ILogger _logger;
    private readonly FleetContextJournal? _journal;
    private bool _disposed;

    public FleetRoutingChatClient(
        IChatClient triageClient,
        IReadOnlyList<FleetRouteTarget> targets,
        FleetHealthMonitor healthMonitor,
        FleetModeService modeService,
        FleetPlanModeService planModeService,
        FleetPlanStore planStore,
        IReadOnlySet<string> readOnlyTools,
        FleetActivityLog activityLog,
        ILogger logger,
        FleetContextJournal? journal = null)
    {
        _journal = journal;
        _triageClient = triageClient;
        _targets = targets;
        _healthMonitor = healthMonitor;
        _modeService = modeService;
        _planModeService = planModeService;
        _planStore = planStore;
        _readOnlyTools = readOnlyTools;
        _activityLog = activityLog;
        _logger = logger;

        _textTargets = targets.Where(target => !target.Node.Vision).ToList();
        _visionTargets = targets.Where(target => target.Node.Vision).ToList();
        _byName = targets.ToDictionary(target => target.Node.Name, StringComparer.Ordinal);

        if (_textTargets.Count == 0)
        {
            throw new ArgumentException("At least one text node is required.", nameof(targets));
        }

        _fallback = _textTargets.Single(target => target.Node.Fallback);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> messageList = PrepareMessages(messages);
        string? runnerTier = RunnerTier(options);
        string? runnerMachine = RunnerMachine(options);
        PlanGateResult gate = runnerTier is null
            ? PlanGate.Evaluate(_planModeService.Effective, messageList, _planStore)
            : new PlanGateResult(PlanPhase.Off, null);
        // Non-streaming path isn't used by the AG-UI chat flow today, so it doesn't get
        // the "answered by" footer that GetStreamingResponseAsync appends.
        (FleetRouteTarget target, string routeReason) = await SelectTargetWithJournalAsync(messageList, gate, runnerTier, runnerMachine, cancellationToken);
        options = StripRunnerKey(options);
        (IReadOnlyList<ChatMessage> sendMessages, ChatOptions? sendOptions) = ApplyPlanMode(messageList, options, gate);
        RecordRoute(target, gate, runnerTier, routeReason);
        sendMessages = InjectContext(sendMessages, target.Node);
        ChatResponse response;
        try
        {
            response = await target.Client.GetResponseAsync(sendMessages, StripToolsIfVision(target, sendOptions), cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _journal?.RecordError(_journal?.ActualNode ?? target.Node.Name, "response", exception.Message);
            throw;
        }

        string servedBy = _journal?.ActualNode ?? target.Node.Name;
        _journal?.RecordAssistantOutput(servedBy, response.Text ?? string.Empty,
            response.Messages.Sum(message => message.Contents.Count(content => content is FunctionCallContent)));

        foreach (ChatMessage message in response.Messages)
        {
            var asUpdate = new ChatResponseUpdate(message.Role, message.Contents);
            message.Contents = PlanGate.GuardCalls(asUpdate, gate, _readOnlyTools).Contents;
        }

        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> messageList = PrepareMessages(messages);
        _logger.LogInformation(
            "Fleet request received: {MessageCount} messages, last role {LastRole}.",
            messageList.Count,
            messageList.Count > 0 ? messageList[^1].Role.ToString() : "none");

        // Worked out once per request: it can approve a plan (an approval word from the user), so
        // routing and the tool filter must both see the same result.
        string? runnerTier = RunnerTier(options);
        string? runnerMachine = RunnerMachine(options);
        PlanGateResult gate = runnerTier is null
            ? PlanGate.Evaluate(_planModeService.Effective, messageList, _planStore)
            : new PlanGateResult(PlanPhase.Off, null);
        (FleetRouteTarget target, string routeReason) = await SelectTargetWithJournalAsync(messageList, gate, runnerTier, runnerMachine, cancellationToken);
        string route = target.Node.Name;
        options = StripRunnerKey(options);

        (IReadOnlyList<ChatMessage> sendMessages, ChatOptions? sendOptions) = ApplyPlanMode(messageList, options, gate);
        RecordRoute(target, gate, runnerTier, routeReason);
        sendMessages = InjectContext(sendMessages, target.Node);

        bool sawToolCall = false;
        bool responseFailed = false;
        int toolCallCount = 0;
        var outputText = new System.Text.StringBuilder();
        IAsyncEnumerator<ChatResponseUpdate>? stream = null;

        try
        {
            try
            {
                stream = target.Client.GetStreamingResponseAsync(
                    sendMessages,
                    StripToolsIfVision(target, sendOptions),
                    cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _journal?.RecordError(_journal?.ActualNode ?? route, "stream setup", exception.Message);
                throw;
            }

            while (true)
            {
                ChatResponseUpdate update = null!;
                Exception? streamError = null;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        break;
                    }

                    update = stream.Current;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    string failedNode = _journal?.ActualNode ?? route;
                    _journal?.RecordError(failedNode, "stream", exception.Message, outputText.ToString());
                    _logger.LogError(
                        exception,
                        "Fleet request to {FleetNode} failed mid-stream after {MessageCount} messages.",
                        failedNode,
                        messageList.Count);
                    streamError = exception;
                }

                if (streamError is not null)
                {
                    responseFailed = true;
                    string failedNode = _journal?.ActualNode ?? route;
                    string notice = $"\n\n[Fleet stopped while {failedNode} was answering. The text above is partial, not a complete answer. Open Context for the error details, then retry.]";
                    outputText.Append(notice);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, notice);
                    break;
                }

                ChatResponseUpdate guarded = PlanGate.GuardCalls(
                    update,
                    gate,
                    _readOnlyTools,
                    tool => _logger.LogWarning(
                        "Plan mode blocked a call to {Tool} from {FleetNode} ({PlanPhase} phase).",
                        tool,
                        route,
                        gate.Phase));
                sawToolCall |= guarded.Contents.Any(content => content is FunctionCallContent);
                toolCallCount += guarded.Contents.Count(content => content is FunctionCallContent);
                if (_journal is not null && guarded.Text is { Length: > 0 } piece)
                {
                    outputText.Append(piece);
                }

                yield return guarded;
            }
        }
        finally
        {
            if (stream is not null)
            {
                try
                {
                    await stream.DisposeAsync();
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _journal?.RecordError(_journal?.ActualNode ?? route, "stream cleanup", exception.Message, outputText.ToString());
                    throw;
                }
            }
        }

        string servedBy = _journal?.ActualNode ?? route;
        _journal?.RecordAssistantOutput(servedBy, outputText.ToString(), toolCallCount);

        // Only tag the response actually shown to the user as final text, not a round
        // that's about to be followed by a tool call and another round of this loop.
        if (!sawToolCall)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"\n\n_— via {servedBy}_");
        }

        _logger.LogInformation("Fleet request {CompletionState} via {FleetNode}.", responseFailed ? "stopped early" : "completed", servedBy);
    }

    private void RecordRoute(FleetRouteTarget target, PlanGateResult gate, string? runnerTier, string routeReason) =>
        _journal?.RecordRoute(
            target.Node.Name,
            runnerTier ?? target.Node.Tier,
            gate.Phase.ToString(),
            routeReason);

    private async Task<(FleetRouteTarget Target, string Reason)> SelectTargetWithJournalAsync(
        IReadOnlyList<ChatMessage> messages,
        PlanGateResult gate,
        string? runnerTier,
        string? runnerMachine,
        CancellationToken cancellationToken)
    {
        try
        {
            if (runnerTier is null)
            {
                FleetRouteTarget target = await SelectTargetAsync(messages, gate, cancellationToken);
                return (target, gate.Phase == PlanPhase.Planning ? "planning" : "routed");
            }

            if (runnerMachine is null)
            {
                if (_modeService.Mode == FleetMode.Aggressive)
                {
                    string reason = $"plan step ({runnerTier}), aggressive hub mode";
                    _activityLog.Record(_fallback.Node.Name, reason);
                    return (_fallback, reason);
                }

                return (await PickForTierAsync(runnerTier, cancellationToken), $"plan step ({runnerTier})");
            }

            if (!_byName.TryGetValue(runnerMachine, out FleetRouteTarget? selected) || selected.Node.Vision)
            {
                throw new InvalidOperationException($"Plan step machine '{runnerMachine}' is not a configured text machine.");
            }

            string selectedReason = $"plan step ({runnerTier}), selected machine ({runnerMachine})";
            _activityLog.Record(selected.Node.Name, selectedReason);
            return (selected, selectedReason);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _journal?.RecordError(null, "route selection", exception.Message);
            throw;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType == typeof(FleetRoutingChatClient)
            ? this
            : _triageClient.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _triageClient.Dispose();
        foreach (FleetRouteTarget target in _targets)
        {
            target.Client.Dispose();
        }
    }

    private async Task<FleetRouteTarget> SelectTargetAsync(
        IReadOnlyList<ChatMessage> messages,
        PlanGateResult gate,
        CancellationToken cancellationToken)
    {
        // Plan mode decides the route itself, from the plan's own state, so it holds for every
        // model call in a tool loop and needs no memory between calls. Planning always goes to
        // the heavy tier: the plan is the most consequential thing the fleet writes, so it is not
        // left to a triage guess. While a plan runs, each call goes to the tier the plan gave the
        // step currently in progress, so a trivial edit does not occupy the big machine in Conservative mode.
        // Aggressive mode sends unpinned plan steps to the hub.
        if (gate.Phase != PlanPhase.Off)
        {
            FleetRouteTarget planned = await PickForPlanAsync(gate, cancellationToken);
            _logger.LogInformation(
                "Fleet router selected {FleetNode} (plan mode, {PlanPhase}).",
                planned.Node.Name,
                gate.Phase);
            return planned;
        }

        // Once a tool result is already in the history, this call is a continuation
        // of an in-progress tool round (the auto function-invocation loop re-invokes
        // this same IChatClient with the grown message list to get the final answer),
        // not a fresh user request. Re-running the triage classifier on that history
        // breaks it - the classifier's prompt only expects a plain question and has
        // no instructions for tool-call/tool-result messages, and re-classifying here
        // also can't recover which specialist actually made the pending tool call,
        // since this client is stateless across calls. Going straight to the fallback
        // node avoids both problems: it's the fleet's designated safe/capable node and
        // can synthesize a final answer from the tool result regardless of which node
        // originally ran it.
        if (messages.Any(message => message.Role == ChatRole.Tool))
        {
            _logger.LogInformation(
                "Fleet router: continuing an in-progress tool round on {FleetNode}.",
                _fallback.Node.Name);
            _activityLog.Record(_fallback.Node.Name, "continuing tool round");
            return _fallback;
        }

        // Deterministic, not classified: no text node can see images, so any message
        // carrying one (this turn or an earlier one still in history, e.g. a follow-up
        // question about a screenshot) always goes to a vision node. With no vision node
        // configured, PrepareMessages has already replaced the images with a note.
        if (_visionTargets.Count > 0 && messages.Any(ContainsImage))
        {
            FleetRouteTarget vision = await PickReadyAsync(_visionTargets, cancellationToken) ?? _visionTargets[0];
            _logger.LogInformation("Fleet router selected {FleetNode} (image attachment present).", vision.Node.Name);
            _activityLog.Record(vision.Node.Name, "image attachment");
            return vision;
        }

        List<FleetRouteTarget> candidates = await ChooseRepresentativesAsync(cancellationToken);
        if (candidates.Count == 1)
        {
            _activityLog.Record(candidates[0].Node.Name, "only one tier configured");
            return candidates[0];
        }

        FleetMode mode = _modeService.Mode;
        Dictionary<string, string> nodeByTier = candidates.ToDictionary(
            candidate => candidate.Node.Tier!,
            candidate => candidate.Node.Name,
            StringComparer.Ordinal);
        var routingOptions = new ChatOptions
        {
            Instructions = FleetRoutingPrompt.BuildInstructions(mode, nodeByTier),
            Temperature = 0,
            MaxOutputTokens = 40,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                FleetRoutingPrompt.BuildSchema(candidates.Select(candidate => candidate.Node.Name)),
                "route",
                "Which fleet node should handle this request")
        };

        string route = _fallback.Node.Name;
        try
        {
            ChatResponse triageResponse = await _triageClient.GetResponseAsync(
                messages,
                routingOptions,
                cancellationToken);
            route = ParseRoute(
                triageResponse.Text,
                candidates.Select(candidate => candidate.Node.Name).ToHashSet(StringComparer.Ordinal),
                route);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _journal?.RecordError(route, "routing", exception.Message);
            _logger.LogWarning(
                exception,
                "Fleet triage failed; routing the request to {FleetNode} as a safe fallback.",
                route);
        }

        _logger.LogInformation("Fleet router selected {FleetNode} ({FleetMode} mode).", route, mode);
        _activityLog.Record(route, $"{mode} mode");
        return _byName[route];
    }

    private IReadOnlyList<ChatMessage> InjectContext(IReadOnlyList<ChatMessage> messages, FleetNodeDefinition node)
    {
        string? style = AgentStylePrompt.Build(node);
        string? injection = _journal?.BuildInjection(node.Name, style) ?? style;
        return ContextInjection.Apply(messages, injection);
    }

    // Planning always goes to the heavy tier. Once a plan runs, chat is only a monitor: the fallback
    // node answers, while the runner sends each step to its own tier.
    private async Task<FleetRouteTarget> PickForPlanAsync(PlanGateResult gate, CancellationToken cancellationToken) =>
        gate.Phase == PlanPhase.Planning
            ? await PickForTierAsync(FleetTiers.Heavy, cancellationToken)
            : _fallback;

    private async Task<FleetRouteTarget> PickForTierAsync(string tier, CancellationToken cancellationToken)
    {
        List<FleetRouteTarget> representatives = await ChooseRepresentativesAsync(cancellationToken);
        Dictionary<string, string> nodeByTier = representatives.ToDictionary(
            target => target.Node.Tier!,
            target => target.Node.Name,
            StringComparer.Ordinal);
        FleetRouteTarget target = _byName[FleetRoutingPrompt.ResolveTier(tier, nodeByTier)];
        _activityLog.Record(target.Node.Name, $"plan step ({tier})");
        return target;
    }

    private static string? RunnerTier(ChatOptions? options) =>
        options?.AdditionalProperties is { } properties &&
        properties.TryGetValue(RunnerTierKey, out object? value) &&
        value is string tier
            ? tier
            : null;

    private static string? RunnerMachine(ChatOptions? options) =>
        options?.AdditionalProperties is { } properties &&
        properties.TryGetValue(RunnerMachineKey, out object? value) &&
        value is string machine &&
        !string.IsNullOrWhiteSpace(machine)
            ? machine
            : null;

    internal static string? RunnerRole(ChatOptions? options) =>
        options?.AdditionalProperties is { } properties &&
        properties.TryGetValue(RunnerRoleKey, out object? value) &&
        value is string role &&
        !string.IsNullOrWhiteSpace(role)
            ? role
            : null;

    // The key is for this class only; do not pass it on to the model's API.
    private static ChatOptions? StripRunnerKey(ChatOptions? options)
    {
        if (options?.AdditionalProperties is null ||
            (!options.AdditionalProperties.ContainsKey(RunnerTierKey) && !options.AdditionalProperties.ContainsKey(RunnerMachineKey) &&
             !options.AdditionalProperties.ContainsKey(RunnerRoleKey)))
        {
            return options;
        }

        ChatOptions cleaned = options.Clone();
        cleaned.AdditionalProperties!.Remove(RunnerTierKey);
        cleaned.AdditionalProperties.Remove(RunnerMachineKey);
        cleaned.AdditionalProperties.Remove(RunnerRoleKey);
        return cleaned;
    }

    // One node per tier for the classifier to choose between: the first ready node in
    // each tier, or the first configured one if none is ready (its own client then
    // falls back to the fallback node).
    private async Task<List<FleetRouteTarget>> ChooseRepresentativesAsync(CancellationToken cancellationToken)
    {
        var chosen = new List<FleetRouteTarget>();
        foreach (string tier in FleetTiers.All)
        {
            List<FleetRouteTarget> group = _textTargets.Where(target => target.Node.Tier == tier).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            chosen.Add(await PickReadyAsync(group, cancellationToken) ?? group[0]);
        }

        return chosen;
    }

    private async Task<FleetRouteTarget?> PickReadyAsync(
        IReadOnlyList<FleetRouteTarget> group,
        CancellationToken cancellationToken)
    {
        if (group.Count == 1)
        {
            return group[0];
        }

        foreach (FleetRouteTarget target in group)
        {
            NodeHealthSnapshot snapshot = await _healthMonitor.GetNodeAsync(target.Node.Name, cancellationToken);
            if (snapshot.Ready)
            {
                return target;
            }
        }

        return null;
    }

    internal static string ParseRoute(string? responseText, IReadOnlySet<string> allowed, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);
                if (document.RootElement.TryGetProperty("node", out JsonElement nodeElement) &&
                    nodeElement.ValueKind == JsonValueKind.String)
                {
                    string? fromJson = nodeElement.GetString()?.Trim();
                    if (fromJson is not null && allowed.Contains(fromJson))
                    {
                        return fromJson;
                    }
                }
            }
            catch (JsonException)
            {
                // Not valid JSON - fall through to the plain-text fallback below. This
                // should be rare given the schema-constrained ResponseFormat, but
                // keeps behavior safe if that constraint is ever relaxed or unsupported.
            }
        }

        string candidate = responseText?.Trim() ?? string.Empty;
        return allowed.Contains(candidate) ? candidate : fallbackName;
    }

    private IReadOnlyList<ChatMessage> PrepareMessages(IEnumerable<ChatMessage> messages)
    {
        IReadOnlyList<ChatMessage> messageList = ReconstructImages(Materialize(messages));
        return _visionTargets.Count == 0 ? StripImages(messageList) : messageList;
    }

    // Undoes the Next.js proxy's marker-based repacking (see ImageMarker above),
    // turning the marked text back into real image content the vision model can use.
    private static IReadOnlyList<ChatMessage> ReconstructImages(IReadOnlyList<ChatMessage> messages)
    {
        List<ChatMessage>? rebuilt = null;

        for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            ChatMessage message = messages[messageIndex];
            List<AIContent>? newContents = null;

            for (int contentIndex = 0; contentIndex < message.Contents.Count; contentIndex++)
            {
                if (message.Contents[contentIndex] is not TextContent text)
                {
                    continue;
                }

                Match match = ImageMarker.Match(text.Text);
                if (!match.Success)
                {
                    continue;
                }

                newContents ??= new List<AIContent>(message.Contents);
                string remainingText = ImageMarker.Replace(text.Text, string.Empty).Trim();
                var replacement = new List<AIContent>();
                if (remainingText.Length > 0)
                {
                    replacement.Add(new TextContent(remainingText));
                }
                replacement.Add(new DataContent(
                    Convert.FromBase64String(match.Groups["data"].Value),
                    match.Groups["mime"].Value));

                int indexInNewContents = newContents.IndexOf(text);
                newContents.RemoveAt(indexInNewContents);
                newContents.InsertRange(indexInNewContents, replacement);
            }

            if (newContents is not null)
            {
                rebuilt ??= new List<ChatMessage>(messages.Take(messageIndex));
                rebuilt.Add(new ChatMessage(message.Role, newContents));
            }
            else
            {
                rebuilt?.Add(message);
            }
        }

        return rebuilt ?? messages;
    }

    // With no vision node configured, a text model would either error on the image or
    // silently ignore it and answer as if it had seen it. Replace it with a plain note
    // so the model can say it could not see the attachment.
    private static IReadOnlyList<ChatMessage> StripImages(IReadOnlyList<ChatMessage> messages)
    {
        if (!messages.Any(ContainsImage))
        {
            return messages;
        }

        return messages
            .Select(message => !ContainsImage(message)
                ? message
                : new ChatMessage(
                    message.Role,
                    message.Contents
                        .Select(content => IsImage(content)
                            ? new TextContent("[An image was attached, but this fleet has no vision node configured, so it could not be read.]")
                            : content)
                        .ToList()))
            .ToList();
    }

    // Vision models such as qwen2.5vl return HTTP 400 "does not support tools" if the
    // request carries any tools at all - unlike the text specialists, they can't just
    // ignore them. The agent attaches all its tools uniformly via ChatOptions, so this
    // strips them for vision nodes; Clone() shallow-copies collections, so this doesn't
    // touch the shared options object other routes use.
    private static ChatOptions? StripToolsIfVision(FleetRouteTarget target, ChatOptions? options)
    {
        if (!target.Node.Vision || options is null || options.Tools is null or { Count: 0 })
        {
            return options;
        }

        ChatOptions stripped = options.Clone();
        stripped.Tools = null;
        return stripped;
    }

    private static bool ContainsImage(ChatMessage message) => message.Contents.Any(IsImage);

    private static bool IsImage(AIContent content) =>
        content switch
        {
            DataContent data => data.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
            UriContent uri => uri.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };

    // Plan-first is enforced here, in code: while there is no approved plan the model is only
    // offered read-only tools (plus propose_plan), so no prompt wording can get it to write.
    // With plan mode off nothing changes except that the plan tools are hidden.
    private (IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) ApplyPlanMode(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        PlanGateResult gate)
    {
        ChatOptions? filtered = PlanGate.FilterTools(options, gate.Phase, _readOnlyTools);

        switch (gate.Phase)
        {
            case PlanPhase.Planning:
                string instructions = PlanGate.PlanningInstructions(gate.Plan, MachinesByTier());
                if (PlanFile.NamedInLatestRequest(messages) is { } named)
                {
                    instructions += "\n\n" + PlanGate.PlanFileNote(named.Path, named.Steps);
                }

                if (filtered?.Instructions is null)
                {
                    return (Prepend(instructions, messages), filtered);
                }

                // Swap the agent's instructions out rather than adding to them (see PlanningInstructions).
                // Clone first: with no tools the filter hands back the caller's own options object.
                ChatOptions planning = filtered.Clone();
                planning.Instructions = instructions;
                return (messages, planning);
            case PlanPhase.Executing:
                return (Prepend(PlanGate.MonitorDirective(gate.Plan!), messages), filtered);
            default:
                return (messages, filtered);
        }
    }

    // "heavy: hub (qwen3-coder:30b); standard: worker1 (qwen2.5-coder:7b)": what the planner can spread steps over.
    private string MachinesByTier() =>
        string.Join("; ", FleetTiers.All
            .Select(tier => (Tier: tier, Nodes: _textTargets.Where(target => target.Node.Tier == tier).Select(target => $"{target.Node.Name} ({target.Node.Model})").ToList()))
            .Where(entry => entry.Nodes.Count > 0)
            .Select(entry => $"{entry.Tier}: {string.Join(", ", entry.Nodes)}"));

    private static IReadOnlyList<ChatMessage> Prepend(string directive, IReadOnlyList<ChatMessage> messages) =>
        new List<ChatMessage> { new(ChatRole.System, directive) }.Concat(messages).ToList();

    private static IReadOnlyList<ChatMessage> Materialize(IEnumerable<ChatMessage> messages) =>
        messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
}
