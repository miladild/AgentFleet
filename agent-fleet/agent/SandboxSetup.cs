using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AgentFleet;

/// <summary>
/// The sandbox the tool uses right now. Saving new sandbox settings in the web UI swaps it, so they apply
/// to the next call without a restart; a call already running keeps the executor it started with.
/// </summary>
internal sealed class SandboxHolder
{
    private readonly ILogger _logger;
    private volatile Current _current;

    public SandboxHolder(DockerSandboxOptions options, ILogger logger)
    {
        _logger = logger;
        _current = Build(options);
        _logger.LogInformation("Sandbox: {Summary}.", options.Summary);
    }

    public DockerSandboxOptions Options => _current.Options;

    /// <summary>Null while the sandbox is switched off; the tool is then not offered.</summary>
    public DockerSandboxExecutor? Executor => _current.Executor;

    public void Apply(DockerSandboxOptions options)
    {
        _current = Build(options);
        _logger.LogInformation("Sandbox changed from the web UI: {Summary}.", options.Summary);
    }

    private Current Build(DockerSandboxOptions options) =>
        new(options, options.Mode == SandboxMode.Off ? null : new DockerSandboxExecutor(options, _logger));

    private sealed record Current(DockerSandboxOptions Options, DockerSandboxExecutor? Executor);
}

/// <summary>SSH connections to the sandbox machine, with its identity checked when one has been remembered.</summary>
internal static class SandboxSsh
{
    /// <summary>"SHA256:..." the way ssh-keygen -l prints it.</summary>
    public static string Fingerprint(HostKeyEventArgs e) => $"SHA256:{e.FingerPrintSHA256}";

    public static SshClient CreateClient(DockerSandboxOptions options, Action<string>? seen = null)
    {
        var client = new SshClient(options.Host, options.Port, options.User, new PrivateKeyFile(options.KeyPath));
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        Guard(client, options.HostKey, seen);
        return client;
    }

    /// <summary>
    /// Refuses a machine whose host key differs from the remembered one (someone else answering at that
    /// address). With nothing remembered yet, any key is accepted, as before, and reported to <paramref name="seen"/>.
    /// </summary>
    public static void Guard(BaseClient client, string? expected, Action<string>? seen)
    {
        client.HostKeyReceived += (_, e) =>
        {
            string fingerprint = Fingerprint(e);
            seen?.Invoke(fingerprint);
            e.CanTrust = string.IsNullOrWhiteSpace(expected) || string.Equals(fingerprint, expected.Trim(), StringComparison.Ordinal);
        };
    }

    public const string HostKeyChanged =
        "The sandbox machine's identity (its SSH host key) is not the one the fleet remembered, so it refused to connect. " +
        "If that machine was reinstalled, open Config, Sandbox, press Test and then Save to remember its new key. If not, something else is answering at that address.";
}

internal sealed record SshKeyInfo(string Path, bool Exists, string? PublicKey, string? Problem = null);

internal sealed record SandboxProbe(bool Ok, string Detail, string? Hint);

internal sealed record SandboxTestStep(string Name, bool Ok, string Detail, string? Hint = null);

internal sealed record SandboxTestResult(bool Ok, IReadOnlyList<SandboxTestStep> Steps, string? HostKey, IReadOnlyList<string> MissingImages);

internal sealed record SandboxPrepareStatus(string State, string Step, string? Output, string? Error, DateTimeOffset StartedAtUtc);

internal sealed record KeyInstallResult(bool Ok, string Message, string? HostKey);

/// <summary>
/// Helps set up the code sandbox from the web UI: make an SSH key for the fleet, put its public half on the
/// sandbox machine once with that machine's password, test the connection and Docker, and download the
/// container images before the first real call needs them.
/// </summary>
internal static partial class SandboxSetup
{
    public static readonly string[] Images = ["python:3.12-slim", "node:22-slim"];

    private static readonly object PrepareGate = new();
    private static SandboxPrepareStatus? _prepare;

    public static SandboxPrepareStatus? PrepareStatus => _prepare;

