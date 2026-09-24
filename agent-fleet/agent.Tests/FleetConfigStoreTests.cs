using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class FleetConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-cfg-tests-" + Guid.NewGuid().ToString("N"));

    public FleetConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ConfigPath => Path.Combine(_dir, "fleet.config.json");

    private FleetConfigStore Open() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = ConfigPath })
            .Build());

    private void WriteConfig(string nodesJson) =>
        File.WriteAllText(
            ConfigPath,
            "{\"triageModel\":\"t\",\"mode\":\"conservative\",\"planModeEnabled\":false,\"nodes\":" + nodesJson + ",\"tools\":{}}");

    [Fact]
    public void First_run_seeds_one_generic_local_node()
    {
        FleetConfig config = Open().Current;

        FleetNodeConfig node = Assert.Single(config.Nodes);
        Assert.Equal("hub", node.Name);
        Assert.Contains("127.0.0.1", node.Url);
        Assert.True(node.Fallback);
        Assert.Equal(FleetTiers.Heavy, node.Tier);
        Assert.True(File.Exists(ConfigPath));
        Assert.DoesNotContain("192.168.", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void The_example_config_shipped_in_the_repo_loads_and_validates()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "fleet.config.example.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        File.Copy(Path.Combine(directory, "fleet.config.example.json"), ConfigPath);

        FleetConfig config = Open().Current;

        Assert.Equal(4, config.Nodes.Count);
        Assert.Equal("hub", Assert.Single(config.Nodes, node => node.Fallback).Name);
        Assert.Contains(config.Nodes, node => node.Vision);
        Assert.Equal("auto", config.Sandbox?.Mode);
        Assert.Contains("microsoft-learn", config.McpServerMap.Keys);
    }

    [Theory]
    [InlineData("http://192.168.1.20:11434", "http://192.168.1.20:11434/v1")]
    [InlineData("http://host:11434/", "http://host:11434/v1")]
    [InlineData("  http://host:11434  ", "http://host:11434/v1")]
    [InlineData("http://host:11434/v1", "http://host:11434/v1")]
    [InlineData("https://gpu.example.com/v1/", "https://gpu.example.com/v1/")]
    public void The_v1_suffix_is_added_when_a_node_address_leaves_it_out(string written, string expected)
    {
        WriteConfig("[{\"name\":\"a\",\"url\":\"" + written + "\",\"model\":\"m\",\"purpose\":\"\"}]");

        Assert.Equal(expected, Assert.Single(Open().Current.Nodes).Url);
    }

    [Fact]
    public void A_node_address_with_some_other_path_is_refused_at_save_time_not_at_startup()
    {
        WriteConfig("""[{"name":"a","url":"http://h:1/api","model":"m","purpose":""}]""");

        var exception = Assert.Throws<InvalidOperationException>(Open);

        Assert.Contains("/v1", exception.Message);
    }

    [Fact]
    public void A_config_without_tiers_or_a_fallback_gets_defaults()
    {
        WriteConfig("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":""},{"name":"b","url":"http://h:2/v1","model":"m","purpose":""}]""");

        FleetConfig config = Open().Current;

        Assert.All(config.Nodes, node => Assert.Equal(FleetTiers.Standard, node.Tier));
        Assert.Equal("a", Assert.Single(config.Nodes, node => node.Fallback).Name);
    }

    [Fact]
    public void The_first_heavy_node_becomes_the_fallback_when_none_is_marked()
    {
        WriteConfig("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":"","tier":"light"},{"name":"b","url":"http://h:2/v1","model":"m","purpose":"","tier":"heavy"}]""");

        Assert.Equal("b", Assert.Single(Open().Current.Nodes, node => node.Fallback).Name);
    }

    [Fact]
    public void Vision_nodes_carry_no_tier_and_never_become_the_fallback()
    {
        WriteConfig("""[{"name":"eyes","url":"http://h:1/v1","model":"m","purpose":"","vision":true,"fallback":true},{"name":"text","url":"http://h:2/v1","model":"m","purpose":""}]""");

        FleetConfig config = Open().Current;

        FleetNodeConfig eyes = config.Nodes.Single(node => node.Name == "eyes");
        Assert.Null(eyes.Tier);
        Assert.False(eyes.Fallback);
        Assert.True(config.Nodes.Single(node => node.Name == "text").Fallback);
    }

    [Theory]
    [InlineData("""[{"name":"My PC!","url":"http://h:1/v1","model":"m","purpose":""}]""", "invalid")]
    [InlineData("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":""},{"name":"a","url":"http://h:2/v1","model":"m","purpose":""}]""", "more than once")]
    [InlineData("""[{"name":"a","url":"not a url","model":"m","purpose":""}]""", "invalid URL")]
    [InlineData("""[{"name":"a","url":"http://h:1/v1","model":"","purpose":""}]""", "must have a model")]
    [InlineData("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":"","tier":"huge"}]""", "tier")]
    [InlineData("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":"","fallback":true},{"name":"b","url":"http://h:2/v1","model":"m","purpose":"","fallback":true}]""", "Exactly one node")]
    [InlineData("""[{"name":"eyes","url":"http://h:1/v1","model":"m","purpose":"","vision":true}]""", "at least one text node")]
    [InlineData("[]", "at least one text node")]
    public void Invalid_configs_are_refused_with_a_clear_message(string nodesJson, string expected)
    {
        WriteConfig(nodesJson);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(Open);

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Save_normalizes_and_persists_and_a_reopen_sees_it()
    {
        FleetConfigStore store = Open();
        FleetConfig updated = store.Current with
        {
            Nodes =
            [
                new FleetNodeConfig("main", "http://127.0.0.1:11434/v1", "m1", "x", FleetTiers.Heavy),
                new FleetNodeConfig("quick", "http://10.0.0.2:11434/v1", "m2", "y", FleetTiers.Light),
                new FleetNodeConfig("eyes", "http://10.0.0.3:11434/v1", "m3", "z", Vision: true)
            ]
        };

        store.Save(updated);
        FleetConfig reopened = Open().Current;

        Assert.Equal(["main", "quick", "eyes"], reopened.Nodes.Select(node => node.Name));
        Assert.Equal("main", Assert.Single(reopened.Nodes, node => node.Fallback).Name);
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.True(file.RootElement.GetProperty("nodes")[2].GetProperty("vision").GetBoolean());
    }

    [Fact]
    public void Mode_and_plan_mode_persist_across_a_reopen()
    {
        FleetConfigStore store = Open();
        store.UpdateMode("aggressive");
        store.UpdatePlanMode(true);

        FleetConfig reopened = Open().Current;

        Assert.Equal("aggressive", reopened.Mode);
        Assert.True(reopened.PlanModeEnabled);
    }

    [Fact]
    public void A_tool_missing_from_the_file_counts_as_enabled()
    {
        Assert.True(Open().Current.IsToolEnabled("something_added_later"));
    }
}
