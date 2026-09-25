using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class CustomToolTests
{
    private static FleetCustomToolConfig Tool(string command, params FleetCustomToolParameter[] parameters) =>
        new("Runs something.", command, parameters);

    [Fact]
    public void A_command_line_splits_like_the_web_ui_splits_it()
    {
        Assert.Equal(["dotnet", "test", "{project}", "--filter", "Name~Fast Tests"],
            CustomToolRunner.Tokenize("dotnet test {project} --filter \"Name~Fast Tests\""));
    }

    [Theory]
    [InlineData("Run-Tests", "dotnet test", "name")]
    [InlineData("run_tests", "", "needs a command")]
    [InlineData("run_tests", "{program} test", "must be fixed")]
    [InlineData("run_tests", "dotnet test {project}", "no parameter called project")]
    public void A_bad_tool_is_refused_with_a_reason(string name, string command, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CustomToolRunner.Validate(name, Tool(command)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_parameter_the_command_does_not_use_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CustomToolRunner.Validate("run_tests", Tool("dotnet test", new FleetCustomToolParameter("project"))));
        Assert.Contains("does not use", error.Message);
    }

    [Fact]
    public void Values_fill_whole_arguments_and_an_empty_optional_one_drops_its_argument()
    {
        FleetCustomToolConfig tool = Tool("dotnet test {project} --filter={filter}",
            new FleetCustomToolParameter("project"), new FleetCustomToolParameter("filter", Required: false));

        (string program, IReadOnlyList<string> arguments, _, string? error) = CustomToolRunner.Prepare(tool,
            new Dictionary<string, string?> { ["project"] = "C:\\My Code\\app.csproj; del *" });

        Assert.Null(error);
        Assert.Equal("dotnet", program);
        // One argument, spaces and all: no shell ever sees it.
        Assert.Equal(["test", "C:\\My Code\\app.csproj; del *"], arguments);
    }

    [Fact]
    public void A_missing_required_value_is_an_error_the_model_can_read()
    {
        (_, _, _, string? error) = CustomToolRunner.Prepare(
            Tool("dotnet test {project}", new FleetCustomToolParameter("project", "the test project")), new Dictionary<string, string?>());

        Assert.Contains("project is required (the test project)", error);
    }

    [Fact]
    public void The_model_sees_one_string_per_parameter_and_which_are_required()
    {
        JsonElement schema = CustomToolRunner.Schema(Tool("x {a} {b}",
            new FleetCustomToolParameter("a", "first"), new FleetCustomToolParameter("b", Required: false)));

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("first", schema.GetProperty("properties").GetProperty("a").GetProperty("description").GetString());
        Assert.Equal(["a"], schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public async Task A_command_tool_runs_its_program_with_the_values_as_arguments()
    {
        // dotnet is always on PATH where the tests run.
        var tool = new CustomCommandTool("dotnet_info", new FleetCustomToolConfig(
            "Shows the dotnet version.", "dotnet {flag}", [new FleetCustomToolParameter("flag")], TimeoutSeconds: 60));

        object? result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["flag"] = JsonSerializer.SerializeToElement("--version")
        }));

        string output = Assert.IsType<string>(result);
        Assert.Contains("Exit code: 0", output);
        Assert.Matches(@"\d+\.\d+\.\d+", output);
    }

    [Fact]
    public async Task A_program_that_is_not_installed_says_so()
    {
        CustomToolResult result = await CustomToolRunner.RunAsync(
            Tool("definitely-not-a-real-program-xyz --help"), new Dictionary<string, string?>(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("was not found", result.Output);
    }

    [Fact]
    public async Task A_missing_working_folder_says_so()
    {
        CustomToolResult result = await CustomToolRunner.RunAsync(
            new FleetCustomToolConfig("x", "dotnet --version", WorkingDirectory: Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid())),
            new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Contains("working folder does not exist", result.Output);
    }

    private static FleetConfigStore StoreWith(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return new FleetConfigStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = path }).Build());
    }

    private const string Base = """
        "triageModel": "t", "mode": "conservative", "planModeEnabled": false,
        "nodes": [{ "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "m", "purpose": "", "fallback": true }],
        "tools": {}
        """;

    [Fact]
    public void Command_tools_in_the_config_are_offered_and_read_only_ones_join_the_planning_set()
    {
        FleetConfigStore store = StoreWith("{" + Base + """
            , "customTools": {
                "run_tests": { "description": "Runs the tests.", "command": "dotnet test {project}", "parameters": [{ "name": "project" }] },
                "show_version": { "description": "Shows the version.", "command": "dotnet --version", "readOnly": true },
                "off_tool": { "description": "Off.", "command": "dotnet --info", "enabled": false }
            } }
            """);
        var readOnly = new SwappableNameSet(PlanGate.BuiltInReadOnlyTools);
        var registry = new FleetToolRegistry(store, ["read_file"], readOnly, NullLoggerFactory.Instance);

        registry.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
        IList<AITool> offered = registry.Apply([]);

        Assert.Equal(["run_tests", "show_version"], offered.Select(tool => tool.Name).Order().ToArray());
        Assert.Contains("show_version", readOnly);
        Assert.DoesNotContain("run_tests", readOnly);
        Assert.Equal(3, registry.CustomStatuses.Count);
        Assert.False(registry.CustomStatuses.Single(status => status.Name == "off_tool").Offered);
    }

    [Fact]
    public void A_command_tool_named_like_a_built_in_is_not_offered()
    {
        FleetConfigStore store = StoreWith("{" + Base + """
            , "customTools": { "read_file": { "description": "Mine.", "command": "dotnet --version" } } }
            """);
        var registry = new FleetToolRegistry(store, ["read_file"], new SwappableNameSet([]), NullLoggerFactory.Instance);

        registry.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Empty(registry.Custom);
        Assert.Contains("built-in", registry.CustomStatuses.Single().Problem);
    }

    [Fact]
    public void An_invalid_command_tool_stops_the_config_from_loading()
    {
        var error = Assert.Throws<InvalidOperationException>(() => StoreWith("{" + Base + """
            , "customTools": { "bad": { "description": "x", "command": "dotnet test {project}" } } }
            """));
        Assert.Contains("no parameter called project", error.Message);
    }
}

