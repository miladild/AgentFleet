using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// Which code paths may read from a vision machine: the stored fit measurement, and the background health sweep. A turned-off
/// machine is not contacted by either; a machine that is on is read as before.
/// </summary>
public sealed class VisionModeProbeScopeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-probe-scope-" + Guid.NewGuid().ToString("N"));

    public VisionModeProbeScopeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLEET_CONFIG_PATH"] = Path.Combine(_directory, "fleet.config.json"),
            ["FLEET_PLANS_DIR"] = Path.Combine(_directory, "plans"),
            ["FLEET_LAB_DIR"] = Path.Combine(_directory, "lab"),
            ["HUB_OLLAMA_MODEL"] = "model-x"
        })
        .Build();

    // Counts every request the fit probe makes and answers each with an empty object.
    private sealed class Machine : HttpMessageHandler
    {
        public readonly List<string> Paths = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                await request.Content.ReadAsStringAsync(cancellationToken);
            }

            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // A vision machine in the given mode, with one stored measurement of its model, as an earlier measurement would have left it.
    private (FitProbeService Service, Machine Machine) FitService(string mode)
    {
        IConfiguration configuration = Configuration();
        var store = new FleetConfigStore(configuration);
        store.Save(store.Current with
        {
            Nodes =
            [
                new("hub", "http://hub.example.test/v1", "model-x", "hub", "heavy", Fallback: true),
                new("vision-node", "http://vision.example.test:11434/v1", "model-x", "vision", Vision: true, VisionMode: mode)
            ]
        });
        FleetOptions options = FleetOptions.Load(configuration, store);

        string labDirectory = Path.Combine(_directory, "lab");
        Directory.CreateDirectory(labDirectory);
        var measured = new FitProbeHistoryEntry(
            "job-1", DateTimeOffset.UtcNow, "http://vision.example.test:11434/", "vision-node", "model-x",
            "sha256:old", "1.0.0", 4096, "ok", 1.0, 20.0, 1.0, null);
        // The history file is written in camel case, as the service writes it.
        var camelCase = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(labDirectory, "fit-history.jsonl"), JsonSerializer.Serialize(measured, camelCase) + "\n");

        var machine = new Machine();
        var service = new FitProbeService(
            options,
            new FleetPlanStore(configuration),
            store,
            new Factory(machine),
            configuration,
            NullLogger<FitProbeService>.Instance,
            new MeasuringMachines());
        return (service, machine);
    }

    [Fact]
    public async Task A_turned_off_vision_machine_reports_no_measurement_and_is_not_contacted()
    {
        (FitProbeService service, Machine machine) = FitService("off");

        FitProbeLatestResult? latest = await service.LatestAsync("vision-node", CancellationToken.None);

        Assert.Null(latest);
        Assert.Empty(machine.Paths);
    }

    [Fact]
    public async Task A_vision_machine_that_is_on_still_reports_its_measurement_and_is_read_as_before()
    {
        (FitProbeService service, Machine machine) = FitService("ondemand");

        FitProbeLatestResult? latest = await service.LatestAsync("vision-node", CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal("vision-node", latest.Node);
        Assert.Contains("/api/tags", machine.Paths);
    }

    [Fact]
    public void The_background_sweep_probes_every_machine_except_a_turned_off_vision_machine()
    {
        IConfiguration configuration = Configuration();
        var store = new FleetConfigStore(configuration);
        store.Save(store.Current with
        {
            Nodes =
            [
                new("hub", "http://hub.example.test/v1", "model-x", "hub", "heavy", Fallback: true),
                new("worker", "http://192.0.2.20:11434/v1", "model-x", "worker", "standard"),
                new("vision-off", "http://192.0.2.10:11434/v1", "vision-m", "vision", Vision: true, VisionMode: "off"),
                new("vision-ondemand", "http://192.0.2.11:11434/v1", "vision-m", "vision", Vision: true, VisionMode: "ondemand"),
                new("vision-always", "http://192.0.2.12:11434/v1", "vision-m", "vision", Vision: true, VisionMode: "always")
            ]
        });
        FleetOptions options = FleetOptions.Load(configuration, store);

        string[] probed = FleetHealthBackgroundService.MachinesToProbe(options.Nodes).Select(node => node.Name).ToArray();

        Assert.Equal(new[] { "hub", "worker", "vision-ondemand", "vision-always" }, probed);
    }
}
