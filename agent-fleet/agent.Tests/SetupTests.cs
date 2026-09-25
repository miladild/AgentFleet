using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;

namespace AgentFleet.Tests;

public sealed class SetupTests
{
    [Theory]
    [InlineData("192.168.1.20", "http://192.168.1.20:11434/")]
    [InlineData("192.168.1.20:11500", "http://192.168.1.20:11500/")]
    [InlineData("http://pc2:11434/v1", "http://pc2:11434/")]
    [InlineData("http://pc2", "http://pc2:11434/")]
    [InlineData("https://ollama.example.com/v1", "https://ollama.example.com/")]
    public void An_ollama_address_in_any_form_gives_the_api_root(string address, string expected)
    {
        Assert.True(OllamaAddress.TryGetRoot(address, out Uri root));
        Assert.Equal(expected, root.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://pc2")]
    [InlineData("http://user:pw@pc2:11434")]
    public void Nonsense_is_not_an_ollama_address(string address) =>
        Assert.False(OllamaAddress.TryGetRoot(address, out _));

    [Theory]
    [InlineData("qwen2.5-coder:7b", true)]
    [InlineData("hf.co/bartowski/Qwen2.5-Coder-7B-GGUF:Q4_K_M", true)]
    [InlineData("llama3.2", true)]
    [InlineData("rm -rf /", false)]
    [InlineData("", false)]
    [InlineData("model;echo", false)]
    public void Only_real_looking_model_names_are_downloaded(string model, bool valid) =>
        Assert.Equal(valid, ModelPullService.IsValidModelName(model));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<ModelPullStatus> PullAsync(string ndjson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson") });
        var service = new ModelPullService(new FakeFactory(handler), NullLogger<ModelPullService>.Instance);
        var finished = new TaskCompletionSource<ModelPullStatus>();
        service.Finished += result => finished.TrySetResult(result);

        Assert.True(OllamaAddress.TryGetRoot("10.0.0.5", out Uri root));
        ModelPullStatus started = service.Start(root, "qwen2.5-coder:7b");
        Assert.Equal(ModelPullService.Running, started.State);

        ModelPullStatus result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("\"model\":\"qwen2.5-coder:7b\"", handler.Bodies.Single());
        Assert.Equal(result, service.List().Single());
        return result;
    }

    [Fact]
    public async Task A_download_adds_up_the_layers_and_ends_done_on_success()
    {
        ModelPullStatus result = await PullAsync(string.Join('\n',
            """{"status":"pulling manifest"}""",
            """{"status":"pulling a1","digest":"sha256:a1","total":1000,"completed":400}""",
            """{"status":"pulling b2","digest":"sha256:b2","total":500,"completed":500}""",
            """{"status":"pulling a1","digest":"sha256:a1","total":1000,"completed":1000}""",
            """{"status":"verifying sha256 digest"}""",
            """{"status":"success"}"""));

        Assert.Equal(ModelPullService.Done, result.State);
        Assert.Equal(1500, result.Total);
        Assert.Equal(1500, result.Completed);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task An_unknown_model_fails_with_a_plain_explanation()
    {
        ModelPullStatus result = await PullAsync("""{"status":"pulling manifest"}""" + "\n" + """{"error":"pull model manifest: file does not exist"}""");

        Assert.Equal(ModelPullService.Failed, result.State);
        Assert.Contains("no model called qwen2.5-coder:7b", result.Error);
    }

    [Fact]
    public async Task A_stream_that_stops_early_is_not_reported_as_done()
    {
        ModelPullStatus result = await PullAsync("""{"status":"pulling a1","digest":"sha256:a1","total":1000,"completed":10}""");

        Assert.Equal(ModelPullService.Failed, result.State);
        Assert.Contains("stopped before it finished", result.Error);
    }

    [Fact]
    public async Task An_http_error_from_ollama_is_reported()
    {
        ModelPullStatus result = await PullAsync("""{"error":"no space left on device"}""", HttpStatusCode.InternalServerError);

        Assert.Equal(ModelPullService.Failed, result.State);
        Assert.Contains("ran out of disk space", result.Error);
    }

    private static HardwareInfo Machine(double ram, double vram = 0, bool unified = false) =>
        new("test", "linux", ram, vram > 0 ? "card" : null, vram, unified, "/models", 100);

    [Theory]
    [InlineData(64, 24, false, "qwen3-coder:30b")]
    [InlineData(32, 12, false, "qwen2.5-coder:14b")]
    [InlineData(16, 8, false, "qwen2.5-coder:7b")]
    [InlineData(16, 0, false, "qwen2.5-coder:7b")]
    [InlineData(8, 0, false, "qwen2.5-coder:3b")]
    [InlineData(32, 0, true, "qwen2.5-coder:14b")]
    [InlineData(48, 0, true, "qwen3-coder:30b")]
    public void The_suggested_model_suits_the_machine(double ram, double vram, bool unified, string expected)
    {
        IReadOnlyList<ModelSuggestion> suggestions = ModelAdvisor.ForCoding(Machine(ram, vram, unified));

        Assert.Equal(expected, suggestions.Single(suggestion => suggestion.Recommended).Model);
        Assert.Equal(3, suggestions.Select(suggestion => suggestion.Model).Distinct().Count());
        Assert.All(suggestions, suggestion => Assert.NotNull(suggestion.DownloadGb));
    }

    [Theory]
    [InlineData("http://localhost:8000", true)]
    [InlineData("http://127.0.0.1:8000", true)]
    [InlineData("http://[::1]:8000", true)]
    [InlineData("http://0.0.0.0:8000", false)]
    [InlineData("http://+:8000", false)]
    [InlineData("http://*:8000", false)]
    [InlineData("http://192.168.1.10:8000", false)]
    public void A_backend_bound_beyond_localhost_is_noticed(string url, bool loopback) =>
        Assert.Equal(loopback, FleetSetupService.IsLoopbackBinding(url));

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("100.100.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Private_addresses_are_recognised(string address, bool isPrivate) =>
        Assert.Equal(isPrivate, HubNetwork.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void A_created_key_is_a_real_ssh_key_and_is_never_overwritten()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"fleet-key-{Guid.NewGuid():N}");
        string path = Path.Combine(folder, "nested", "agent-fleet_rsa");
        try
        {
            SshKeyInfo created = SandboxSetup.CreateKey(path);

            Assert.True(created.Exists);
            Assert.Matches(@"^ssh-rsa AAAAB3NzaC1yc2E[A-Za-z0-9+/=]+ agent-fleet@[a-z0-9.-]+$", created.PublicKey);
            // SSH.NET, which the sandbox connects with, must be able to load it.
            using var loaded = new PrivateKeyFile(path);
            Assert.NotNull(loaded.Key);

            string before = File.ReadAllText(path);
            SshKeyInfo again = SandboxSetup.CreateKey(path);
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(created.PublicKey, again.PublicKey);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void A_key_path_must_be_a_full_path() =>
        Assert.Throws<ArgumentException>(() => SandboxSetup.CreateKey("keys/fleet"));

    [Theory]
    [InlineData("permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock", "not allowed to use Docker")]
    [InlineData("sudo: a password is required", "sudo asked for a password")]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", "not running")]
    [InlineData("bash: line 1: docker: command not found", "not installed")]
    public void Docker_errors_are_explained(string error, string expected)
    {
        (string detail, string? hint) = SandboxSetup.ExplainDocker(error, remote: true, "builder");

        Assert.Contains(expected, detail);
        Assert.NotNull(hint);
    }

    [Fact]
    public void A_remembered_host_key_is_carried_into_the_options()
    {
        string key = Path.GetTempFileName();
        try
        {
            DockerSandboxOptions options = DockerSandboxOptions.Resolve(
                new FleetSandboxConfig(Mode: "ssh", Host: "box", KeyPath: key, HostKey: " SHA256:abc "),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

            Assert.Equal("SHA256:abc", options.HostKey);
        }
        finally
        {
            File.Delete(key);
        }
    }
}
