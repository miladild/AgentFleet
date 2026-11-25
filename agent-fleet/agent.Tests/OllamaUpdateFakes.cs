using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>The pieces an Ollama update needs, replaced by fakes: no network, no SSH, no processes.</summary>
internal sealed class OllamaUpdateFixture : IDisposable
{
    public const string Latest = "0.13.0";
    public const string LinuxUser = "fleet-updater";
    public static readonly byte[] InstallerBytes = Encoding.ASCII.GetBytes("pretend installer bytes");

    public static readonly string InstallerSha = Convert.ToHexString(SHA256.HashData(InstallerBytes)).ToLowerInvariant();
    public static readonly string LinuxSha = new('b', 64);

    public readonly List<string> Requests = [];
    public readonly Dictionary<string, string?> Versions = new(StringComparer.Ordinal);
    public readonly FakeHost Host = new();
    public readonly FakeRemote Remote = new();
    public bool PlanRunning;
    public string ReleaseJson;
    public HttpStatusCode ReleaseStatus = HttpStatusCode.OK;
    public Func<HttpRequestMessage, HttpResponseMessage>? Download;
    public readonly List<(string Url, string UserAgent, bool HasAuthorization)> Seen = [];

    private FleetConfig _config;
    private OllamaUpdateService? _service;

    public OllamaUpdateFixture()
    {
        ReleaseJson = Release(Latest);
        Host.Probe = root => Versions.TryGetValue(root.AbsoluteUri, out string? version) ? version : null;
        _config = Config(Node("hub", "http://127.0.0.1:11434/v1", Method("local")));
    }

    public OllamaUpdateService Service => _service ??= Build();

    public OllamaUpdateTimings Timings { get; set; } = new(
        VersionPoll: TimeSpan.FromMilliseconds(5),
        LocalVersionWait: TimeSpan.FromMilliseconds(300),
        RemoteVersionWait: TimeSpan.FromMilliseconds(300),
        ServeGrace: TimeSpan.FromMilliseconds(40),
        DownloadLimit: TimeSpan.FromSeconds(10),
        InstallerLimit: TimeSpan.FromSeconds(10),
        RemoteStepLimit: TimeSpan.FromSeconds(10));

    public void UseNodes(params FleetNodeConfig[] nodes)
    {
        _config = Config(nodes);
        _service = null;
    }

    public static FleetConfig Config(params FleetNodeConfig[] nodes) =>
        new("t", "conservative", false, nodes, new Dictionary<string, FleetToolConfig>());

    public static FleetNodeConfig Node(string name, string url, FleetOllamaUpdateConfig? update = null, bool vision = false, string? visionMode = null, string? platform = null) =>
        new(name, url, "m", "test", Vision: vision, VisionMode: visionMode, OllamaUpdate: update,
            Workspace: platform is null ? null : new FleetWorkerWorkspaceConfig("192.0.2.10", "agentfleet", "k", "SHA256:x", "/w", platform));

    public static FleetOllamaUpdateConfig Method(string method) => new(method);

    public static FleetOllamaUpdateConfig Ssh() =>
        new(FleetOllamaUpdateConfig.SshLinux, "192.0.2.10", 22, LinuxUser, "/keys/update", "SHA256:" + new string('A', 43));

    /// <summary>GitHub's "latest release" answer. A digest of "none" leaves the digest out (null).</summary>
    public static string Release(string tag, bool draft = false, bool prerelease = false, string? linuxDigest = null, string? windowsDigest = null, bool linuxAsset = true)
    {
        static string Digest(string? value, string fallback) =>
            value == "none" ? "null" : "\"" + (value ?? "sha256:" + fallback) + "\"";

        var assets = new List<string>
        {
            "{\"name\":\"OllamaSetup.exe\",\"digest\":" + Digest(windowsDigest, InstallerSha) + "}",
            "{\"name\":\"Ollama-darwin.zip\",\"digest\":\"sha256:" + new string('c', 64) + "\"}",
            "{\"name\":\"something-else.zip\",\"digest\":\"sha256:" + new string('d', 64) + "\"}"
        };
        if (linuxAsset)
        {
            assets.Add("{\"name\":\"ollama-linux-amd64.tar.zst\",\"digest\":" + Digest(linuxDigest, LinuxSha) + "}");
        }

        return "{\"tag_name\":\"" + tag + "\",\"draft\":" + draft.ToString().ToLowerInvariant() +
            ",\"prerelease\":" + prerelease.ToString().ToLowerInvariant() +
            ",\"published_at\":\"2026-11-01T10:00:00Z\",\"assets\":[" + string.Join(",", assets) + "]}";
    }

