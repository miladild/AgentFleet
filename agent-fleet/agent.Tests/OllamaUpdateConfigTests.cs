using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class OllamaUpdateConfigTests : IDisposable
{
    private const string HostKey = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-update-cfg-" + Guid.NewGuid().ToString("N"));

    public OllamaUpdateConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ConfigPath => Path.Combine(_dir, "fleet.config.json");

    private FleetConfigStore Open() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = ConfigPath })
            .Build());

    private void Write(string updateBlock) =>
        File.WriteAllText(
            ConfigPath,
            """{"triageModel":"t","mode":"conservative","planModeEnabled":false,"tools":{},"nodes":[""" +
            """{"name":"hub","url":"http://127.0.0.1:11434/v1","model":"m","purpose":"local","tier":"heavy","fallback":true,"ollamaUpdate":{"method":"local"}},""" +
            """{"name":"worker","url":"http://192.0.2.10:11434/v1","model":"m","purpose":"worker","tier":"standard","ollamaUpdate":""" + updateBlock + "}]}");

    private static string Ssh(string? user = null, string? hostKey = null, string? host = null, string? method = null, int port = 22) =>
        $$"""{"method":"{{method ?? "ssh-linux"}}","host":"{{host ?? "192.0.2.10"}}","port":{{port}},"user":"{{user ?? "fleet-updater"}}","keyPath":"C:/keys/updater","hostKey":"{{hostKey ?? HostKey}}"}""";

    [Fact]
    public void The_update_block_survives_a_save_and_a_reload()
    {
        Write(Ssh());
        FleetConfigStore store = Open();
        FleetOllamaUpdateConfig expected = new(FleetOllamaUpdateConfig.SshLinux, "192.0.2.10", 22, "fleet-updater", "C:/keys/updater", HostKey);

        Assert.Equal(expected, store.Current.Nodes.Single(node => node.Name == "worker").OllamaUpdate);
        Assert.Equal(FleetOllamaUpdateConfig.Local, store.Current.Nodes.Single(node => node.Name == "hub").OllamaUpdate!.Method);

        store.Save(store.Current);
        FleetConfig reopened = Open().Current;

        Assert.Equal(expected, reopened.Nodes.Single(node => node.Name == "worker").OllamaUpdate);
        Assert.Contains("\"ollamaUpdate\"", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void A_node_without_the_block_has_no_update_method_and_the_block_is_not_written()
    {
        File.WriteAllText(ConfigPath, """{"triageModel":"t","mode":"conservative","planModeEnabled":false,"tools":{},"nodes":[{"name":"hub","url":"http://127.0.0.1:11434/v1","model":"m","purpose":"local","fallback":true}]}""");

        FleetNodeConfig node = Assert.Single(Open().Current.Nodes);

        Assert.Null(node.OllamaUpdate);
    }

    [Fact]
    public void The_method_is_read_without_regard_to_case_and_spacing()
    {
        Write(Ssh(method: " SSH-Linux "));

        Assert.Equal("ssh-linux", Open().Current.Nodes.Single(node => node.Name == "worker").OllamaUpdate!.Method);
    }

    [Theory]
    [InlineData("root", null, null, null, "dedicated account")]
    [InlineData("Fleet Updater", null, null, null, "dedicated account")]
    [InlineData("u;id", null, null, null, "dedicated account")]
    [InlineData(null, "SHA256:", null, null, "SHA256 fingerprint")]
    [InlineData(null, "MD5:aa:bb", null, null, "SHA256 fingerprint")]
    [InlineData(null, null, "192.0.2.10/x", null, "needs a host")]
    [InlineData(null, null, "user@192.0.2.10", null, "needs a host")]
    [InlineData(null, null, null, "windows-remote", "choose none, local or ssh-linux")]
    public void A_block_that_cannot_be_used_safely_is_refused_with_a_reason(string? user, string? hostKey, string? host, string? method, string expected)
    {
        Write(Ssh(user, hostKey, host, method));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Open());

        Assert.Contains(expected, exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void A_port_outside_the_range_is_refused(int port)
    {
        Write(Ssh(port: port));

        Assert.Contains("valid port", Assert.Throws<InvalidOperationException>(() => Open()).Message);
    }

    [Fact]
    public void The_ssh_method_needs_a_host_key_a_user_and_a_key()
    {
        Write("""{"method":"ssh-linux","host":"192.0.2.10"}""");

        Assert.Contains("needs a host, a user, a key path, a pinned SHA256 host key", Assert.Throws<InvalidOperationException>(() => Open()).Message);
    }

    // The web UI sends the whole machine list back when it saves. The form never shows this block, so a save from the
    // form must not erase it, and an explicit clear must remove it.

    private static FleetNodeConfig Previous() =>
        new("worker", "http://192.0.2.10:11434/v1", "m", "p", OllamaUpdate: new FleetOllamaUpdateConfig(FleetOllamaUpdateConfig.SshLinux, "192.0.2.10", 22, "fleet-updater", "k", HostKey));

    private static FleetConfigNodeUpdate Update(Func<FleetConfigNodeUpdate, FleetConfigNodeUpdate>? change = null)
    {
        var node = new FleetConfigNodeUpdate("worker", "http://192.0.2.10:11434/v1", "m", "p");
        return change is null ? node : change(node);
    }

    [Fact]
    public void A_save_from_the_form_that_leaves_the_block_out_keeps_the_saved_one()
    {
        FleetNodeConfig merged = FleetConfigStore.MergeNode(Update(), Previous());

        Assert.Equal(Previous().OllamaUpdate, merged.OllamaUpdate);
    }

    [Fact]
    public void A_block_in_the_request_replaces_the_saved_one_and_clear_removes_it()
    {
        FleetOllamaUpdateConfig local = new(FleetOllamaUpdateConfig.Local);

        Assert.Equal(local, FleetConfigStore.MergeNode(Update(node => node with { OllamaUpdate = local }), Previous()).OllamaUpdate);
        Assert.Null(FleetConfigStore.MergeNode(Update(node => node with { ClearOllamaUpdate = true }), Previous()).OllamaUpdate);
        Assert.Null(FleetConfigStore.MergeNode(Update(), null).OllamaUpdate);
    }

    [Fact]
    public void The_block_arrives_from_the_form_in_camel_case_and_goes_back_out_in_the_saved_file()
    {
        const string body = """{"nodes":[{"name":"worker","url":"http://192.0.2.10:11434/v1","model":"m","purpose":"p","ollamaUpdate":{"method":"ssh-linux","host":"192.0.2.10","port":2222,"user":"fleet-updater","keyPath":"k","hostKey":"SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}}]}""";
        FleetConfigUpdateRequest request = JsonSerializer.Deserialize<FleetConfigUpdateRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        FleetNodeConfig merged = FleetConfigStore.MergeNode(request.Nodes![0], null);

        Assert.Equal(2222, merged.OllamaUpdate!.Port);
        Assert.Equal("fleet-updater", merged.OllamaUpdate.User);
    }

    [Fact]
    public void The_example_config_shows_the_block_and_still_validates()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "fleet.config.example.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        File.Copy(Path.Combine(directory, "fleet.config.example.json"), ConfigPath);

        FleetConfig config = Open().Current;

        Assert.Contains(config.Nodes, node => node.OllamaUpdate?.Method == FleetOllamaUpdateConfig.Local);
        Assert.Contains(config.Nodes, node => node.OllamaUpdate?.Method == FleetOllamaUpdateConfig.SshLinux);
    }
}
