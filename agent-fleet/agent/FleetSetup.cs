using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AgentFleet;

/// <summary>This machine's network addresses, for "tell the other computers to let this one in".</summary>
internal static class HubNetwork
{
    /// <summary>Every unicast address of every interface that is up, loopback included.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address.ToString())
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// The private IPv4 addresses other machines on the home network would see this one as, the ones on an
    /// interface with a default gateway first (a VPN or virtual switch rarely has one).
    /// </summary>
    public static IReadOnlyList<string> PrivateIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => (Properties: nic.GetIPProperties(), Nic: nic))
                .SelectMany(entry => entry.Properties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address.Address))
                    .Select(address => (Address: address.Address.ToString(), HasGateway: entry.Properties.GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))))
                .OrderByDescending(entry => entry.HasGateway)
                // The worker scripts take home-network addresses; a VPN mesh address goes last.
                .ThenBy(entry => entry.Address.StartsWith("100.", StringComparison.Ordinal))
                .Select(entry => entry.Address)
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>RFC 1918 ranges, plus 100.64.0.0/10 which VPN meshes such as Tailscale use.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                                 (b[0] == 100 && b[1] is >= 64 and <= 127));
    }
}

/// <summary>Runs a small command and waits for it, for checks such as "is git installed".</summary>
internal static class QuickProcess
{
    public sealed record Result(int ExitCode, string Output, string Error, bool TimedOut);

    /// <returns>Null when the program could not be started at all (usually: it is not installed).</returns>
    public static async Task<Result?> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already gone.
                }

                return new Result(-1, string.Empty, string.Empty, TimedOut: true);
            }

            return new Result(process.ExitCode, (await output).Trim(), (await error).Trim(), TimedOut: false);
        }
    }

    /// <summary>The full path of a program on PATH, or null. On Windows .exe and .cmd are tried.</summary>
    public static string? FindOnPath(string name)
    {
        string[] candidates = OperatingSystem.IsWindows() ? [$"{name}.exe", $"{name}.cmd", $"{name}.bat"] : [name];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in candidates)
            {
                try
                {
                    string path = Path.Combine(directory.Trim('"'), candidate);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }
        }

        return null;
    }
}

internal sealed record HardwareInfo(
    string Os,
    string OsFamily,
    double RamGb,
    string? Gpu,
    double VramGb,
    bool UnifiedMemory,
    string ModelsFolder,
    double? FreeDiskGb)
{
    /// <summary>
    /// Memory a model can sit in at full speed: the graphics card's, or the part of a Mac's shared memory macOS
    /// lets the graphics side use (about two thirds).
    /// </summary>
    public double ModelMemoryGb => VramGb > 0 ? VramGb : UnifiedMemory ? Math.Round(RamGb * 0.66, 1) : 0;
}

