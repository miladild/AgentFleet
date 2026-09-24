using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// Direct, unsandboxed process execution on the hub's own machine - unlike
/// DockerSandboxExecutor, there is no container boundary here, and unlike the
/// Docker sandbox, output is not limited to a throwaway environment: a command run
/// here can affect any real file or state on this PC. Deliberately unrestricted (no
/// command allowlist, no approval gate) per the same explicit user choice already
/// made for the file tools and the sandbox's network access.
/// </summary>
internal static class HubShellTools
{
    public static Task<string> RunGitCommandAsync(
        string repositoryPath,
        string arguments,
        HubShellOptions options,
        ILogger logger,
        CancellationToken cancellationToken) =>
        ExecuteAsync("git", arguments, null, repositoryPath, options, logger, cancellationToken);

    // The hub's own shell: cmd.exe on Windows, /bin/sh elsewhere. The command string goes to
    // the shell as one argument, so pipes, redirects and && behave as the user would expect.
    public static Task<string> RunCommandAsync(
        string command,
        string? workingDirectory,
        HubShellOptions options,
        ILogger logger,
        CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows()
            ? ExecuteAsync("cmd.exe", $"/c {command}", null, workingDirectory, options, logger, cancellationToken)
            : ExecuteAsync("/bin/sh", null, ["-c", command], workingDirectory, options, logger, cancellationToken);

    private static async Task<string> ExecuteAsync(
        string fileName,
        string? arguments,
        IReadOnlyList<string>? argumentList,
        string? workingDirectory,
        HubShellOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (argumentList is not null)
        {
            foreach (string argument in argumentList)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            startInfo.Arguments = arguments ?? string.Empty;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return $"Error: failed to start '{fileName}': {exception.Message}";
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.ExecutionTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Command '{FileName} {Arguments}' exceeded {Timeout}s; killing it.",
                fileName, arguments ?? string.Join(' ', argumentList ?? []), options.ExecutionTimeout.TotalSeconds);
            TryKill(process);
            return $"Error: command exceeded the {options.ExecutionTimeout.TotalSeconds}s timeout and was killed.\n" +
                   $"--- partial stdout ---\n{stdout}\n--- partial stderr ---\n{stderr}";
        }

        string result = $"Exit code: {process.ExitCode}\n--- stdout ---\n{stdout}";
        if (stderr.Length > 0)
        {
            result += $"\n--- stderr ---\n{stderr}";
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best-effort - the process may have already exited between the timeout
            // firing and this call.
        }
    }
}
