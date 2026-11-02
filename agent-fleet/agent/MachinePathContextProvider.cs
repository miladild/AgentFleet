using Microsoft.Agents.AI;

namespace AgentFleet;

/// <summary>
/// Adds the paragraph about the machine the tools act on: the hub's in ordinary chat, the worker's during a plan step on a worker.
/// The hub's paragraph ("the hub machine you run tools on is Windows. Always use Windows-style paths...") once reached a model on a
/// Linux worker, which then ran "> nul" and left a file by that name, so the worker's paragraph replaces it there rather than
/// following it.
/// </summary>
internal sealed class MachinePathContextProvider : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new AIContext { Instructions = Paragraph(WorkerWorkspaceContext.Current?.Platform) });

    internal static string Paragraph(string? workerPlatform) => workerPlatform switch
    {
        null => HubPlatform.PathInstructions,
        _ when string.Equals(workerPlatform, "windows", StringComparison.OrdinalIgnoreCase) =>
            "During this plan step your file and shell tools act on a Windows worker, in an isolated copy of the project, not on the hub. " +
            "Use project-relative paths (src\\app.js). run_command runs in Windows PowerShell 5.1; && and || chains use cmd.exe.",
        _ => "During this plan step your file and shell tools act on a Linux worker, in an isolated copy of the project, not on the hub. " +
             "Use project-relative paths with forward slashes (src/app.js). run_command runs in bash on Linux: use /dev/null to discard " +
             "output (there is no nul) and POSIX commands."
    };
}