    private OllamaUpdateService Build()
    {
        var handler = new RouteHandler(request =>
        {
            lock (Seen)
            {
                Seen.Add((request.RequestUri!.AbsoluteUri, request.Headers.UserAgent.ToString(), request.Headers.Authorization is not null));
            }

            string url = request.RequestUri!.AbsoluteUri;
            lock (Requests)
            {
                Requests.Add(url);
            }

            if (url == OllamaUpdateAssets.LatestReleaseUrl)
            {
                return new HttpResponseMessage(ReleaseStatus) { Content = new StringContent(ReleaseJson, Encoding.UTF8, "application/json") };
            }

            if (Download is not null)
            {
                return Download(request);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(InstallerBytes) };
        });
        return new OllamaUpdateService(
            new Factory(handler),
            () => _config,
            Host.AsProbe(),
            Host,
            Remote,
            () => PlanRunning,
            NullLogger.Instance,
            Timings,
            isThisMachine: root => root.IsLoopback);
    }

    public void Dispose()
    {
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>Stands in for the version probe and for the machine the backend runs on.</summary>
internal sealed class FakeHost : IOllamaHostActions
{
    public bool IsWindows { get; set; } = true;

    public bool IsWindowsService { get; set; }

    public string? Executable { get; set; } = "C:/Ollama/ollama.exe";

    public Func<Uri, string?> Probe { get; set; } = _ => null;

    public List<string> Events { get; } = [];

    public List<string> StoppedProcesses { get; } = [];

    public List<string> InstallerArguments { get; } = [];

    public int InstallerExit { get; set; }

    public string? InstallerPath { get; private set; }

    public bool InstallerFileExisted { get; private set; }

    public Func<Task>? BeforeInstallerFinishes { get; set; }

    public Action? AfterInstaller { get; set; }

    public int ServeStarts { get; private set; }

    public string? OllamaExecutable() => Executable;

    public void StopProcesses(IReadOnlyList<string> names)
    {
        lock (Events)
        {
            Events.Add("stop");
            StoppedProcesses.AddRange(names);
        }
    }

    public async Task<int> RunInstallerAsync(string installerPath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Events)
        {
            Events.Add("install");
        }

        InstallerPath = installerPath;
        InstallerFileExisted = File.Exists(installerPath);
        InstallerArguments.AddRange(arguments);
        if (BeforeInstallerFinishes is not null)
        {
            await BeforeInstallerFinishes();
        }

        AfterInstaller?.Invoke();
        return InstallerExit;
    }

    public void StartServeHidden(string executable)
    {
        lock (Events)
        {
            Events.Add("serve");
        }

        ServeStarts++;
    }

    public IOllamaVersionProbe AsProbe() => new HostProbe(this);

    private sealed class HostProbe(FakeHost host) : IOllamaVersionProbe
    {
        public Task<string?> GetVersionAsync(Uri root, CancellationToken cancellationToken) => Task.FromResult(host.Probe(root));
    }
}

/// <summary>Answers each remote command from a function, and remembers them in order.</summary>
internal sealed class FakeRemote : IRemoteShellFactory
{
    private int _concurrent;

    public List<string> Commands { get; } = [];

    public int Opened { get; private set; }

    public int MostAtOnce { get; private set; }

    public Func<string, RemoteResult> Respond { get; set; } = command =>
        command.StartsWith("printf", StringComparison.Ordinal)
            ? new RemoteResult(0, "/home/" + OllamaUpdateFixture.LinuxUser, string.Empty)
            : new RemoteResult(0, string.Empty, string.Empty);

    public Func<Task>? BeforeFirstCommand { get; set; }

    public Task<IRemoteShell> OpenAsync(FleetOllamaUpdateConfig config, CancellationToken cancellationToken)
    {
        Opened++;
        int now = Interlocked.Increment(ref _concurrent);
        MostAtOnce = Math.Max(MostAtOnce, now);
        return Task.FromResult<IRemoteShell>(new Shell(this));
    }

    private sealed class Shell(FakeRemote owner) : IRemoteShell
    {
        public async Task<RemoteResult> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (owner.BeforeFirstCommand is { } gate && owner.Commands.Count == 0)
            {
                await gate();
            }

            lock (owner.Commands)
            {
                owner.Commands.Add(command);
            }

            return owner.Respond(command);
        }

        public void Dispose() => Interlocked.Decrement(ref owner._concurrent);
    }
}
