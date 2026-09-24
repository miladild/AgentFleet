using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class ToolRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-tools-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FleetConfigStore _config;

    public ToolRegistryTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new FleetConfigStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(_dir, "fleet.config.json") })
            .Build());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AITool Tool(string name) => AIFunctionFactory.Create(() => "ok", name: name, description: name);

    private FleetToolRegistry Registry(SwappableNameSet? readOnly = null) =>
        new(_config, ["read_file", "run_command"], readOnly ?? new SwappableNameSet(["read_file"]), NullLoggerFactory.Instance);

    [Fact]
    public void A_built_in_tool_switched_off_is_removed_from_the_very_next_request_without_a_restart()
    {
        FleetToolRegistry registry = Registry();
        IList<AITool> requested = [Tool("read_file"), Tool("run_command")];
        Assert.Equal(["read_file", "run_command"], registry.Apply(requested).Select(t => t.Name));

        _config.Save(_config.Current with
        {
            Tools = new Dictionary<string, FleetToolConfig>(_config.Current.Tools) { ["run_command"] = new FleetToolConfig(false) }
        });

        Assert.Equal(["read_file"], registry.Apply(requested).Select(t => t.Name));
    }

    [Fact]
    public void Tools_the_client_declared_are_kept_and_duplicates_dropped()
    {
        FleetToolRegistry registry = Registry();

        IList<AITool> result = registry.Apply([Tool("read_file"), Tool("vscode_open_file"), Tool("vscode_open_file")]);

        Assert.Equal(["read_file", "vscode_open_file"], result.Select(t => t.Name));
    }

    [Fact]
    public async Task Reloading_with_no_servers_is_quick_and_a_repeat_reload_with_the_same_config_does_nothing()
    {
        FleetToolRegistry registry = Registry();

        IReadOnlyList<McpServerStatus> first = await registry.ReloadAsync(default);
        McpToolProvider connected = registry.Mcp;
        IReadOnlyList<McpServerStatus> second = await registry.ReloadAsync(default);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Same(connected, registry.Mcp);
    }

    [Fact]
    public async Task A_server_that_fails_is_reported_with_what_it_printed_and_the_read_only_set_keeps_the_built_ins()
    {
        var readOnly = new SwappableNameSet(["read_file"]);
        FleetToolRegistry registry = Registry(readOnly);
        (string command, string[] args) = FailingCommand();
        _config.Save(_config.Current with
        {
            McpServers = new Dictionary<string, FleetMcpServerConfig> { ["broken"] = new("stdio", command, args) }
        });

        IReadOnlyList<McpServerStatus> statuses = await registry.ReloadAsync(default);

        McpServerStatus broken = Assert.Single(statuses);
        Assert.False(broken.Connected);
        Assert.Contains("missing-api-key-for-test", broken.Error);
        Assert.Empty(registry.Mcp.Tools);
        Assert.True(readOnly.Contains("read_file"));
    }

    [Fact]
    public async Task Testing_a_server_that_will_not_start_says_why_in_its_own_words()
    {
        (string command, string[] args) = FailingCommand();

        McpTestResult result = await McpToolProvider.TestAsync("broken", new FleetMcpServerConfig("stdio", command, args), NullLoggerFactory.Instance, default);

        Assert.False(result.Ok);
        Assert.Empty(result.Tools);
        Assert.Contains("missing-api-key-for-test", result.Error);
    }

    [Fact]
    public void The_read_only_set_can_be_replaced_while_it_is_being_read()
    {
        var set = new SwappableNameSet(["a"]);
        Parallel.For(0, 2000, i =>
        {
            if (i % 2 == 0)
            {
                set.Replace(["a", "b" + i]);
            }
            else
            {
                Assert.True(set.Contains("a"));
                _ = set.Count;
            }
        });
    }

    [Theory]
    [InlineData("hey", false, false, false)]
    [InlineData("how do I parse a date in C#?", false, false, false)]
    [InlineData("remember: we use CommonJS, not ES modules", false, false, true)]
    [InlineData("From now on keep all prices in cents", false, false, true)]
    [InlineData("We decided to use PostgreSQL", false, false, true)]
    [InlineData("hey", true, false, true)]
    [InlineData("hey", false, true, true)]
    public void Record_decision_is_offered_only_where_a_decision_is_likely(string lastUser, bool planMode, bool runner, bool offered) =>
        Assert.Equal(offered, ContextTools.ShouldOffer("record_decision", hasContext: true, planMode, runner, lastUser));

    [Fact]
    public void Without_a_durable_record_neither_context_tool_is_offered_and_other_tools_are_untouched()
    {
        Assert.False(ContextTools.ShouldOffer("record_decision", hasContext: false, planModeOn: true, fromPlanRunner: true, "remember this"));
        Assert.False(ContextTools.ShouldOffer("search_context", hasContext: false, planModeOn: false, fromPlanRunner: false, "hey"));
        Assert.True(ContextTools.ShouldOffer("search_context", hasContext: true, planModeOn: false, fromPlanRunner: false, "hey"));
        Assert.True(ContextTools.ShouldOffer("read_file", hasContext: false, planModeOn: false, fromPlanRunner: false, "hey"));
    }

    private sealed class CapturingClient : IChatClient
    {
        public ChatOptions? Seen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task The_dynamic_client_applies_the_veto_per_request()
    {
        var inner = new CapturingClient();
        var client = new DynamicToolsChatClient(inner, Registry(), (messages, _, name) =>
            ContextTools.ShouldOffer(name, hasContext: true, planModeOn: false, fromPlanRunner: false, messages.Last(m => m.Role == ChatRole.User).Text));
        var options = new ChatOptions { Tools = [Tool("read_file"), Tool("record_decision")] };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hey")], options);
        Assert.Equal(["read_file"], inner.Seen!.Tools!.Select(t => t.Name));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "remember: tabs, not spaces")], options);
        Assert.Equal(["read_file", "record_decision"], inner.Seen!.Tools!.Select(t => t.Name));
    }
    // A "server" that prints a reason to stderr and exits before speaking MCP.
    private static (string Command, string[] Args) FailingCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", "echo missing-api-key-for-test 1>&2 & exit 1"])
            : ("/bin/sh", ["-c", "echo missing-api-key-for-test 1>&2; exit 1"]);
}
