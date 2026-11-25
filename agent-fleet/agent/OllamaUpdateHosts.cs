using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AgentFleet;

/// <summary>Why an update did not happen, in words for the person who clicked. <see cref="Status"/> is the HTTP answer a request gets.</summary>
internal sealed class OllamaUpdateException(string message, int status = StatusCodes.Status409Conflict) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Asks one Ollama server which version it runs. Null when it does not answer.</summary>
internal interface IOllamaVersionProbe
{
    Task<string?> GetVersionAsync(Uri root, CancellationToken cancellationToken);
}

internal sealed class HttpOllamaVersionProbe(IHttpClientFactory clients) : IOllamaVersionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<string?> GetVersionAsync(Uri root, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            HttpClient client = clients.CreateClient(FleetHealthMonitor.HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(OllamaAddress.Endpoint(root, "api/version"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await OllamaUpdateService.ReadLimitedAsync(response.Content, 64 * 1024, timeout.Token);
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }
}

/// <summary>What the update of the machine this backend runs on needs from the operating system. Faked in tests.</summary>
internal interface IOllamaHostActions
{
    bool IsWindows { get; }

    /// <summary>True when the backend is a Windows service: its account is not the person's, so an installer would put Ollama in the wrong profile.</summary>
    bool IsWindowsService { get; }

    /// <summary>Where ollama.exe is installed, or null when it is not found.</summary>
    string? OllamaExecutable();

    /// <summary>Ends every process with one of these names, and its children.</summary>
    void StopProcesses(IReadOnlyList<string> names);

    /// <summary>Runs the installer and waits for it. Its exit code, or -1 when it did not finish in time (it is ended then).</summary>
    Task<int> RunInstallerAsync(string installerPath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Starts "ollama serve" without a window, and does not wait for it.</summary>
    void StartServeHidden(string executable);
}

internal sealed class WindowsOllamaHost : IOllamaHostActions
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsWindowsService => OperatingSystem.IsWindows() &&
        Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();

    public string? OllamaExecutable()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        return File.Exists(path) ? path : null;
    }

    public void StopProcesses(IReadOnlyList<string> names)
    {
        foreach (string name in names)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(10_000);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        // It ended by itself in the meantime.
                    }
                }
            }
        }
    }

    public async Task<int> RunInstallerAsync(string installerPath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The installer's output is not redirected: it relaunches Ollama's app, which must not be tied to a pipe this process owns.
        var start = new ProcessStartInfo(installerPath, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installerPath)! };
        using Process process = Process.Start(start) ?? throw new OllamaUpdateException("The installer could not be started.");
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // It finished at the last moment.
            }

            return -1;
        }
    }

    public void StartServeHidden(string executable)
    {
        var start = new ProcessStartInfo(executable, ["serve"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        Process.Start(start)?.Dispose();
    }
}

internal sealed record RemoteResult(int ExitCode, string Output, string Error);

/// <summary>One SSH sign-in that runs one command after another.</summary>
internal interface IRemoteShell : IDisposable
{
    Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IRemoteShellFactory
{
    Task<IRemoteShell> OpenAsync(FleetOllamaUpdateConfig config, CancellationToken cancellationToken);
}

/// <summary>
/// SSH with the update account's key, to a machine whose host key is pinned. This account is not the worker workspace
/// account: that one is checked to have no administrator rights, this one has narrow, named sudo rights.
/// </summary>
internal sealed class SshRemoteShellFactory : IRemoteShellFactory
{
    public async Task<IRemoteShell> OpenAsync(FleetOllamaUpdateConfig config, CancellationToken cancellationToken)
    {
        string keyPath = Environment.ExpandEnvironmentVariables((config.KeyPath ?? string.Empty).Trim());
        if (!Path.IsPathRooted(keyPath))
        {
            keyPath = Path.GetFullPath(keyPath);
        }

        if (!File.Exists(keyPath))
        {
            throw new OllamaUpdateException("The SSH key configured for updating this machine was not found on this computer.");
        }

        SshClient? client = null;
        try
        {
            client = new SshClient(config.Host!.Trim(), config.Port, config.User!.Trim(), new PrivateKeyFile(keyPath));
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            SandboxSsh.Guard(client, config.HostKey, null);
            await client.ConnectAsync(cancellationToken);
            return new Shell(client);
        }
        catch (Exception exception) when (exception is SshException or SocketException or IOException)
        {
            client?.Dispose();
            throw new OllamaUpdateException(
                $"Could not sign in over SSH to {config.Host}: {exception.Message} (check the address, the key, and that the pinned host key is the machine's).",
                StatusCodes.Status502BadGateway);
        }
    }

    private sealed class Shell(SshClient client) : IRemoteShell
    {
        public async Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using SshCommand running = client.CreateCommand(command);
            running.CommandTimeout = timeout;
            try
            {
                await running.ExecuteAsync(cancellationToken);
            }
            catch (SshOperationTimeoutException)
            {
                return new RemoteResult(-1, string.Empty, $"It did not finish within {timeout.TotalSeconds:0} seconds.");
            }

            return new RemoteResult(running.ExitStatus ?? -1, running.Result.Trim(), running.Error.Trim());
        }

        public void Dispose() => client.Dispose();
    }
}
