using Microsoft.Extensions.Configuration;

namespace AgentFleet;

internal enum SandboxMode
{
    /// <summary>No sandbox tool is offered.</summary>
    Off,

    /// <summary>Docker on the hub itself.</summary>
    Local,

    /// <summary>Docker on another machine, reached over SSH.</summary>
    Ssh
}

/// <summary>
/// Where and how the sandbox tool runs. Nothing here assumes a particular network: with no
/// configuration at all the sandbox uses the hub's own Docker when it is installed and is
/// switched off otherwise. Values come from the "sandbox" section of fleet.config.json,
/// then the SANDBOX_* environment variables.
/// </summary>
internal sealed class DockerSandboxOptions
{
    private DockerSandboxOptions(
        SandboxMode mode,
        string summary,
        string host,
        int port,
        string user,
        string keyPath,
        bool useSudo,
        TimeSpan executionTimeout)
    {
        Mode = mode;
        Summary = summary;
        Host = host;
        Port = port;
        User = user;
        KeyPath = keyPath;
        UseSudo = useSudo;
        ExecutionTimeout = executionTimeout;
    }

    public SandboxMode Mode { get; }

    /// <summary>One line for the startup log: what was chosen and why.</summary>
    public string Summary { get; }

    public string Host { get; }

    public int Port { get; }

    public string User { get; }

    public string KeyPath { get; }

    public bool UseSudo { get; }

    public TimeSpan ExecutionTimeout { get; }

    /// <param name="dockerOnPath">Seam for tests; defaults to looking for a docker executable on PATH.</param>
    public static DockerSandboxOptions Resolve(
        FleetSandboxConfig? config,
        IConfiguration configuration,
        Func<bool>? dockerOnPath = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? requested = Clean(config?.Mode) ?? Clean(configuration["SANDBOX_MODE"]);
        requested = requested?.ToLowerInvariant();
        if (requested is not (null or "auto" or "off" or "local" or "ssh"))
        {
            throw new InvalidOperationException(
                $"Sandbox mode '{requested}' is not valid - use off, local, ssh or leave it out for auto.");
        }

        string? host = Clean(config?.Host) ?? Clean(configuration["SANDBOX_SSH_HOST"]);
        TimeSpan timeout = ResolveTimeout(config, configuration);

        SandboxMode mode;
        string reason;
        switch (requested)
        {
            case "off":
                return Off("sandbox is switched off in the configuration", timeout);
            case "local":
                mode = SandboxMode.Local;
                reason = "configured as local";
                break;
            case "ssh":
                mode = SandboxMode.Ssh;
                reason = "configured as ssh";
                break;
            default:
                if (host is not null)
                {
                    mode = SandboxMode.Ssh;
                    reason = "an ssh host is configured";
                }
                else if ((dockerOnPath ?? DockerIsOnPath)())
                {
                    mode = SandboxMode.Local;
                    reason = "Docker was found on this machine";
                }
                else
                {
                    return Off("no ssh host is configured and Docker was not found on this machine", timeout);
                }

                break;
        }

        if (mode == SandboxMode.Local)
        {
            return new DockerSandboxOptions(mode, $"local Docker ({reason})", string.Empty, 0, string.Empty, string.Empty, false, timeout);
        }

        if (host is null)
        {
            throw new InvalidOperationException(
                "The sandbox is set to ssh but has no host. Set sandbox.host in fleet.config.json (or SANDBOX_SSH_HOST).");
        }

        int port = config?.Port ?? ParsePort(configuration["SANDBOX_SSH_PORT"]);
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("The sandbox ssh port must be between 1 and 65535.");
        }

        string user = Clean(config?.User) ?? Clean(configuration["SANDBOX_SSH_USER"]) ?? Environment.UserName;

        string keyPath = Clean(config?.KeyPath) ?? Clean(configuration["SANDBOX_SSH_KEY_PATH"]) ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh",
            "id_ed25519");
        if (!File.Exists(keyPath))
        {
            throw new InvalidOperationException(
                $"The sandbox ssh key does not exist: {keyPath}. Set sandbox.keyPath in fleet.config.json (or SANDBOX_SSH_KEY_PATH).");
        }

        bool useSudo = config?.Sudo ?? bool.TryParse(configuration["SANDBOX_SSH_SUDO"], out bool parsed) && parsed;

        return new DockerSandboxOptions(
            mode, $"Docker over ssh to {user}@{host}:{port} ({reason})", host, port, user, keyPath, useSudo, timeout);
    }

    private static DockerSandboxOptions Off(string reason, TimeSpan timeout) =>
        new(SandboxMode.Off, $"off ({reason})", string.Empty, 0, string.Empty, string.Empty, false, timeout);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParsePort(string? value) =>
        value is null ? 22 : int.TryParse(value, out int port) ? port : 0;

    private static TimeSpan ResolveTimeout(FleetSandboxConfig? config, IConfiguration configuration)
    {
        int seconds = config?.TimeoutSeconds ??
            (int.TryParse(configuration["SANDBOX_EXECUTION_TIMEOUT_SECONDS"], out int fromEnvironment) ? fromEnvironment : 20);
        if (seconds is < 1 or > 120)
        {
            throw new InvalidOperationException("The sandbox execution timeout must be between 1 and 120 seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static bool DockerIsOnPath()
    {
        string[] names = OperatingSystem.IsWindows() ? ["docker.exe", "docker.cmd"] : ["docker"];
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim('"'), name)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not a reason to fail startup.
                }
            }
        }

        return false;
    }
}
