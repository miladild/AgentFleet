using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AgentFleet;

internal sealed record SandboxExecutionResult(
    bool Succeeded,
    string Output,
    int? ExitCode,
    bool TimedOut,
    string? Error);

// Runs untrusted, model-generated code in a throwaway Docker container, either on this
// machine or on another one reached over SSH (see DockerSandboxOptions). The container is
// the safety boundary (non-root, read-only root filesystem, capabilities dropped,
// resource-limited, ephemeral), not an approval gate - by design, per the owner's explicit
// choice.
internal sealed class DockerSandboxExecutor
{
    private static readonly IReadOnlyDictionary<string, (string Image, string Interpreter)> Runtimes =
        new Dictionary<string, (string Image, string Interpreter)>(StringComparer.OrdinalIgnoreCase)
        {
            ["python"] = ("python:3.12-slim", "python3 -"),
            ["bash"] = ("python:3.12-slim", "bash -s"),
            ["javascript"] = ("node:22-slim", "node -"),
            ["node"] = ("node:22-slim", "node -"),
        };

    private readonly DockerSandboxOptions _options;
    private readonly ILogger _logger;

    public DockerSandboxExecutor(DockerSandboxOptions options, ILogger logger)
    {
        if (options.Mode == SandboxMode.Off)
        {
            throw new ArgumentException("A sandbox that is switched off has no executor.", nameof(options));
        }

        _options = options;
        _logger = logger;
    }

    public async Task<SandboxExecutionResult> ExecuteAsync(
        string language,
        string code,
        CancellationToken cancellationToken)
    {
        if (!Runtimes.TryGetValue(language.Trim(), out (string Image, string Interpreter) runtime))
        {
            string supported = string.Join(", ", Runtimes.Keys.Distinct(StringComparer.OrdinalIgnoreCase));
            return new SandboxExecutionResult(false, string.Empty, null, false,
                $"Unsupported language '{language}'. Supported: {supported}.");
        }

        string containerName = $"fleet-sandbox-{Guid.NewGuid():N}";

        // Fixed argument list: user-provided code is never interpolated into this
        // command line, only piped to the interpreter's stdin. That, not escaping, is
        // what rules out shell/command injection here - the shell (local or remote) only
        // ever sees this exact literal string, regardless of code content.
        List<string> dockerArguments =
        [
            "run", "--rm", "-i",
            "--name", containerName,
            "--network", "bridge",
            "--memory=512m",
            "--cpus=1",
            "--pids-limit=128",
            "--cap-drop=ALL",
            "--security-opt=no-new-privileges",
            "--read-only",
            "--tmpfs", "/tmp:rw,size=64m",
            "--user", "1000:1000",
            "--workdir", "/tmp",
            runtime.Image,
            .. runtime.Interpreter.Split(' ')
        ];

        return _options.Mode == SandboxMode.Local
            ? await ExecuteLocalAsync(dockerArguments, containerName, code, cancellationToken)
            : await ExecuteOverSshAsync(dockerArguments, containerName, code, cancellationToken);
    }