    public static string DefaultKeyPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "agent-fleet_rsa");

    [GeneratedRegex(@"^ssh-(rsa|ed25519) [A-Za-z0-9+/=]+( [A-Za-z0-9._@-]{1,100})?$")]
    private static partial Regex PublicKeyPattern();

    public static SshKeyInfo ReadKey(string? path)
    {
        string keyPath = string.IsNullOrWhiteSpace(path) ? DefaultKeyPath() : path.Trim();
        if (!File.Exists(keyPath))
        {
            return new SshKeyInfo(keyPath, false, null);
        }

        string publicPath = keyPath + ".pub";
        string? publicKey = File.Exists(publicPath) ? File.ReadAllText(publicPath).Trim() : null;
        return publicKey is not null && PublicKeyPattern().IsMatch(publicKey)
            ? new SshKeyInfo(keyPath, true, publicKey)
            : new SshKeyInfo(keyPath, true, null, $"There is a key at {keyPath}, but no readable {Path.GetFileName(publicPath)} next to it, so the fleet cannot show or install its public half.");
    }

    /// <summary>Makes a new key pair for the fleet. Never replaces a file that is already there.</summary>
    public static SshKeyInfo CreateKey(string? path)
    {
        string keyPath = string.IsNullOrWhiteSpace(path) ? DefaultKeyPath() : path.Trim();
        if (!Path.IsPathFullyQualified(keyPath))
        {
            throw new ArgumentException("Give the key a full path, for example " + DefaultKeyPath());
        }

        if (File.Exists(keyPath) || File.Exists(keyPath + ".pub"))
        {
            return ReadKey(keyPath);
        }

        string directory = Path.GetDirectoryName(keyPath)!;
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        using RSA rsa = RSA.Create(3072);
        string privatePem = rsa.ExportRSAPrivateKeyPem();
        WritePrivate(keyPath, privatePem + "\n");

        RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);
        using var blob = new MemoryStream();
        WriteSshString(blob, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteSshString(blob, Mpint(parameters.Exponent!));
        WriteSshString(blob, Mpint(parameters.Modulus!));
        string host = Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9.-]", "-");
        string publicKey = $"ssh-rsa {Convert.ToBase64String(blob.ToArray())} agent-fleet@{host}";
        File.WriteAllText(keyPath + ".pub", publicKey + "\n");
        return new SshKeyInfo(keyPath, true, publicKey);
    }

    // Only the account the backend runs as may read the private key.
    private static void WritePrivate(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        stream.Write(Encoding.ASCII.GetBytes(content));
    }

    private static void WriteSshString(Stream stream, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(data);
    }

    // SSH's mpint: big-endian, no leading zeros, and a zero byte in front when the top bit is set.
    private static byte[] Mpint(byte[] value)
    {
        int start = 0;
        while (start < value.Length - 1 && value[start] == 0)
        {
            start++;
        }

        byte[] trimmed = value[start..];
        return (trimmed[0] & 0x80) != 0 ? [0, .. trimmed] : trimmed;
    }

    /// <summary>
    /// Signs in with the machine's password once and adds the fleet's public key to that account's
    /// authorized_keys, then checks the key works. The password is used for this one connection and is
    /// never stored or logged.
    /// </summary>
    public static async Task<KeyInstallResult> InstallKeyAsync(
        string host, int port, string user, string password, string keyPath, string? expectedHostKey, CancellationToken cancellationToken)
    {
        SshKeyInfo key = ReadKey(keyPath);
        if (key.PublicKey is null)
        {
            return new KeyInstallResult(false, key.Problem ?? $"There is no key at {key.Path} yet. Create one first.", null);
        }

        var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (AuthenticationPrompt prompt in e.Prompts)
            {
                prompt.Response = password;
            }
        };
        var connection = new Renci.SshNet.ConnectionInfo(host, port, user, new PasswordAuthenticationMethod(user, password), keyboard)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        string? seen = null;
        using var client = new SshClient(connection);
        SandboxSsh.Guard(client, expectedHostKey, fingerprint => seen = fingerprint);
        try
        {
            await client.ConnectAsync(cancellationToken);
            using SshCommand uname = client.CreateCommand("uname -s");
            uname.CommandTimeout = TimeSpan.FromSeconds(15);
            await uname.ExecuteAsync(cancellationToken);
            if (uname.ExitStatus != 0 || uname.Result.Trim() is not ("Linux" or "Darwin" or "FreeBSD"))
            {
                return new KeyInstallResult(false,
                    "That machine does not look like Linux or macOS, so the fleet will not change its settings. " +
                    "On Windows, add the public key below to C:\\ProgramData\\ssh\\administrators_authorized_keys (administrators) or %USERPROFILE%\\.ssh\\authorized_keys by hand.", seen);
            }

            // The key is checked against a strict pattern above, so it holds no quote or shell character.
            string line = key.PublicKey;
            string script =
                "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
                $"(grep -qxF '{line}' ~/.ssh/authorized_keys || printf '%s\\n' '{line}' >> ~/.ssh/authorized_keys) && " +
                "chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys";
            using SshCommand install = client.CreateCommand(script);
            install.CommandTimeout = TimeSpan.FromSeconds(15);
            await install.ExecuteAsync(cancellationToken);
            if (install.ExitStatus != 0)
            {
                return new KeyInstallResult(false, $"Adding the key failed: {FirstLine(install.Error)}", seen);
            }
        }
        catch (SshConnectionException) when (expectedHostKey is not null && seen is not null && seen != expectedHostKey)
        {
            return new KeyInstallResult(false, SandboxSsh.HostKeyChanged, seen);
        }
        catch (SshAuthenticationException)
        {
            return new KeyInstallResult(false, $"{host} did not accept that password for {user}. Some machines only allow keys; then add the public key by hand.", seen);
        }
        catch (Exception exception) when (exception is SshException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return new KeyInstallResult(false, ConnectProblem(host, port, exception), seen);
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }

        return new KeyInstallResult(true, $"The fleet's key is now on {host} for {user}. Press Test to check Docker there.", seen);
    }

    public static async Task<SandboxProbe> ProbeLocalDockerAsync(CancellationToken cancellationToken)
    {
        QuickProcess.Result? version = await QuickProcess.RunAsync("docker", ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(10), cancellationToken);
        if (version is null)
        {
            return new SandboxProbe(false, "Docker is not installed on this computer.", InstallDockerHint(remote: false));
        }

        if (version.TimedOut)
        {
            return new SandboxProbe(false, "Docker did not answer within 10 seconds.", "If Docker Desktop is starting, wait until it says it is running and check again.");
        }

        if (version.ExitCode != 0 || version.Output.Length == 0)
        {
            (string detail, string? hint) = ExplainDocker(version.Error.Length > 0 ? version.Error : version.Output, remote: false, Environment.UserName);
            return new SandboxProbe(false, detail, hint);
        }

        return new SandboxProbe(true, version.Output, null);
    }

    public static async Task<SandboxTestResult> TestAsync(DockerSandboxOptions options, CancellationToken cancellationToken)
    {
        var steps = new List<SandboxTestStep>();
        var missing = new List<string>();
        if (options.Mode == SandboxMode.Off)
        {
            return new SandboxTestResult(true, [new SandboxTestStep("Sandbox", true, "Switched off: there is nothing to test.")], null, missing);
        }

        if (options.Mode == SandboxMode.Local)
        {
            SandboxProbe probe = await ProbeLocalDockerAsync(cancellationToken);
            steps.Add(new SandboxTestStep("Docker on this computer", probe.Ok, probe.Ok ? $"Docker {probe.Detail} is running." : probe.Detail, probe.Hint));
            if (probe.Ok)
            {
                foreach (string image in Images)
                {
                    QuickProcess.Result? inspect = await QuickProcess.RunAsync("docker", ["image", "inspect", image, "--format", "{{.Id}}"], TimeSpan.FromSeconds(10), cancellationToken);
                    if (inspect is not { ExitCode: 0 })
                    {
                        missing.Add(image);
                    }
                }
            }

            return new SandboxTestResult(steps.All(step => step.Ok), steps, null, missing);
        }

        string? seen = null;
        SshClient client;
        try
        {
            client = SandboxSsh.CreateClient(options, fingerprint => seen = fingerprint);
        }
        catch (Exception exception) when (exception is SshException or IOException or UnauthorizedAccessException)
        {
            steps.Add(new SandboxTestStep("Key", false, $"The key at {options.KeyPath} could not be read: {exception.Message}"));
            return new SandboxTestResult(false, steps, null, missing);
        }

        using (client)
        {
            try
            {
                await client.ConnectAsync(cancellationToken);
                steps.Add(new SandboxTestStep("Connect", true, $"Signed in to {options.Host} as {options.User} with the key."));
            }
            catch (SshConnectionException) when (options.HostKey is not null && seen is not null && seen != options.HostKey)
            {
                steps.Add(new SandboxTestStep("Connect", false, SandboxSsh.HostKeyChanged));
                return new SandboxTestResult(false, steps, seen, missing);
            }
            catch (SshAuthenticationException)
            {
                steps.Add(new SandboxTestStep("Connect", false, $"{options.Host} did not accept the fleet's key for {options.User}.",
                    "Install the key on that machine (the button below does it with the machine's password once), or check the user name."));
                return new SandboxTestResult(false, steps, seen, missing);
            }
            catch (Exception exception) when (exception is SshException or System.Net.Sockets.SocketException or TimeoutException)
            {
                steps.Add(new SandboxTestStep("Connect", false, ConnectProblem(options.Host, options.Port, exception),
                    "Check the address, that the machine is on, and that its SSH server runs (Linux: sudo systemctl status ssh)."));
                return new SandboxTestResult(false, steps, seen, missing);
            }

            string docker = options.UseSudo ? "sudo -n docker" : "docker";
            (int exit, string output, string error) = await RunRemoteAsync(client, $"{docker} version --format {{{{.Server.Version}}}}", TimeSpan.FromSeconds(20), cancellationToken);
            if (exit != 0 || output.Length == 0)
            {
                (string detail, string? hint) = ExplainDocker(error.Length > 0 ? error : output, remote: true, options.User);
                steps.Add(new SandboxTestStep("Docker", false, detail, hint));
            }
            else
            {
                steps.Add(new SandboxTestStep("Docker", true, $"Docker {output} is running on {options.Host}."));
                foreach (string image in Images)
                {
                    (int found, _, _) = await RunRemoteAsync(client, $"{docker} image inspect {image} --format {{{{.Id}}}}", TimeSpan.FromSeconds(20), cancellationToken);
                    if (found != 0)
                    {
                        missing.Add(image);
                    }
                }
            }

            client.Disconnect();
        }

        return new SandboxTestResult(steps.All(step => step.Ok), steps, seen, missing);
    }

    /// <summary>
    /// Downloads the sandbox images in the background and then runs a real snippet, so the first call a
    /// model makes does not spend its whole timeout waiting for a download.
    /// </summary>
    public static bool StartPrepare(DockerSandboxOptions options, ILogger logger)
    {
        lock (PrepareGate)
        {
            if (_prepare?.State == ModelPullService.Running)
            {
                return false;
            }

            _prepare = new SandboxPrepareStatus(ModelPullService.Running, "starting", null, null, DateTimeOffset.UtcNow);
        }

        _ = Task.Run(async () =>
        {
            DateTimeOffset started = _prepare!.StartedAtUtc;
            void Report(string step) => _prepare = new SandboxPrepareStatus(ModelPullService.Running, step, null, null, started);
            try
            {
                foreach (string image in Images)
                {
                    Report($"downloading {image}");
                    string? problem = await PullAsync(options, image);
                    if (problem is not null)
                    {
                        _prepare = new SandboxPrepareStatus(ModelPullService.Failed, $"downloading {image}", null, problem, started);
                        return;
                    }
                }

                Report("running a test snippet");
                var executor = new DockerSandboxExecutor(options, logger);
                SandboxExecutionResult result = await executor.ExecuteAsync("python", "print('hello from the sandbox')", CancellationToken.None);
                _prepare = result.Succeeded && result.Output.Contains("hello from the sandbox", StringComparison.Ordinal)
                    ? new SandboxPrepareStatus(ModelPullService.Done, "done", result.Output.Trim(), null, started)
                    : new SandboxPrepareStatus(ModelPullService.Failed, "running a test snippet", result.Output, result.Error ?? $"Exit code {result.ExitCode}.", started);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Preparing the sandbox failed.");
                string problem = exception is SshConnectionException && exception.Message.Contains("Host key", StringComparison.OrdinalIgnoreCase)
                    ? SandboxSsh.HostKeyChanged
                    : exception.Message;
                _prepare = new SandboxPrepareStatus(ModelPullService.Failed, _prepare?.Step ?? "starting", null, problem, started);
            }
        });
        return true;
    }

    private static async Task<string?> PullAsync(DockerSandboxOptions options, string image)
    {
        TimeSpan timeout = TimeSpan.FromMinutes(15);
        if (options.Mode == SandboxMode.Local)
        {
            QuickProcess.Result? pull = await QuickProcess.RunAsync("docker", ["pull", image], timeout);
            return pull switch
            {
                null => "Docker is not installed on this computer.",
                { TimedOut: true } => $"Downloading {image} took longer than 15 minutes.",
                { ExitCode: not 0 } => FirstLine(pull.Error.Length > 0 ? pull.Error : pull.Output),
                _ => null
            };
        }

        using SshClient client = SandboxSsh.CreateClient(options);
        await client.ConnectAsync(CancellationToken.None);
        (int exit, string output, string error) = await RunRemoteAsync(client, $"{(options.UseSudo ? "sudo -n docker" : "docker")} pull {image}", timeout, CancellationToken.None);
        client.Disconnect();
        return exit == 0 ? null : FirstLine(error.Length > 0 ? error : output);
    }

    private static async Task<(int Exit, string Output, string Error)> RunRemoteAsync(SshClient client, string commandText, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using SshCommand command = client.CreateCommand(commandText);
        command.CommandTimeout = timeout;
        try
        {
            await command.ExecuteAsync(cancellationToken);
        }
        catch (SshOperationTimeoutException)
        {
            return (-1, string.Empty, $"'{commandText}' did not finish within {timeout.TotalSeconds:0} seconds.");
        }

        return (command.ExitStatus ?? -1, command.Result.Trim(), command.Error.Trim());
    }

    internal static (string Detail, string? Hint) ExplainDocker(string error, bool remote, string user)
    {
        string text = error.Trim();
        string where = remote ? "that machine" : "this computer";
        if (text.Contains("a password is required", StringComparison.OrdinalIgnoreCase) || text.StartsWith("sudo:", StringComparison.OrdinalIgnoreCase))
        {
            return ("sudo asked for a password, which the fleet cannot type.",
                $"On {where}, add {user} to the docker group (sudo usermod -aG docker {user}) and untick Use sudo, or allow {user} to run docker with sudo without a password.");
        }

        if (text.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return ($"The account {user} is not allowed to use Docker on {where}.",
                $"On {where}: sudo usermod -aG docker {user}, then sign out and back in{(remote ? string.Empty : " and restart the fleet")}. Or tick Use sudo if {user} may run sudo without a password.");
        }

        if (text.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error during connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("docker daemon is not running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot find the file specified", StringComparison.OrdinalIgnoreCase))
        {
            return ($"Docker is installed on {where} but not running.",
                remote || OperatingSystem.IsLinux()
                    ? $"Start it on {where}: sudo systemctl start docker (and sudo systemctl enable docker to start it at boot)."
                    : "Start Docker Desktop and wait until it says Docker is running, then test again.");
        }

        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) || text.Contains("not recognized", StringComparison.OrdinalIgnoreCase))
        {
            return ($"Docker is not installed on {where}.", InstallDockerHint(remote));
        }

        return (text.Length == 0 ? "Docker did not answer." : FirstLine(text), null);
    }

    private static string InstallDockerHint(bool remote) =>
        remote || OperatingSystem.IsLinux()
            ? "Install Docker Engine (https://docs.docker.com/engine/install/, or: curl -fsSL https://get.docker.com | sh), then add the account to the docker group."
            : "Install Docker Desktop from https://www.docker.com/products/docker-desktop/ and start it. Or run the sandbox on another machine over SSH.";

    private static string ConnectProblem(string host, int port, Exception exception) => exception switch
    {
        System.Net.Sockets.SocketException socket when socket.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound =>
            $"There is no machine called {host} on the network.",
        System.Net.Sockets.SocketException socket when socket.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused =>
            $"{host} refused the connection on port {port}: no SSH server is listening there.",
        SshOperationTimeoutException or TimeoutException or System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut } =>
            $"{host} did not answer on port {port} within 15 seconds.",
        _ => $"Could not connect to {host}:{port}: {exception.Message}"
    };

    private static string FirstLine(string text)
    {
        string line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 400 ? line[..400] + "..." : line;
    }
}
