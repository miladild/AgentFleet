using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class McpToolProviderTests
{
    [Theory]
    [InlineData("microsoft-learn", "microsoft_docs_search", "microsoft-learn_microsoft_docs_search")]
    [InlineData("files", "read file!", "files_read_file_")]
    [InlineData("s", "a.b/c", "s_a_b_c")]
    public void Tool_names_are_prefixed_and_made_safe_for_models(string server, string tool, string expected) =>
        Assert.Equal(expected, McpToolProvider.ToolName(server, tool));

    [Fact]
    public void Tool_names_are_capped_at_64_characters()
    {
        string name = McpToolProvider.ToolName("server", new string('x', 200));

        Assert.Equal(64, name.Length);
        Assert.StartsWith("server_", name);
    }

    [Fact]
    public void Environment_references_are_expanded_and_unset_ones_become_empty()
    {
        Environment.SetEnvironmentVariable("FLEET_TEST_TOKEN", "s3cret");
        try
        {
            Assert.Equal("Bearer s3cret", McpToolProvider.ExpandEnvironment("Bearer ${env:FLEET_TEST_TOKEN}"));
            Assert.Equal("a--b", McpToolProvider.ExpandEnvironment("a-${env:FLEET_TEST_DEFINITELY_UNSET}-b"));
            Assert.Equal("no references here", McpToolProvider.ExpandEnvironment("no references here"));
            Assert.Equal("${notenv:X}", McpToolProvider.ExpandEnvironment("${notenv:X}"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLEET_TEST_TOKEN", null);
        }
    }

    [Fact]
    public async Task With_no_servers_configured_nothing_is_started()
    {
        McpToolProvider provider = await McpToolProvider.ConnectAsync(
            new Dictionary<string, FleetMcpServerConfig>(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            default);

        Assert.Empty(provider.Tools);
        Assert.Empty(provider.Statuses);
    }

    [Fact]
    public async Task A_disabled_server_is_reported_but_never_started()
    {
        var servers = new Dictionary<string, FleetMcpServerConfig>
        {
            ["off"] = new("stdio", Command: "this-command-must-never-run", Enabled: false)
        };

        McpToolProvider provider = await McpToolProvider.ConnectAsync(
            servers,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            default);

        McpServerStatus status = Assert.Single(provider.Statuses);
        Assert.False(status.Enabled);
        Assert.False(status.Connected);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task A_server_that_cannot_start_is_skipped_with_a_reason()
    {
        var servers = new Dictionary<string, FleetMcpServerConfig>
        {
            ["broken"] = new("stdio", Command: "fleet-test-no-such-command-12345")
        };

        McpToolProvider provider = await McpToolProvider.ConnectAsync(
            servers,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            default);

        McpServerStatus status = Assert.Single(provider.Statuses);
        Assert.False(status.Connected);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
        Assert.Empty(provider.Tools);
    }
}

public sealed class FleetConfigMcpValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-mcp-tests-" + Guid.NewGuid().ToString("N"));

    public FleetConfigMcpValidationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FleetConfigStore Open(string mcpJson)
    {
        string path = Path.Combine(_dir, "fleet.config.json");
        File.WriteAllText(
            path,
            "{\"triageModel\":\"t\",\"mode\":\"conservative\",\"planModeEnabled\":false," +
            "\"nodes\":[{\"name\":\"a\",\"url\":\"http://h:1/v1\",\"model\":\"m\",\"purpose\":\"\"}]," +
            "\"tools\":{}," + mcpJson + "}");
        return new FleetConfigStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = path })
            .Build());
    }

    [Fact]
    public void A_config_with_no_mcp_section_has_no_servers() =>
        Assert.Empty(Open("\"x\":1").Current.McpServerMap);

    [Fact]
    public void Valid_stdio_and_http_servers_load_with_a_lowercased_type()
    {
        FleetConfig config = Open("""
            "mcpServers":{
              "files":{"type":"STDIO","command":"npx","args":["-y","x"]},
              "docs":{"type":"http","url":"https://learn.microsoft.com/api/mcp"},
              "secret":{"type":"http","url":"${env:MY_MCP_URL}"}
            }
            """).Current;

        Assert.Equal("stdio", config.McpServerMap["files"].Type);
        Assert.Equal(["-y", "x"], config.McpServerMap["files"].Args);
        Assert.Equal("http", config.McpServerMap["docs"].Type);
        Assert.True(config.McpServerMap["docs"].Enabled);
        Assert.Equal(3, config.McpServerMap.Count);
    }

    [Theory]
    [InlineData("\"mcpServers\":{\"Bad Name\":{\"type\":\"stdio\",\"command\":\"x\"}}", "invalid")]
    [InlineData("\"mcpServers\":{\"a\":{\"type\":\"stdio\"}}", "needs a command")]
    [InlineData("\"mcpServers\":{\"a\":{\"type\":\"http\"}}", "absolute http(s) url")]
    [InlineData("\"mcpServers\":{\"a\":{\"type\":\"http\",\"url\":\"ftp://x\"}}", "absolute http(s) url")]
    [InlineData("\"mcpServers\":{\"a\":{\"type\":\"websocket\",\"url\":\"http://x\"}}", "stdio or http")]
    public void Invalid_servers_are_refused_with_a_clear_message(string mcpJson, string expected)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Open(mcpJson));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Saving_an_empty_server_map_removes_every_server()
    {
        FleetConfigStore store = Open("\"mcpServers\":{\"a\":{\"type\":\"stdio\",\"command\":\"x\"}}");
        Assert.Single(store.Current.McpServerMap);

        store.Save(store.Current with { McpServers = new Dictionary<string, FleetMcpServerConfig>() });

        Assert.Empty(store.Current.McpServerMap);
    }
}