/// <summary>What this computer can run: memory, graphics card and disk space, read once.</summary>
internal static class HardwareProbe
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string? Name, double Gb)? _gpu;

    public static async Task<HardwareInfo> ReadAsync(CancellationToken cancellationToken)
    {
        double ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024, 1);
        (string? gpuName, double vramGb) = await GpuAsync(cancellationToken);
        bool unified = OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64;
        string models = ModelsFolder();
        return new HardwareInfo(
            RuntimeInformation.OSDescription,
            OsFamily(),
            ramGb,
            unified && gpuName is null ? "Apple silicon (shared memory)" : gpuName,
            vramGb,
            unified,
            models,
            FreeDiskGb(models));
    }

    public static string OsFamily() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    // Where Ollama keeps models on this machine, for the free-space check.
    private static string ModelsFolder()
    {
        string? configured = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string mine = Path.Combine(home, ".ollama", "models");
        // The Linux installer runs Ollama as its own service account.
        return OperatingSystem.IsLinux() && !Directory.Exists(mine) && Directory.Exists("/usr/share/ollama/.ollama/models")
            ? "/usr/share/ollama/.ollama/models"
            : mine;
    }

    private static double? FreeDiskGb(string folder)
    {
        try
        {
            string? existing = folder;
            while (existing is not null && !Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
            }

            if (existing is null)
            {
                return null;
            }

            return Math.Round(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(existing))!).AvailableFreeSpace / 1024d / 1024 / 1024, 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<(string? Name, double Gb)> GpuAsync(CancellationToken cancellationToken)
    {
        if (_gpu is { } cached)
        {
            return cached;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_gpu is { } again)
            {
                return again;
            }

            (string? Name, double Gb) found = await NvidiaAsync(cancellationToken) ?? WindowsAdapter() ?? (null, 0);
            _gpu = found;
            return found;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<(string? Name, double Gb)?> NvidiaAsync(CancellationToken cancellationToken)
    {
        string? smi = QuickProcess.FindOnPath("nvidia-smi");
        if (smi is null && OperatingSystem.IsWindows())
        {
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            smi = File.Exists(system) ? system : null;
        }

        if (smi is null)
        {
            return null;
        }

        QuickProcess.Result? result = await QuickProcess.RunAsync(
            smi, ["--query-gpu=name,memory.total", "--format=csv,noheader,nounits"], TimeSpan.FromSeconds(5), cancellationToken);
        if (result is not { ExitCode: 0 })
        {
            return null;
        }

        // One line per card; a model can use the biggest one.
        (string Name, double Gb)? best = null;
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(',');
            if (parts.Length == 2 && double.TryParse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out double mib))
            {
                double gb = Math.Round(mib / 1024, 1);
                if (best is null || gb > best.Value.Gb)
                {
                    best = (parts[0].Trim(), gb);
                }
            }
        }

        return best;
    }

    // Other graphics cards on Windows: the display driver's registry entries hold the real memory size
    // (WMI's AdapterRAM stops at 4 GB).
    private static (string? Name, double Gb)? WindowsAdapter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using Microsoft.Win32.RegistryKey? display = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null)
            {
                return null;
            }

            (string? Name, double Gb)? best = null;
            foreach (string name in display.GetSubKeyNames().Where(name => name.All(char.IsDigit)))
            {
                using Microsoft.Win32.RegistryKey? adapter = display.OpenSubKey(name);
                if (adapter?.GetValue("HardwareInformation.qwMemorySize") is long bytes && bytes > 0)
                {
                    double gb = Math.Round(bytes / 1024d / 1024 / 1024, 1);
                    if (best is null || gb > best.Value.Gb)
                    {
                        best = (adapter.GetValue("DriverDesc") as string, gb);
                    }
                }
            }

            return best;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

internal sealed record ModelSuggestion(string Model, double? DownloadGb, string Why, bool Recommended);

/// <summary>Which models to suggest for which hardware. The same ladder the setup scripts use.</summary>
internal static class ModelAdvisor
{
    public const string DefaultTriageModel = "llama3.2:latest";

    // Approximate download sizes from the Ollama library, to show before someone starts a download.
    private static readonly Dictionary<string, double> DownloadSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen3-coder:30b"] = 19,
        ["qwen2.5-coder:32b"] = 20,
        ["qwen2.5-coder:14b"] = 9.0,
        ["qwen2.5-coder:7b"] = 4.7,
        ["qwen2.5-coder:latest"] = 4.7,
        ["qwen2.5-coder:3b"] = 1.9,
        ["qwen2.5-coder:1.5b"] = 1.0,
        ["llama3.2:latest"] = 2.0,
        ["llama3.2:3b"] = 2.0,
        ["llama3.2:1b"] = 1.3,
        ["qwen2.5:1.5b"] = 1.0,
        ["qwen2.5vl:7b"] = 6.0,
        ["llava:7b"] = 4.7,
    };

    public static double? DownloadGb(string model) =>
        DownloadSizes.TryGetValue(model.Contains(':') ? model : $"{model}:latest", out double gb) ? gb : null;

    public static IReadOnlyList<ModelSuggestion> ForCoding(HardwareInfo hardware)
    {
        double gpu = hardware.ModelMemoryGb;
        string memory = hardware.VramGb > 0
            ? $"{hardware.VramGb:0.#} GB of graphics memory"
            : hardware.UnifiedMemory ? $"{hardware.RamGb:0} GB of shared memory" : $"{hardware.RamGb:0} GB of memory and no large graphics card";

        (string Model, string Why)[] ladder =
            gpu >= 22 ? [("qwen3-coder:30b", $"The strongest that fits in {memory}."), ("qwen2.5-coder:14b", "Faster, a little less capable."), ("qwen2.5-coder:7b", "Quick and small.")]
            : gpu >= 11 ? [("qwen2.5-coder:14b", $"Fits in {memory}."), ("qwen2.5-coder:7b", "Faster, less capable."), ("qwen2.5-coder:3b", "Smallest and quickest.")]
            : gpu >= 6 || hardware.RamGb >= 16 ? [("qwen2.5-coder:7b", gpu >= 6 ? $"Fits in {memory}." : $"Runs on the processor with {memory}: it works, just slower."), ("qwen2.5-coder:3b", "Quicker on a modest machine."), ("qwen2.5-coder:14b", "More capable, noticeably slower here.")]
            : [("qwen2.5-coder:3b", $"Suits {memory}."), ("qwen2.5-coder:1.5b", "Smallest; fine for quick questions."), ("qwen2.5-coder:7b", "More capable, slow on this machine.")];

        return ladder.Select((entry, index) => new ModelSuggestion(entry.Model, DownloadGb(entry.Model), entry.Why, index == 0)).ToList();
    }
}