public sealed class McpImportTests
{
    private static IReadOnlyDictionary<string, FleetMcpServerConfig> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return McpImport.Parse(document.RootElement);
    }

    [Fact]
    public void Vs_code_claude_and_settings_formats_are_all_read()
    {
        Assert.Single(Parse("""{ "servers": { "Docs": { "type": "http", "url": "https://learn.microsoft.com/api/mcp" } } }"""));
        Assert.Single(Parse("""{ "mcpServers": { "fs": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\code"] } } }"""));
        Assert.Single(Parse("""
            {
              // VS Code settings.json allows comments
              "editor.fontSize": 14,
              "mcp": { "servers": { "time": { "command": "uvx", "args": ["mcp-server-time"] }, } },
            }
            """));
    }

    [Fact]
    public void Names_are_tidied_and_types_worked_out()
    {
        IReadOnlyDictionary<string, FleetMcpServerConfig> servers = Parse("""
            { "mcpServers": {
                "My Server!": { "command": "node", "args": ["server.js"] },
                "remote": { "url": "https://example.com/mcp" },
                "sse-one": { "type": "sse", "url": "https://example.com/sse" },
                "broken": { "type": "stdio" }
            } }
            """);

        Assert.Equal(["my-server", "remote", "sse-one"], servers.Keys.Order().ToArray());
        Assert.Equal("stdio", servers["my-server"].Type);
        Assert.Equal("http", servers["remote"].Type);
        Assert.Equal("http", servers["sse-one"].Type);
    }

    [Fact]
    public void A_secret_written_into_an_entry_is_noticed_but_an_env_reference_is_not()
    {
        IReadOnlyDictionary<string, FleetMcpServerConfig> servers = Parse("""
            { "mcpServers": {
                "inline": { "command": "npx", "env": { "GITHUB_TOKEN": "ghp_abcdefghijklmnopqrstuvwxyz0123456789" } },
                "reference": { "command": "npx", "env": { "GITHUB_TOKEN": "${env:GITHUB_TOKEN}" } },
                "in-args": { "command": "npx", "args": ["--token", "ghp_abcdefghijklmnopqrstuvwxyz0123456789"] }
            } }
            """);

        Assert.True(McpImport.HasInlineSecret(servers["inline"]));
        Assert.False(McpImport.HasInlineSecret(servers["reference"]));
        Assert.True(McpImport.HasInlineSecret(servers["in-args"]));
    }

    [Fact]
    public void Only_the_known_config_files_can_be_imported_from()
    {
        FleetConfig config = new("t", "conservative", false, [], new Dictionary<string, FleetToolConfig>());
        Assert.Throws<ArgumentException>(() => McpImport.Take(Path.Combine(Path.GetTempPath(), "anything.json"), ["x"], config));
    }
}

public sealed class ProjectToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-project-{Guid.NewGuid():N}");

    public ProjectToolTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "deep", "deeper"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(_root, "package.json"), """{ "name": "demo", "scripts": { "dev": "next dev", "test": "node --test" } }""");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Demo\n\nA tiny demo project.\n");
        File.WriteAllText(Path.Combine(_root, "src", "index.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(_root, "src", "deep", "deeper", "x.js"), "");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_overview_shows_the_tree_how_to_build_and_the_readme_but_not_dependencies()
    {
        string overview = ProjectTools.ProjectOverview(_root, 2);

        Assert.Contains("src/", overview);
        Assert.Contains("  index.js", overview);
        Assert.Contains("deep/ (0 files, 1 folders)", overview);
        Assert.DoesNotContain("left-pad", overview);
        Assert.Contains("npm run <script>; scripts: dev, test", overview);
        Assert.Contains("A tiny demo project.", overview);
    }

    [Fact]
    public void Move_renames_and_will_not_overwrite_unless_asked()
    {
        string from = Path.Combine(_root, "src", "index.js");
        string to = Path.Combine(_root, "lib", "main.js");

        Assert.StartsWith("Moved", ProjectTools.MoveFile(from, to, null));
        Assert.True(File.Exists(to));

        File.WriteAllText(from, "again");
        Assert.Contains("already exists", ProjectTools.MoveFile(from, to, null));
        Assert.StartsWith("Moved", ProjectTools.MoveFile(from, to, true));
        Assert.Equal("again", File.ReadAllText(to));
    }

    [Fact]
    public void Delete_removes_a_file_or_an_empty_folder_but_not_a_full_one()
    {
        Assert.StartsWith("Deleted", ProjectTools.DeleteFile(Path.Combine(_root, "README.md")));
        Assert.Contains("not empty", ProjectTools.DeleteFile(Path.Combine(_root, "src")));
        Assert.True(Directory.Exists(Path.Combine(_root, "src")));
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Assert.StartsWith("Deleted the empty folder", ProjectTools.DeleteFile(Path.Combine(_root, "empty")));
    }
}
