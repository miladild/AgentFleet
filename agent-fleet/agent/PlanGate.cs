using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentFleet;

internal enum PlanPhase
{
    /// <summary>Plan mode is off: the agent acts directly, exactly as before.</summary>
    Off,

    /// <summary>Plan mode with no approved plan: the model can look but not change anything.</summary>
    Planning,

    /// <summary>The conversation's plan is approved and being carried out by the plan runner: chat is read-only.</summary>
    Executing
}

internal sealed record PlanGateResult(PlanPhase Phase, PlanRecord? Plan);

/// <summary>
/// Plan-first, enforced in code. In the planning phase the write and execute tools are not
/// even offered to the model, so no wording in a prompt can talk it into using them. The
/// phase is worked out from the conversation itself (plan tool results carry a
/// [plan:&lt;id&gt;] marker) and the plan's stored status, so it works the same from the web
/// UI and from VS Code and needs no per-conversation state on the server.
/// </summary>
internal static partial class PlanGate
{
    public static readonly IReadOnlySet<string> PlanToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "propose_plan", "get_plan"
    };

    // Always safe to offer while planning: they only read. MCP tools join this set when the
    // server marks them read-only.
    public static readonly IReadOnlySet<string> BuiltInReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file", "list_directory", "find_files", "search_files", "project_overview", "web_search", "web_fetch", "validate_diagram", "get_plan",
        // These two only touch the fleet's own record of the conversation: pinning a decision while
        // planning is exactly when it is most useful, and looking something up changes nothing.
        "record_decision", "search_context"
    };

    public static PlanGateResult Evaluate(bool planModeEnabled, IReadOnlyList<ChatMessage> messages, FleetPlanStore store)
    {
        if (!planModeEnabled)
        {
            return new PlanGateResult(PlanPhase.Off, null);
        }

        PlanRecord? plan = FindReferencedPlan(messages, store);
        if (plan is null)
        {
            return new PlanGateResult(PlanPhase.Planning, null);
        }

        // Typing the approval word in chat works everywhere, including VS Code where there is
        // no button. It is recognised here, in code - the model never gets to decide whether
        // it was approved.
        if (plan.Status is PlanStatus.AwaitingApproval or PlanStatus.Blocked &&
            IsApproval(LastUserText(messages)))
        {
            plan = store.Approve(plan.Id) ?? plan;
        }

        return plan.Status is PlanStatus.Approved or PlanStatus.Running
            ? new PlanGateResult(PlanPhase.Executing, plan)
            : new PlanGateResult(PlanPhase.Planning, plan);
    }

    /// <summary>
    /// A stand-in the router swaps in for a call the plan gate refuses. Offering fewer tools is
    /// not enough on its own: the agent's function-invocation layer holds the full tool list and
    /// runs any call by name, including ones the model was never offered (the instructions still
    /// mention them, and a leaked-call rescue rebuilds calls from text). So the refusal has to
    /// happen to the call itself.
    /// </summary>
    public const string BlockedToolName = "blocked_by_plan_mode";

    // While a plan is being written the model may read and may propose. While a plan is being carried
    // out (by the plan runner, in the background) a chat model may only read and check progress, so it
    // cannot run the same work a second time.
    public static bool IsAllowed(PlanPhase phase, string toolName, IReadOnlySet<string> readOnlyTools) =>
        phase switch
        {
            PlanPhase.Planning => readOnlyTools.Contains(toolName) || toolName == "propose_plan",
            PlanPhase.Executing => readOnlyTools.Contains(toolName) || toolName == "get_plan",
            _ => true
        };

    public static bool IsAllowedWhilePlanning(string toolName, IReadOnlySet<string> readOnlyTools) =>
        IsAllowed(PlanPhase.Planning, toolName, readOnlyTools);

    // Rewrites any call to a tool the planning phase does not allow into a call to the
    // blocked_by_plan_mode stand-in, which just tells the model why and what to do instead.
    public static ChatResponseUpdate GuardCalls(
        ChatResponseUpdate update,
        PlanGateResult gate,
        IReadOnlySet<string> readOnlyTools,
        Action<string>? onBlocked = null)
    {
        if (gate.Phase == PlanPhase.Off || !update.Contents.Any(content => content is FunctionCallContent))
        {
            return update;
        }

        bool changed = false;
        var guarded = new List<AIContent>(update.Contents.Count);
        foreach (AIContent content in update.Contents)
        {
            if (content is FunctionCallContent call &&
                call.Name != BlockedToolName &&
                !IsAllowed(gate.Phase, call.Name, readOnlyTools))
            {
                onBlocked?.Invoke(call.Name);
                guarded.Add(new FunctionCallContent(
                    call.CallId,
                    BlockedToolName,
                    new Dictionary<string, object?> { ["tool"] = call.Name }));
                changed = true;
            }
            else
            {
                guarded.Add(content);
            }
        }

        if (changed)
        {
            update.Contents = guarded;
        }

        return update;
    }

    public static string BlockedMessage(string tool) =>
        $"Blocked: {tool} is not available to you right now, and nothing was changed. While plan mode has no approved plan you can " +
        "only read and search: explore with read_file, list_directory, find_files and search_files (validate_diagram checks Mermaid syntax), then call propose_plan with the " +
        "complete plan and stop. Once a plan is approved the fleet's plan runner carries it out automatically in the background, so " +
        "do not make the changes yourself.";

    public static ChatOptions? FilterTools(ChatOptions? options, PlanPhase phase, IReadOnlySet<string> readOnlyTools)
    {
        if (options?.Tools is null or { Count: 0 })
        {
            return options;
        }

        Func<AITool, bool> keep = phase switch
        {
            // Not planning: the plan tools are just clutter for a model with many tools already.
            PlanPhase.Off => tool => !PlanToolNames.Contains(tool.Name) && tool.Name != BlockedToolName,
            _ => tool => tool.Name != BlockedToolName && IsAllowed(phase, tool.Name, readOnlyTools)
        };

        ChatOptions filtered = options.Clone();
        filtered.Tools = options.Tools.Where(keep).ToList();
        return filtered;
    }

    // Replaces the agent's normal instructions while planning instead of adding to them. The
    // normal ones tell the model to use write_file, edit_file and run_command, and it does what
    // the longer, earlier text says: in testing it wrote out a whole file into a write_file call
    // (minutes of generation on a large model) before being refused. So the planning phase never
    // shows it those instructions at all.
    /// <param name="machines">Which machines serve which tier, so the planner can spread independent steps over them.</param>
    public static string PlanningInstructions(PlanRecord? existing, string? machines = null) =>
        $"You are a careful software planner working on the user's own machine, which runs {HubPlatform.Name}: use " +
        "paths in that machine's own style exactly as given, never rewrite them into another style.\n\n" + PlanningDirective(existing, machines);

    public static string PlanningDirective(PlanRecord? existing, string? machines = null) =>
        """
        Plan mode is on and no plan has been approved yet. You can look around, but you cannot change anything:
        only read-only tools are available to you until the user approves a plan. Do not write files, run commands
        or edit code in this phase, not even as an example: describe the work in the plan instead.

        Work in this order:
        1. Explore. Use read_file, list_directory, find_files and search_files (and web_search or documentation
           tools when you need facts) to understand the code this task touches. Use validate_diagram to check Mermaid
           source if needed. Do not guess about files you have
           not read. If the user states a decision or constraint (a language, a library, a style, something not to
           touch), pin each one with record_decision so it reaches every step, whichever machine runs it.
        2. If something important is unclear, ask the user one or two specific questions and stop. Do not assume.
        3. Otherwise call propose_plan once with the complete plan: a goal, the assumptions you are making, the
           risks, and small steps. Each step does one thing, names the files it touches, and has a verify command
           (build, test, lint) that fails if the step did not work. A good verify command exercises the step's
           behavior (call the function and check the result, run the tests, build the project) rather than only
           loading the file, and it must finish on its own: never a server, a watcher or anything that waits.
           Later steps build on earlier ones, so a weak check early on hides bugs until a later step fails.
           The fleet runs each verify command itself, exactly as written, in workingDirectory: it must be a real
           command (for example node --test test/slug.test.js, npm test, dotnet test), never a sentence. Make every
           step able to pass its own check when it is done: write a piece of code and its test in the same step, and
           never check a step with a file that a later step creates. Name every file a step creates in its files.
           Give each step a tier: heavy for hard or risky
           work, standard for ordinary work, light for trivial edits. Set workingDirectory to the project folder
           (its full path).
           Leave parallelGroup empty by default. Only give the same parallelGroup label to consecutive steps when
           they are independent, name disjoint files, and can safely be edited at the same time. Parallel groups
           must use different tiers, so different machines do them at the same time; the runner also checks that
           those routes are configured and healthy, and will run the steps in order if any safety check fails.
           Group checks run after all group edits finish. When the work splits into independent parts (two separate
           helpers, a feature and an unrelated fix), make them a parallel group on different tiers so the plan is
           spread over the machines instead of queuing on one.
           Add a Mermaid diagram only when the change affects how several parts fit together (a flowchart,
           sequenceDiagram or classDiagram, with every node label in double quotes); skip it for small tasks.
           Keep the plan compact: at most eight steps, one or two sentences of detail each. Send each list as a
           real JSON array (steps as an array of objects), not as text.
        4. When propose_plan returns, stop. Tell the user in two or three sentences that the plan is ready to review
           and approve. Do not start the work. If it says the plan was NOT saved, fix what it names and call
           propose_plan again with the whole plan.
        """ + (string.IsNullOrWhiteSpace(machines)
            ? string.Empty
            : $"\n\nThe machines that will carry the plan out, by tier: {machines}.") + (existing is null
            ? string.Empty
            : $"\n\nThe plan {FleetPlanStore.Marker(existing.Id)} is currently {existing.Status.Replace('-', ' ')}. " +
              "If the user asks for changes to it, revise it by calling propose_plan again with the full updated plan.");

    // Once a plan is approved the plan runner carries it out in the background, one verified step or
    // approved parallel group at a time. The chat model's job is only to say so and to report progress: letting it also do the work
    // would run every step twice, and measured behaviour shows a small model does not follow a
    // step-reporting protocol reliably anyway.
    public static string MonitorDirective(PlanRecord plan) =>
        $"""
        Plan {FleetPlanStore.Marker(plan.Id)} "{plan.Title}" has been approved by the user, and the fleet is now carrying it out
        automatically in the background, checking steps or explicitly approved parallel groups before moving on. You do not do the work.

        - Tell the user briefly that the plan is running, or report where it stands. Call get_plan for the current state and
          answer from that; never guess.
        - You can read files to answer questions about the code, but you cannot change anything.
        - If the user wants a change to the plan, tell them to stop it from the plan card (or the Plans panel) and then describe
          the change so a revised plan can be proposed.

        Where the plan stands right now:

        {FleetPlanStore.ToMarkdown(plan)}
        """;

    public static bool IsApproval(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ApprovalPattern().IsMatch(text);

    private static PlanRecord? FindReferencedPlan(IReadOnlyList<ChatMessage> messages, FleetPlanStore store)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            foreach (AIContent content in messages[i].Contents)
            {
                string? text = content switch
                {
                    FunctionResultContent result => result.Result?.ToString(),
                    TextContent textContent => textContent.Text,
                    _ => null
                };

                string? id = FleetPlanStore.FindMarkerId(text);
                if (id is not null && store.Get(id) is { } plan)
                {
                    return plan;
                }
            }
        }

        return null;
    }

    private static string? LastUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                return messages[i].Text;
            }
        }

        return null;
    }

    // The whole message must be an approval, so "approve the idea but change step 3" is
    // treated as feedback, not as a go-ahead.
    [GeneratedRegex(@"^\s*(?:\[plan:[0-9a-f]{32}\]\s*)?(?:approve|approved|confirm|confirmed|go ahead|proceed|yes,? proceed|looks good,? go ahead)[\s.!]*(?:\[plan:[0-9a-f]{32}\]\s*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ApprovalPattern();
}
