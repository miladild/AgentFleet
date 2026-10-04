using Microsoft.Extensions.AI;

namespace AgentFleet;

/// <summary>
/// During a plan step on a worker, the system prompt's paragraph about the machine is replaced by one about the worker. That
/// paragraph is written for the hub ("the hub machine you run tools on is Windows. Always use Windows-style paths..."), and a
/// model on a Linux worker was told exactly that before it ran "> nul" and left a file by that name. In ordinary chat, where
/// the tools act on the hub, nothing changes.
/// </summary>
internal sealed class WorkerPlatformChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, ForWorker(options, WorkerWorkspaceContext.Current?.Platform), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, ForWorker(options, WorkerWorkspaceContext.Current?.Platform), cancellationToken);

    /// <returns>The options themselves when there is nothing to change, else a copy with the worker's paragraph.</returns>
    internal static ChatOptions? ForWorker(ChatOptions? options, string? workerPlatform)
    {
        if (workerPlatform is null || options?.Instructions is not { Length: > 0 } instructions)
        {
            return options;
        }

        string hub = HubPlatform.PathInstructions;
        int at = instructions.IndexOf(hub, StringComparison.Ordinal);
        if (at < 0)
        {
            return options;
        }

        ChatOptions copy = options.Clone();
        copy.Instructions = string.Concat(instructions.AsSpan(0, at), Describe(workerPlatform), instructions.AsSpan(at + hub.Length));
        return copy;
    }

    internal static string Describe(string workerPlatform) =>
        string.Equals(workerPlatform, "windows", StringComparison.OrdinalIgnoreCase)
            ? "During this plan step your file and shell tools act on a Windows worker, in an isolated copy of the project, not on the hub. " +
              "Use project-relative paths (src\\app.js). run_command runs in Windows PowerShell 5.1; && and || chains use cmd.exe."
            : "During this plan step your file and shell tools act on a Linux worker, in an isolated copy of the project, not on the hub. " +
              "Use project-relative paths with forward slashes (src/app.js). run_command runs in bash on Linux: use /dev/null to discard " +
              "output (there is no nul) and POSIX commands.";
}