internal sealed record SetupAction(string Kind, string Label, string? Url = null, string? Model = null, string? Tab = null);

internal sealed record SetupCheck(
    string Id,
    string Area,
    string Status,
    string Title,
    string Detail,
    string? Fix = null,
    string? Command = null,
    SetupAction? Action = null);

internal sealed record HubInfo(string Hostname, string OsFamily, IReadOnlyList<string> Addresses, bool BackendOnLan, IReadOnlyList<string> BackendUrls);

internal sealed record SetupReport(
    bool FirstRun,
    bool Ready,
    HardwareInfo Hardware,
    IReadOnlyList<ModelSuggestion> Suggestions,
    string TriageSuggestion,
    HubInfo Hub,
    IReadOnlyList<SetupCheck> Checks);

/// <summary>
/// The web UI's setup checklist: the same questions Test-Fleet.ps1 asks, answered by the running backend,
/// each with what to do about it and, where the fleet can do it itself, a button.
/// </summary>
internal sealed class FleetSetupService(
    FleetConfigStore configStore,
    FleetOptions fleetOptions,
    FleetHealthMonitor healthMonitor,
    SandboxHolder sandbox,
    IHttpClientFactory httpClientFactory)
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Info = "info";

    public async Task<SetupReport> BuildAsync(
        IReadOnlyList<McpServerStatus> mcpStatuses,
        IReadOnlyList<string> backendUrls,
        CancellationToken cancellationToken)
    {
        FleetConfig config = configStore.Current;
        HardwareInfo hardware = await HardwareProbe.ReadAsync(cancellationToken);
        HubInfo hub = Hub(backendUrls);
        var checks = new List<SetupCheck>();

        Task<(bool Up, string? Version)> localOllama = LocalOllamaAsync(cancellationToken);
        Task<IReadOnlyList<NodeHealthSnapshot>> nodes = Task.WhenAll(fleetOptions.Nodes.Select(node =>
            healthMonitor.GetNodeAsync(node.Name, cancellationToken, forceProbe: true))).ContinueWith(
            task => (IReadOnlyList<NodeHealthSnapshot>)task.Result, cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        Task<SetupCheck?> sandboxCheck = SandboxCheckAsync(cancellationToken);

        bool anyLocal = fleetOptions.Nodes.Any(node => IsLocal(node.OpenAiEndpoint));
        (bool up, string? version) = await localOllama;
        checks.Add(OllamaCheck(up, version, anyLocal, hub.OsFamily));

        foreach (NodeHealthSnapshot snapshot in await nodes)
        {
            if (fleetOptions.TryGetNode(snapshot.Name, out FleetNodeDefinition? node))
            {
                checks.Add(NodeCheck(node, snapshot, up, hub));
            }
        }

        if (await TriageCheckAsync(config, cancellationToken) is { } triage)
        {
            checks.Add(triage);
        }

        if (hardware.FreeDiskGb is { } free && free < 15)
        {
            checks.Add(new SetupCheck("disk", "machines", free < 5 ? Fail : Warn,
                $"Only {free:0.#} GB free where Ollama keeps models",
                $"Models are several gigabytes each and are stored in {hardware.ModelsFolder}.",
                "Free up space on that drive, or move Ollama's models elsewhere by setting OLLAMA_MODELS to a folder on a bigger drive and restarting Ollama."));
        }

        if (await sandboxCheck is { } sandboxResult)
        {
            checks.Add(sandboxResult);
        }

        checks.AddRange(ToolChecks(config));
        checks.AddRange(McpChecks(mcpStatuses));
        checks.Add(NetworkCheck(hub));

        NodeHealthSnapshot? fallback = (await nodes).FirstOrDefault(snapshot =>
            fleetOptions.TryGetNode(snapshot.Name, out FleetNodeDefinition? node) && node.Fallback);

        return new SetupReport(
            configStore.Seeded,
            fallback?.Ready == true,
            hardware,
            ModelAdvisor.ForCoding(hardware),
            ModelAdvisor.DefaultTriageModel,
            hub,
            checks);
    }

    public static HubInfo Hub(IReadOnlyList<string> backendUrls) => new(
        Environment.MachineName,
        HardwareProbe.OsFamily(),
        HubNetwork.PrivateIPv4(),
        backendUrls.Any(url => !IsLoopbackBinding(url)),
        backendUrls);

    private static bool IsLocal(Uri endpoint) =>
        OllamaAddress.TryGetRoot(endpoint.AbsoluteUri, out Uri root) && OllamaAddress.IsThisMachine(root);

    internal static bool IsLoopbackBinding(string url)
    {
        if (!Uri.TryCreate(url.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(bool Up, string? Version)> LocalOllamaAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            HttpClient client = httpClientFactory.CreateClient(FleetHealthMonitor.HttpClientName);
            using HttpResponseMessage response = await client.GetAsync("http://127.0.0.1:11434/api/version", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return (true, document.RootElement.TryGetProperty("version", out JsonElement version) ? version.GetString() : null);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return (false, null);
        }
    }

    /// <summary>Where Ollama's program is when it is installed but not running, for the Start button.</summary>
    public static string? OllamaApp()
    {
        if (OperatingSystem.IsWindows())
        {
            string app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama app.exe");
            return File.Exists(app) ? app : null;
        }

        return OperatingSystem.IsMacOS() && Directory.Exists("/Applications/Ollama.app") ? "/Applications/Ollama.app" : null;
    }

    public static bool OllamaInstalled() => QuickProcess.FindOnPath("ollama") is not null || OllamaApp() is not null;

    private static SetupCheck OllamaCheck(bool up, string? version, bool anyLocal, string osFamily)
    {
        if (up)
        {
            return new SetupCheck("ollama", "machines", Ok, $"Ollama {version} is running on this computer".Replace("  ", " "),
                "It runs the models. The fleet talks to it on port 11434.");
        }

        string status = anyLocal ? Fail : Info;
        if (OllamaInstalled())
        {
            string how = osFamily switch
            {
                "windows" => "Start Ollama from the Start menu (it sits in the notification area), or press Start Ollama.",
                "macos" => "Open the Ollama app, or press Start Ollama.",
                _ => "Start its service: sudo systemctl start ollama (or run ollama serve in a terminal)."
            };
            return new SetupCheck("ollama", "machines", status, "Ollama is installed but not running",
                anyLocal ? "This computer serves a model, so Ollama has to be running here." : "Not needed here: your machines are other computers.",
                how, osFamily == "linux" ? "sudo systemctl start ollama" : null,
                OllamaApp() is not null ? new SetupAction("start-ollama", "Start Ollama") : null);
        }

        string install = osFamily switch
        {
            "windows" => "Download it from https://ollama.com/download and run the installer, or in PowerShell: winget install Ollama.Ollama. Then press Check again.",
            "macos" => "Download it from https://ollama.com/download, open it once, then press Check again.",
            _ => "Install it with the command below (from ollama.com), then press Check again."
        };
        return new SetupCheck("ollama", "machines", status, "Ollama is not installed on this computer",
            anyLocal ? "Ollama runs the models, and this computer is set to serve one." : "Not needed here: your machines are other computers.",
            install,
            osFamily switch
            {
                "windows" => "winget install Ollama.Ollama",
                "linux" => "curl -fsSL https://ollama.com/install.sh | sh",
                _ => null
            });
    }

    private static SetupCheck NodeCheck(FleetNodeDefinition node, NodeHealthSnapshot snapshot, bool localOllamaUp, HubInfo hub)
    {
        string where = IsLocal(node.OpenAiEndpoint) ? "this computer" : node.OpenAiEndpoint.Host;
        if (snapshot.Ready)
        {
            return new SetupCheck($"node:{node.Name}", "machines", Ok, $"{node.Name}: {node.Model} is ready",
                $"On {where}.");
        }

        if (snapshot.Failure == "model_missing")
        {
            double? size = ModelAdvisor.DownloadGb(node.Model);
            return new SetupCheck($"node:{node.Name}", "machines", Fail, $"{node.Name}: {node.Model} is not downloaded yet",
                $"Ollama answers on {where}, but this model is not there yet{(size is { } gb ? $" (about {gb:0.#} GB)" : string.Empty)}.",
                "Download it here, or pick a model that is already installed under Machines.",
                $"ollama pull {node.Model}",
                new SetupAction("pull", $"Download {node.Model}", node.OpenAiEndpoint.AbsoluteUri, node.Model));
        }

        if (IsLocal(node.OpenAiEndpoint))
        {
            return new SetupCheck($"node:{node.Name}", "machines", Fail, $"{node.Name}: Ollama on this computer is not answering",
                localOllamaUp ? $"Ollama runs, but not at {node.OpenAiEndpoint}." : "See the Ollama item above.",
                localOllamaUp ? "Check the address under Machines: it is normally http://127.0.0.1:11434/v1." : null);
        }

        string hubAddress = hub.Addresses.FirstOrDefault() ?? "<this computer's address>";
        return new SetupCheck($"node:{node.Name}", "machines", Fail, $"{node.Name}: cannot reach {where}",
            $"Nothing answered at {node.OpenAiEndpoint.Host}:{node.OpenAiEndpoint.Port} ({snapshot.Failure}). The computer may be off or asleep, " +
            "Ollama may not be listening on the network, or its firewall may not let this computer in.",
            $"On {where}: install Ollama, then run the worker setup from Agent Fleet's scripts folder, with this computer's address. " +
            "It makes Ollama listen on the network and lets only this computer through the firewall. The command below is for a Windows machine " +
            $"(PowerShell as administrator); on Linux: sudo bash scripts/setup-worker.sh --allow-from {hubAddress}. Machines, Add a machine, has the details.",
            $@".\scripts\Setup-Worker.ps1 -AllowFrom {hubAddress} -KeepAwake");
    }

    private async Task<SetupCheck?> TriageCheckAsync(FleetConfig config, CancellationToken cancellationToken)
    {
        if (config.Nodes.Count(node => !node.Vision) < 2 || fleetOptions.Nodes.FirstOrDefault(node => node.Fallback) is not { } fallback)
        {
            return null;
        }

        IReadOnlyList<string>? installed = await InstalledModelsAsync(fallback.OpenAiEndpoint.AbsoluteUri, cancellationToken);
        if (installed is null)
        {
            return null; // The fallback machine's own check already says it is unreachable.
        }

        string wanted = Tagged(config.TriageModel);
        if (installed.Any(model => Tagged(model) == wanted))
        {
            return new SetupCheck("triage", "machines", Ok, $"Routing model {config.TriageModel} is ready",
                $"It picks a machine for each message, on {fallback.Name}.");
        }

        double? size = ModelAdvisor.DownloadGb(config.TriageModel);
        return new SetupCheck("triage", "machines", Fail, $"Routing model {config.TriageModel} is not downloaded",
            $"With more than one machine, a small model on {fallback.Name} picks which one answers each message. Until it is there, everything goes to {fallback.Name}" +
            (size is { } gb ? $". It is about {gb:0.#} GB." : "."),
            "Download it, or pick another small model under Routing.",
            $"ollama pull {config.TriageModel}",
            new SetupAction("pull", $"Download {config.TriageModel}", fallback.OpenAiEndpoint.AbsoluteUri, config.TriageModel));
    }

    public async Task<IReadOnlyList<string>?> InstalledModelsAsync(string address, CancellationToken cancellationToken)
    {
        if (!OllamaAddress.TryGetRoot(address, out Uri root))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            HttpClient client = httpClientFactory.CreateClient(FleetHealthMonitor.HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(OllamaAddress.Endpoint(root, "api/tags"), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return document.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array
                ? models.EnumerateArray().Select(model => model.TryGetProperty("name", out JsonElement name) ? name.GetString() : null)
                    .OfType<string>().ToList()
                : [];
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string Tagged(string model) => model.Contains(':') ? model : $"{model}:latest";

    private async Task<SetupCheck?> SandboxCheckAsync(CancellationToken cancellationToken)
    {
        DockerSandboxOptions options = sandbox.Options;
        var setUp = new SetupAction("tab", "Sandbox settings", Tab: "sandbox");
        switch (options.Mode)
        {
            case SandboxMode.Off:
                return new SetupCheck("sandbox", "sandbox", Info, "The code sandbox is off",
                    "run_sandboxed_code, which runs snippets in a throwaway Docker container, is not offered. Everything else works.",
                    "Optional: install Docker here, or use Docker on another machine over SSH.", Action: setUp with { Label = "Set it up" });
            case SandboxMode.Local:
                SandboxProbe probe = await SandboxSetup.ProbeLocalDockerAsync(cancellationToken);
                return probe.Ok
                    ? new SetupCheck("sandbox", "sandbox", Ok, $"Code sandbox: Docker {probe.Detail} on this computer",
                        "Snippets run in a locked-down, throwaway container.", Action: setUp)
                    : new SetupCheck("sandbox", "sandbox", Warn, "Code sandbox: Docker is not ready", probe.Detail, probe.Hint, Action: setUp);
            default:
                return options.HostKey is null
                    ? new SetupCheck("sandbox", "sandbox", Warn, $"Code sandbox: Docker on {options.Host} over SSH",
                        "The fleet does not know that machine's identity yet, so it cannot tell it apart from an impostor.",
                        "Open Sandbox settings and press Test, then Save: the fleet remembers the machine's key and refuses a different one.", Action: setUp)
                    : new SetupCheck("sandbox", "sandbox", Ok, $"Code sandbox: Docker on {options.Host} over SSH",
                        $"As {options.User}, with a key. The machine's identity is remembered.", Action: setUp);
        }
    }

    private static IReadOnlyList<SetupCheck> ToolChecks(FleetConfig config)
    {
        var checks = new List<SetupCheck>();
        string os = HardwareProbe.OsFamily();

        if (config.IsToolEnabled("run_git_command") && QuickProcess.FindOnPath("git") is null)
        {
            checks.Add(new SetupCheck("git", "tools", Warn, "git is not installed",
                "The run_git_command tool needs it.",
                "Install git, then restart the fleet so it sees it.",
                os switch { "windows" => "winget install Git.Git", "macos" => "xcode-select --install", _ => "sudo apt install git" }));
        }

        IEnumerable<string> commands = config.McpServerMap.Values
            .Where(server => server.Enabled && server.Type == "stdio" && server.Command is not null)
            .Select(server => Path.GetFileNameWithoutExtension(server.Command!).ToLowerInvariant());
        HashSet<string> used = commands.ToHashSet(StringComparer.Ordinal);

        if ((used.Contains("npx") || used.Contains("node")) && QuickProcess.FindOnPath("npx") is null)
        {
            checks.Add(new SetupCheck("npx", "tools", Fail, "Node.js is not on the backend's PATH",
                "An MCP server you added starts with npx, which comes with Node.js.",
                "Install Node.js 20 or newer (https://nodejs.org). If it is installed, the backend's account cannot see it: restart the fleet after installing.",
                os == "windows" ? "winget install OpenJS.NodeJS.LTS" : null));
        }

        if ((used.Contains("uvx") || used.Contains("uv")) && QuickProcess.FindOnPath("uvx") is null)
        {
            checks.Add(new SetupCheck("uvx", "tools", Fail, "uv is not installed",
                "An MCP server you added starts with uvx, which comes with uv (a Python tool runner).",
                "Install uv, then restart the fleet so it sees it.",
                os == "windows" ? "winget install astral-sh.uv" : "curl -LsSf https://astral.sh/uv/install.sh | sh"));
        }

        if (used.Contains("docker") && QuickProcess.FindOnPath("docker") is null)
        {
            checks.Add(new SetupCheck("docker-mcp", "tools", Fail, "Docker is not installed",
                "An MCP server you added starts with docker.", "Install Docker Desktop (Windows, macOS) or Docker Engine (Linux)."));
        }

        return checks;
    }

    private static IEnumerable<SetupCheck> McpChecks(IReadOnlyList<McpServerStatus> statuses)
    {
        foreach (McpServerStatus status in statuses.Where(status => status.Enabled && !status.Connected))
        {
            string firstLine = (status.Error ?? "It did not start.").Split('\n')[0];
            yield return new SetupCheck($"mcp:{status.Name}", "tools", Fail, $"Tool server {status.Name} did not start",
                firstLine.Length > 300 ? firstLine[..300] + "..." : firstLine,
                "Open Tools to see everything it printed, fix it or turn it off.",
                Action: new SetupAction("tab", "Tools", Tab: "tools"));
        }
    }

    private static SetupCheck NetworkCheck(HubInfo hub) =>
        hub.BackendOnLan
            ? new SetupCheck("network", "network", Warn, "Other computers on your network can use the fleet",
                "The backend answers on the network. There is no login: anyone who can reach it can read and change files and run commands on this computer through its tools.",
                "Keep this only on a network you trust, or reinstall without -ListenOnLan so it answers this computer only. See docs/security.md.")
            : new SetupCheck("network", "network", Ok, "Only this computer can use the fleet",
                "The backend answers on localhost only. Your other machines do not need to reach it: it reaches them.");
}
