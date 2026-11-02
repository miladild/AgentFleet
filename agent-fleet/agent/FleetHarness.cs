using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFleet;

/// <summary>
/// Composition of the agent pipeline. Five rules govern the architecture:
/// 1. Context fitting happens once, per machine, in the node client (ContextSizeChatClient).
/// 2. The tools a model is offered are decided in one place: the tool registry through DynamicToolsChatClient.
/// 3. Tool-call policy lives in the function invoker (FleetFunctionInvoker).
/// 4. Conversation history has one owner: the session store.
/// 5. New hooks use the framework's extension points (middleware, the function-invocation hook, context providers for adding instructions, the session store).
/// </summary>
internal static class FleetHarness
{
    /// <summary>
    /// Build the agent chat client from the fleet client and supporting services.
    /// </summary>
    internal static IChatClient BuildAgentClient(
        IChatClient fleetClient,
        FleetToolRegistry toolRegistry,
        FleetContextJournal journal,
        FleetPlanModeService planModeService,
        FleetFunctionInvoker invoker,
        ILoggerFactory loggerFactory,
        bool recoveryNoteEnabled = false)
    {
        var builder = new ChatClientBuilder(fleetClient);
        if (recoveryNoteEnabled)
        {
            // Added only when the setting is on, so with it off the chain and the model input are exactly as before.
            builder.Use(inner => new ChatRecoveryNoteClient(inner, journal, enabled: true));
        }

        return builder
            // The current tool list goes onto the request before anything looks for a tool by name.
            .Use(inner => new DynamicToolsChatClient(inner, toolRegistry, (messages, options, toolName) =>
                PlanRunnerToolPolicy.Offers(
                    toolName,
                    fromRunner: options?.AdditionalProperties?.ContainsKey(FleetRoutingChatClient.RunnerTierKey) == true,
                    role: FleetRoutingChatClient.RunnerRole(options),
                    disputeOffered: FleetRoutingChatClient.DisputeOffered(options),
                    webOffered: FleetRoutingChatClient.WebOffered(options)) &&
                ContextTools.ShouldOffer(
                    toolName,
                    hasContext: journal.Current is not null,
                    planModeOn: planModeService.Effective,
                    fromPlanRunner: options?.AdditionalProperties?.ContainsKey(FleetRoutingChatClient.RunnerTierKey) == true,
                    lastUserText: messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text)))
            .UseFunctionInvocation(loggerFactory, invocation =>
            {
                invocation.IncludeDetailedErrors = true;
                invocation.MaximumIterationsPerRequest = 200;
                invocation.MaximumConsecutiveErrorsPerRequest = 6;
                invocation.FunctionInvoker = invoker.InvokeAsync;
            })
            .Build();
    }

    /// <summary>
    /// The agent: the framework's HarnessAgent over the agent client. Its parts that the fleet does its own way are off: its
    /// default instructions (they ask a model to explain its reasoning between tool calls, the opposite of what the plan steps
    /// measured), its compaction (fitting is per machine, in the node client), its web search (the fleet's own web tools are
    /// used), skills, to-dos, modes, file memory, its tool auto-approval and its telemetry (the agent is instrumented with the
    /// fleet's allow-list). The agent client keeps its own function invocation innermost, so every tool call, the harness's
    /// included, passes the fleet's tool policy once.
    /// </summary>
    internal static HarnessAgent BuildAgent(IChatClient agentClient, string instructions, IList<AITool> tools,
        IEnumerable<AIContextProvider> contextProviders, ILoggerFactory? loggerFactory = null) =>
        agentClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "fleet",
            Description = "Local coding assistant routed across the available fleet",
            HarnessInstructions = "",
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools },
            AIContextProviders = [.. contextProviders],
            // No MaxContextWindowTokens or CompactionStrategy: the harness compacts nothing.
            DisableWebSearch = true,
            DisableAgentSkillsProvider = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true,
        }, loggerFactory);
}