    private async Task<SandboxExecutionResult> ExecuteLocalAsync(
        List<string> dockerArguments,
        string containerName,
        string code,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in dockerArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return new SandboxExecutionResult(false, string.Empty, null, false,
                $"Could not start docker: {exception.Message}. Is Docker installed and running on the hub?");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.ExecutionTimeout);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.StandardInput.WriteAsync(code);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The container can exit before it reads everything; its output says why.
        }

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Sandbox execution {Container} exceeded {Timeout}s; killing it.",
                containerName, _options.ExecutionTimeout.TotalSeconds);
            TryKill(process);
            await KillLocalContainerAsync(containerName);
            return new SandboxExecutionResult(false, string.Empty, null, true,
                $"Execution exceeded the {_options.ExecutionTimeout.TotalSeconds}s sandbox timeout and was killed.");
        }

        string output = await stdoutTask;
        string error = await stderrTask;
        return new SandboxExecutionResult(
            Succeeded: process.ExitCode == 0,
            Output: string.IsNullOrEmpty(error) ? output : $"{output}\n--- stderr ---\n{error}",
            ExitCode: process.ExitCode,
            TimedOut: false,
            Error: null);
    }

    private async Task<SandboxExecutionResult> ExecuteOverSshAsync(
        List<string> dockerArguments,
        string containerName,
        string code,
        CancellationToken cancellationToken)
    {
        string dockerCommand = (_options.UseSudo ? "sudo docker " : "docker ") + string.Join(' ', dockerArguments);

        using var sshClient = new SshClient(_options.Host, _options.Port, _options.User,
            new PrivateKeyFile(_options.KeyPath));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.ExecutionTimeout);

        try
        {
            await Task.Run(sshClient.Connect, cancellationToken);

            using SshCommand command = sshClient.CreateCommand(dockerCommand);
            command.CommandTimeout = _options.ExecutionTimeout;

            Task executeTask = command.ExecuteAsync(timeoutSource.Token);

            using (Stream inputStream = command.CreateInputStream())
            {
                byte[] codeBytes = Encoding.UTF8.GetBytes(code);
                await inputStream.WriteAsync(codeBytes, cancellationToken);
            }

            await executeTask;

            int exitCode = command.ExitStatus ?? -1;
            string output = command.Result;
            string error = command.Error;

            return new SandboxExecutionResult(
                Succeeded: exitCode == 0,
                Output: string.IsNullOrEmpty(error) ? output : $"{output}\n--- stderr ---\n{error}",
                ExitCode: exitCode,
                TimedOut: false,
                Error: null);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Sandbox execution {Container} exceeded {Timeout}s; killing it.",
                containerName, _options.ExecutionTimeout.TotalSeconds);
            await KillRemoteContainerAsync(containerName, cancellationToken);
            return new SandboxExecutionResult(false, string.Empty, null, true,
                $"Execution exceeded the {_options.ExecutionTimeout.TotalSeconds}s sandbox timeout and was killed.");
        }
        catch (Exception exception) when (exception is SshException or SocketException)
        {
            _logger.LogError(exception, "Sandbox execution {Container} failed to reach {Host}.",
                containerName, _options.Host);
            await KillRemoteContainerAsync(containerName, cancellationToken);
            return new SandboxExecutionResult(false, string.Empty, null, false,
                $"Sandbox execution failed: {exception.Message}");
        }
        finally
        {
            if (sshClient.IsConnected)
            {
                sshClient.Disconnect();
            }
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
            // Already gone.
        }
    }

    private async Task KillLocalContainerAsync(string containerName)
    {
        try
        {
            using Process? kill = Process.Start(new ProcessStartInfo("docker", ["kill", containerName])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (kill is not null)
            {
                await kill.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to force-kill sandbox container {Container}; it may still be running.",
                containerName);
        }
    }

    // Best-effort: the sandbox timeout above only stops us waiting, not the container itself,
    // since it runs on a separate machine over a channel we may have just abandoned. This is
    // a second, independent SSH connection specifically to make sure a runaway container
    // does not keep consuming that machine's resources.
    private async Task KillRemoteContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        try
        {
            using var killClient = new SshClient(_options.Host, _options.Port, _options.User,
                new PrivateKeyFile(_options.KeyPath));
            await Task.Run(killClient.Connect, cancellationToken);
            string sudo = _options.UseSudo ? "sudo " : string.Empty;
            using SshCommand killCommand = killClient.CreateCommand($"{sudo}docker kill {containerName}");
            killCommand.CommandTimeout = TimeSpan.FromSeconds(10);
            await killCommand.ExecuteAsync(CancellationToken.None);
            killClient.Disconnect();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Failed to force-kill sandbox container {Container}; it may still be running on {Host}.",
                containerName, _options.Host);
        }
    }
}
