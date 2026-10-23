using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// The tool call invoker policy: enforces plan approval, tool availability by phase,
/// iteration limits, termination conditions for plan steps, and, when the config turns it on,
/// the user's approval of chat tool calls (see ChatApprovals).
/// </summary>
internal sealed class FleetFunctionInvoker(
    FleetContextJournal journal,
    FleetPlanModeService planModeService,
    FleetPlanStore planStore,
    SwappableNameSet readOnlyTools,
    PlanTools planTools,
    ILogger toolLoopLogger,
    int planStepToolRounds,
    ChatApprovals? chatApprovals = null,
    Func<string>? chatApprovalsOn = null)
{
    internal static readonly IReadOnlySet<string> RiskyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "run_command", "run_git_command", "http_request"
    };

    private static bool ShouldAsk(string level, string toolName) => level switch
    {
        "off" => false,
        "risky" => RiskyTools.Contains(toolName),
        _ => true
    };

    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        string toolName = context.Function.Name;
        Activity? activity = Activity.Current;
        Activity? ownedActivity = null;
        if (activity?.DisplayName.StartsWith("execute_tool", StringComparison.Ordinal) != true)
            activity = ownedActivity = FleetTelemetry.Source.StartActivity($"tool {toolName}", ActivityKind.Internal);
        activity?.SetTag("fleet.tool", toolName);
        try
        {
            // The outcome comes from the path that produced the result: a tool's own text can say "refused" and still have run.
            (object? result, string outcome) = await InvokeCoreAsync(context, cancellationToken);
            activity?.SetTag("fleet.result_chars", result?.ToString()?.Length ?? 0);
            activity?.SetTag("fleet.tool_outcome", outcome);
            return result;
        }
        catch (Exception)
        {
            activity?.SetTag("fleet.tool_outcome", "error");
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            ownedActivity?.Dispose();
        }
    }

    private async ValueTask<(object? Result, string Outcome)> InvokeCoreAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        string toolName = context.Function.Name;
        // The plan runner is the executor: its requests carry the runner key and are exempt.
        bool fromRunner = context.Options?.AdditionalProperties?.ContainsKey(FleetRoutingChatClient.RunnerTierKey) == true;
        if (fromRunner)
        {
            if (context.Options?.AdditionalProperties?.TryGetValue(StepToolActivity.Key, out object? activity) == true && activity is StepToolActivity counter)
            {
                counter.Record(toolName);
            }

            string? command = null;
            if (toolName == "run_command" && context.Arguments.TryGetValue("command", out object? commandValue))
                command = commandValue?.ToString();
            else if (toolName == "run_git_command" && context.Arguments.TryGetValue("arguments", out object? gitArguments))
                command = gitArguments?.ToString();
            if (PlanRunnerToolPolicy.Refusal(toolName, command, FleetRoutingChatClient.RunnerRole(context.Options)) is { } refusal)
            {
                context.Terminate = true;
                RecordToolCall(context, toolName, refusal, isError: true);
                return (refusal, "refused");
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
                return (refusal, "refused");
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

        // The user answers a chat tool call before it runs, after the gate and the argument fixes above, so the arguments
        // shown are the ones that run. The plan runner is exempt: an approved plan runs unattended by design.
        if (!fromRunner && chatApprovals is not null && ShouldAsk(chatApprovalsOn?.Invoke() ?? "off", toolName) &&
            !readOnlyTools.Contains(toolName) && !IsFleetBookkeeping(toolName))
        {
            ApprovalDecision decision = await chatApprovals.RequestAsync(
                journal.Current?.ContextId ?? string.Empty, toolName, SerializeArguments(context), cancellationToken);
            if (decision != ApprovalDecision.Approved)
            {
                // Returned to the model as the tool's result, not as a stop: the model can say what did not run and carry on.
                string refusal = decision == ApprovalDecision.Rejected
                    ? $"The user did not approve running {toolName}."
                    : $"{toolName} was not approved in time.";
                RecordToolCall(context, toolName, refusal, isError: true);
                return (refusal, "approval-denied");
            }
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

            // The model is told how many rounds are left in the last few, so it can leave the project in a working state.
            int roundsLeft = planStepToolRounds - (context.Iteration + 1);
            if (fromRunner && roundsLeft is >= 1 and <= ToolLoopGuard.RoundBudgetWarnAt)
            {
                result = ToolLoopGuard.WithRoundBudget(result, roundsLeft);
            }

            string text = result?.ToString() ?? string.Empty;
            return (result, text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ? "error" : "ok");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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

        journal.RecordToolExecution(context.CallContent?.CallId, toolName, SerializeArguments(context), result, isError || ToolReturnedError(result));
    }

    private static string SerializeArguments(FunctionInvocationContext context)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(context.Arguments);
        }
        catch (Exception)
        {
            return "(arguments could not be serialised)";
        }
    }

    // The plan proposal and the durable record change nothing on a machine, and a plan is approved separately in its
    // review, so asking about these would only repeat the question.
    private static bool IsFleetBookkeeping(string toolName) =>
        toolName is "propose_plan" or "record_decision" or "search_context" or PlanGate.BlockedToolName;

    private static bool ToolReturnedError(string result) => ToolLoopGuard.IsError(result);
}
