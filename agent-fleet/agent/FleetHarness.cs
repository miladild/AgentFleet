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
        ILoggerFactory loggerFactory)
    {
        return new ChatClientBuilder(fleetClient)
            // A plan step on a worker is told about the worker's machine, not the hub's (see WorkerPlatformChatClient).
            .Use(inner => new WorkerPlatformChatClient(inner))
            // Next: the current tool list goes onto the request before anything looks for a tool by name.
            .Use(inner => new DynamicToolsChatClient(inner, toolRegistry, (messages, options, toolName) =>
                PlanRunnerToolPolicy.Offers(
                    toolName,
                    fromRunner: options?.AdditionalProperties?.ContainsKey(FleetRoutingChatClient.RunnerTierKey) == true,
                    role: FleetRoutingChatClient.RunnerRole(options)) &&
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
}
