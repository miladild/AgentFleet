using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// The tool call invoker policy: enforces plan approval, tool availability by phase,
/// iteration limits, and termination conditions for plan steps.
/// </summary>
internal sealed class FleetFunctionInvoker(
    FleetContextJournal journal,
    FleetPlanModeService planModeService,
    FleetPlanStore planStore,
    SwappableNameSet readOnlyTools,
    PlanTools planTools,
    ILogger toolLoopLogger,
    int planStepToolRounds)
{
    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        string toolName = context.Function.Name;
        // The plan runner is the executor: its requests carry the runner key and are exempt.
        bool fromRunner = context.Options?.AdditionalProperties?.ContainsKey(FleetRoutingChatClient.RunnerTierKey) == true;
        if (fromRunner)
        {
            string? command = null;
            if (toolName == "run_command" && context.Arguments.TryGetValue("command", out object? commandValue))
                command = commandValue?.ToString();
            else if (toolName == "run_git_command" && context.Arguments.TryGetValue("arguments", out object? gitArguments))
                command = gitArguments?.ToString();
            if (PlanRunnerToolPolicy.Refusal(toolName, command, FleetRoutingChatClient.RunnerRole(context.Options)) is { } refusal)
            {
                context.Terminate = true;
                RecordToolCall(context, toolName, refusal, isError: true);
                return refusal;
            }
        }
        if (toolName != PlanGate.BlockedToolName && !fromRunner)
        {
            PlanGateResult gate = PlanGate.Evaluate(
                planModeService.Effective,
                context.Messages as IReadOnlyList<ChatMessage> ?? context.Messages.ToList(),
                planStore);
            if (gate.Phase != PlanPhase.Off && !PlanGate.IsAllowed(gate.Phase, toolName, readOnlyTools))
            {
                string refusal = PlanGate.BlockedMessage(toolName);
                RecordToolCall(context, toolName, refusal, isError: true);
                return refusal;
            }
        }

        // A planner copying a plan file by hand shortens every step, and one has proposed a plan of its own instead of
        // the file the user named: the steps come from the file.
        if (toolName == "propose_plan" &&
            (PlanFile.NamedByLatestRequest(context.Messages as IReadOnlyList<ChatMessage> ?? context.Messages.ToList(), context.Arguments) ??
             PlanFile.FindTranscribed(context.Messages, context.Arguments)) is { } planFile)
        {
            context.Arguments["planFile"] = planFile;
        }

        // A plan step's commands run in its project folder unless the model names another (see StepWorkingDirectory).
        if (fromRunner && toolName == "run_command" &&
            string.IsNullOrWhiteSpace(context.Arguments.TryGetValue("workingDirectory", out object? folder) ? folder?.ToString() : null) &&
            planTools.StepWorkingDirectory(context.Messages) is { } stepFolder)
        {
            context.Arguments["workingDirectory"] = stepFolder;
        }

        try
        {
            object? result = await ToolLoopGuard.InvokeAsync(context, fromRunner, cancellationToken);
            if (fromRunner)
            {
                result = ToolLoopGuard.Apply(context, result, toolLoopLogger);
            }

            RecordToolCall(context, toolName, result?.ToString() ?? string.Empty, isError: false);

            // A saved plan ends the turn. The planner has nothing left to do, but it has been seen proposing again and
            // again in one reply, and each proposal replaces the last, so a later, worse version won (measured: six
            // proposals in one turn, the last a one-step plan). The plan itself is the answer the user sees.
            if (toolName == "propose_plan" && result?.ToString()?.StartsWith("Plan saved", StringComparison.Ordinal) == true)
            {
                context.Terminate = true;
            }

            // A proposal is the auditor's whole answer: the runner reads it from the reply, so the turn ends here.
            if (fromRunner && toolName == "propose_check")
            {
                context.Terminate = true;
            }

            // Iteration counts from 0: this ends the attempt after planStepToolRounds rounds of tool calls.
            if (fromRunner && context.Iteration >= planStepToolRounds - 1)
            {
                context.Terminate = true;
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordToolCall(context, toolName, exception.Message, isError: true);
            throw;
        }
    }

    private void RecordToolCall(FunctionInvocationContext context, string toolName, string result, bool isError)
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

        journal.RecordToolExecution(context.CallContent?.CallId, toolName, arguments, result, isError || ToolReturnedError(result));
    }

    private static bool ToolReturnedError(string result) => ToolLoopGuard.IsError(result);
}
