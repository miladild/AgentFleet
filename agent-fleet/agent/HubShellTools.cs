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
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    // How long a command that showed the stop line is let run on, so the rest of its output arrives, and how long ending its
    // process tree and reading what it printed may take. Ending a tree can be slow, so it has a limit of its own and is never waited on twice.
    private static readonly TimeSpan EarlyEndGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan EarlyEndLimit = TimeSpan.FromSeconds(5);

    public static Task<string> RunGitCommandAsync(
        string repositoryPath,
        string arguments,
        HubShellOptions options,
        ILogger logger,
        CancellationToken cancellationToken) =>
        ExecuteAsync("git", arguments, null, repositoryPath, options, logger, cancellationToken);

    // The hub's own shell: cmd.exe on Windows, /bin/sh elsewhere. The command string goes to
    // the shell as one argument, so pipes, redirects and && behave as the user would expect.
    //
    // stopWhenLine: when given and it says true for a line the command prints (on either stream), the command is let run on for a
    // moment and then its whole process tree is ended, and what it printed so far is returned (see EndedEarlyText). It is not the
    // runner's time limit. Without it the command runs as it always has.
    public static Task<string> RunCommandAsync(
        string command,
        string? workingDirectory,
        HubShellOptions options,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<string, bool>? stopWhenLine = null) =>
        OperatingSystem.IsWindows()
            ? ExecuteAsync("cmd.exe", $"/c {command}", null, workingDirectory, options, logger, cancellationToken, stopWhenLine)
            : ExecuteAsync("/bin/sh", null, ["-c", command], workingDirectory, options, logger, cancellationToken, stopWhenLine);

    private static async Task<string> ExecuteAsync(
        string fileName,
        string? arguments,
        IReadOnlyList<string>? argumentList,
        string? workingDirectory,
        HubShellOptions options,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<string, bool>? stopWhenLine = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Node, npm, git and dotnet write UTF-8; read with the console's code page, node's test marks came back
            // as "Γ£û" instead of "✖" and a model had to guess which tests failed.
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
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
        var stopLineSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Collect(StringBuilder into, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (into)
            {
                into.AppendLine(line);
            }

            if (stopWhenLine is not null && stopWhenLine(line))
            {
                stopLineSeen.TrySetResult();
            }
        }

        process.OutputDataReceived += (_, e) => Collect(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Collect(stderr, e.Data);

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

        bool endedEarly = false;
        try
        {
            Task exited = process.WaitForExitAsync(timeoutSource.Token);
            if (stopWhenLine is not null && await Task.WhenAny(exited, stopLineSeen.Task) == stopLineSeen.Task && !exited.IsCompleted)
            {
                // The command shows what it was watched for. It may still end by itself within the grace; if not, it is ended here.
                try
                {
                    await Task.WhenAny(exited, Task.Delay(EarlyEndGrace, timeoutSource.Token));
                }
                catch (OperationCanceledException)
                {
                    // The runner's own limit came first: the wait below reports it.
                }

                endedEarly = !exited.IsCompleted && !timeoutSource.IsCancellationRequested;
            }

            if (!endedEarly)
            {
                await exited;
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Command '{FileName} {Arguments}' exceeded {Timeout}s; killing it.",
                fileName, arguments ?? string.Join(' ', argumentList ?? []), options.ExecutionTimeout.TotalSeconds);
            TryKill(process);
            return $"Error: command exceeded the {options.ExecutionTimeout.TotalSeconds}s timeout and was killed.\n" +
                   $"--- partial stdout ---\n{stdout}\n--- partial stderr ---\n{stderr}";
        }

        if (endedEarly)
        {
            logger.LogInformation("Command '{FileName} {Arguments}' showed its stop line; ending it.",
                fileName, arguments ?? string.Join(' ', argumentList ?? []));
            await EndProcessTreeAsync(process);
            lock (stdout)
            {
                lock (stderr)
                {
                    return EndedEarlyText(stdout.ToString(), stderr.ToString());
                }
            }
        }

        string result = $"Exit code: {process.ExitCode}\n--- stdout ---\n{stdout}";
        if (stderr.Length > 0)
        {
            result += $"\n--- stderr ---\n{stderr}";
        }

        return result;
    }

    // What a command that was ended because it showed its stop line returns: its output so far, laid out like the text for a command
    // killed at the time limit but with a first line of its own, so it is never read as a time limit.
    internal static string EndedEarlyText(string stdout, string stderr) =>
        "Error: the command was ended early because it showed a stop line (see the output below).\n" +
        $"--- partial stdout ---\n{stdout}\n--- partial stderr ---\n{stderr}";

    // Ends the process tree and gives the output readers a moment to deliver what was already printed. Both are limited, and a tree
    // that is slow to end does not hold the result up.
    private static async Task EndProcessTreeAsync(Process process)
    {
        try
        {
            await Task.Run(() => TryKill(process)).WaitAsync(EarlyEndLimit);
            using var limit = new CancellationTokenSource(EarlyEndLimit);
            await process.WaitForExitAsync(limit.Token);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // The tree did not end in time: what was printed is returned anyway.
        }
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
