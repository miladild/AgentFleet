using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class FleetConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-cfg-tests-" + Guid.NewGuid().ToString("N"));

    public FleetConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ConfigPath => Path.Combine(_dir, "fleet.config.json");

    private FleetConfigStore Open(Action<string, string>? replaceFile = null) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = ConfigPath })
            .Build(), replaceFile);

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
        Assert.Equal("off", node.Caveman);
        Assert.Equal("off", node.Ponytail);
        Assert.True(File.Exists(ConfigPath));
        Assert.DoesNotContain("192.168.", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Worker_workspace_round_trips_with_a_pinned_host_key()
    {
        WriteConfig("""[{"name":"hub","url":"http://127.0.0.1:11434/v1","model":"m","purpose":"local","tier":"heavy","fallback":true},{"name":"worker","url":"http://192.0.2.30:11434/v1","model":"m","purpose":"worker","tier":"standard","workspace":{"host":"192.0.2.30","user":"agentfleet","keyPath":"C:/fleet-test/worker-key","hostKey":"SHA256:known","root":"/home/agentfleet/workspaces","platform":"linux","port":22}}]""");

        FleetConfig config = Open().Current;
        FleetWorkerWorkspaceConfig workspace = Assert.Single(config.Nodes, node => node.Name == "worker").Workspace!;
        Assert.Equal("agentfleet", workspace.User);
        Assert.Equal("SHA256:known", workspace.HostKey);

        FleetConfig reopened = Open().Current;
        Assert.Equal(workspace, Assert.Single(reopened.Nodes, node => node.Name == "worker").Workspace);
    }

    [Fact]
    public void The_hub_cannot_be_configured_as_a_worker_workspace()
    {
        WriteConfig("""[{"name":"hub","url":"http://127.0.0.1:11434/v1","model":"m","purpose":"local","tier":"heavy","fallback":true,"workspace":{"host":"127.0.0.1","user":"agentfleet","keyPath":"C:/keys/worker","hostKey":"SHA256:known","root":"/home/agentfleet/workspaces","platform":"linux","port":22}}]""");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Open());
        Assert.Contains("cannot host a worker plan workspace", exception.Message);
    }

    [Fact]
    public void Worker_workspace_requires_a_pinned_host_key()
    {
        WriteConfig("""[{"name":"hub","url":"http://127.0.0.1:11434/v1","model":"m","purpose":"local","tier":"heavy","fallback":true},{"name":"worker","url":"http://192.0.2.30:11434/v1","model":"m","purpose":"worker","tier":"standard","workspace":{"host":"192.0.2.30","user":"agentfleet","keyPath":"C:/fleet-test/worker-key","root":"relative/workspaces","platform":"linux","port":22}}]""");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Open());
        Assert.Contains("pinned SHA256 host key", exception.Message);
    }

    [Fact]
    public void Worker_workspace_root_must_be_absolute_for_its_platform()
    {
        WriteConfig("""[{"name":"hub","url":"http://127.0.0.1:11434/v1","model":"m","purpose":"local","tier":"heavy","fallback":true},{"name":"worker","url":"http://192.0.2.30:11434/v1","model":"m","purpose":"worker","tier":"standard","workspace":{"host":"192.0.2.30","user":"agentfleet","keyPath":"C:/fleet-test/worker-key","hostKey":"SHA256:known","root":"relative/workspaces","platform":"linux","port":22}}]""");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Open());
        Assert.Contains("absolute linux path", exception.Message);
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
    [InlineData("http://192.0.2.20:11434", "http://192.0.2.20:11434/v1")]
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

        var exception = Assert.Throws<InvalidOperationException>(() => Open());

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
    public void Machine_style_levels_normalize_persist_and_reach_the_node_prompt()
    {
        WriteConfig("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":"","caveman":"FULL","ponytail":"lite"}]""");

        FleetConfigStore store = Open();
        FleetNodeConfig savedNode = Assert.Single(store.Current.Nodes);
        FleetNodeDefinition node = Assert.Single(FleetOptions.Load(new ConfigurationBuilder().Build(), store).Nodes);
        string prompt = Assert.IsType<string>(AgentStylePrompt.Build(node));

        Assert.Equal("full", savedNode.Caveman);
        Assert.Equal("lite", savedNode.Ponytail);
        Assert.Contains("Caveman full", prompt);
        Assert.Contains("Ponytail lite", prompt);

        FleetConfig reopened = Open().Current;
        Assert.Equal("full", Assert.Single(reopened.Nodes).Caveman);
        Assert.Equal("lite", Assert.Single(reopened.Nodes).Ponytail);
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
    [InlineData("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":"","caveman":"brief"}]""", "caveman")]
    [InlineData("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":"","ponytail":"maximum"}]""", "ponytail")]
    [InlineData("""[{"name":"a","url":"http://h:1/v1","model":"m","purpose":"","fallback":true},{"name":"b","url":"http://h:2/v1","model":"m","purpose":"","fallback":true}]""", "Exactly one node")]
    [InlineData("""[{"name":"eyes","url":"http://h:1/v1","model":"m","purpose":"","vision":true}]""", "at least one text node")]
    [InlineData("[]", "at least one text node")]
    public void Invalid_configs_are_refused_with_a_clear_message(string nodesJson, string expected)
    {
        WriteConfig(nodesJson);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Open());

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
                new FleetNodeConfig("quick", "http://192.0.2.10:11434/v1", "m2", "y", FleetTiers.Light),
                new FleetNodeConfig("eyes", "http://192.0.2.11:11434/v1", "m3", "z", Vision: true)
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
    public void The_version_changes_when_the_saved_bytes_change()
    {
        FleetConfigStore store = Open();
        string before = store.Version;

        File.AppendAllText(ConfigPath, "\n");

        Assert.Equal(16, before.Length);
        Assert.NotEqual(before, store.Version);
    }

    [Fact]
    public void The_version_can_be_read_while_another_process_has_the_file_open_for_writing()
    {
        // Another process can hold the file open for a moment after a save, for example a virus scanner. Reading the
        // version must not fail while that process has the file open for writing.
        FleetConfigStore store = Open();
        string before = store.Version;

        using var otherProcess = new FileStream(ConfigPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(before, store.Version);
    }

    [Fact]
    public void A_stale_version_is_rejected_without_changing_the_file()
    {
        FleetConfigStore store = Open();
        byte[] before = File.ReadAllBytes(ConfigPath);
        string currentVersion = store.Version;

        bool saved = store.TrySave(store.Current with { Mode = "aggressive" }, "stale-version", out _, out string actualVersion);

        Assert.False(saved);
        Assert.Equal(currentVersion, actualVersion);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        IResult conflict = FleetConfigResponses.VersionConflict(actualVersion);
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(conflict).StatusCode);
        JsonElement body = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(conflict).Value);
        Assert.Equal(FleetConfigResponses.VersionConflictReason, body.GetProperty("reason").GetString());
        Assert.Equal(currentVersion, body.GetProperty("version").GetString());
    }

    [Fact]
    public void A_save_without_a_version_still_succeeds()
    {
        FleetConfigStore store = Open();

        bool saved = store.TrySave(store.Current with { Mode = "aggressive" }, null, out FleetConfig result, out string version);

        Assert.True(saved);
        Assert.Equal("aggressive", result.Mode);
        Assert.Equal(version, store.Version);
    }

    [Fact]
    public void A_save_keeps_a_backup_and_only_the_newest_ten_and_removes_its_temporary_file()
    {
        FleetConfigStore store = Open();
        byte[] previous = File.ReadAllBytes(ConfigPath);
        for (int index = 0; index < 11; index++)
        {
            File.WriteAllText(Path.Combine(_dir, $"fleet.config.json.20000101-{index:D6}.bak"), "old");
        }

        store.Save(store.Current with { Mode = "aggressive" });

        string[] backups = Directory.GetFiles(_dir, "fleet.config.json.*.bak");
        Assert.Equal(10, backups.Length);
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(previous));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_failed_atomic_replace_keeps_the_old_file_and_cleans_up_the_temporary_file()
    {
        FleetConfigStore store = Open((_, _) => throw new IOException("simulated replace failure"));
        byte[] previous = File.ReadAllBytes(ConfigPath);

        Assert.Throws<IOException>(() => store.Save(store.Current with { Mode = "aggressive" }));

        Assert.Equal(previous, File.ReadAllBytes(ConfigPath));
        Assert.Equal("conservative", store.Current.Mode);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Context_patch_changes_only_the_requested_node_field()
    {
        FleetConfigStore store = Open();
        FleetConfig config = store.Current with
        {
            Nodes =
            [
                new FleetNodeConfig("main", "http://main.example.test/v1", "model-x", "main", FleetTiers.Heavy, Fallback: true),
                new FleetNodeConfig("worker", "http://worker.example.test/v1", "model-y", "worker", FleetTiers.Standard)
            ]
        };
        store.Save(config);
        JsonNode expected = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        expected["nodes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "worker")!["contextLength"] = 16384;

        FleetContextPatchResult result = store.PatchNodeContext("worker", 16384, store.Version);
        JsonNode actual = JsonNode.Parse(File.ReadAllText(ConfigPath))!;

        Assert.Equal(FleetContextPatchStatus.Updated, result.Status);
        Assert.True(JsonNode.DeepEquals(expected, actual));
    }

    [Fact]
    public void Context_patch_reports_missing_nodes_conflicts_and_out_of_range_values()
    {
        FleetConfigStore store = Open();

        FleetContextPatchResult missing = store.PatchNodeContext("missing", 16384, null);
        FleetContextPatchResult conflict = store.PatchNodeContext("hub", 16384, "stale-version");
        FleetContextPatchResult invalid = store.PatchNodeContext("hub", 131073, store.Version);

        Assert.Equal(FleetContextPatchStatus.NotFound, missing.Status);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(FleetConfigResponses.ContextPatchFailure(missing)).StatusCode);
        Assert.Equal(FleetContextPatchStatus.Conflict, conflict.Status);
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(FleetConfigResponses.ContextPatchFailure(conflict)).StatusCode);
        Assert.Equal(FleetContextPatchStatus.InvalidContextLength, invalid.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(FleetConfigResponses.ContextPatchFailure(invalid)).StatusCode);
    }

    [Fact]
    public void A_tool_missing_from_the_file_counts_as_enabled()
    {
        Assert.True(Open().Current.IsToolEnabled("something_added_later"));
    }

    [Fact]
    public void Chat_approvals_default_to_risky_so_commands_ask_even_without_a_block()
    {
        FleetConfig config = Open().Current;

        Assert.Null(config.ChatApprovals);
        Assert.True(config.ChatApprovalSettings.Enabled);
        Assert.Equal("risky", config.ChatApprovalSettings.Mode);
        Assert.Equal(240, config.ChatApprovalSettings.TimeoutSeconds);
    }

    [Theory]
    [InlineData(5, 30)]
    [InlineData(240, 240)]
    [InlineData(900, 280)]
    public void The_chat_approval_wait_is_kept_between_30_and_280_seconds(int written, int expected)
    {
        File.WriteAllText(
            ConfigPath,
            "{\"triageModel\":\"t\",\"mode\":\"conservative\",\"planModeEnabled\":false,\"nodes\":[{\"name\":\"hub\",\"url\":\"http://127.0.0.1:11434/v1\",\"model\":\"m\",\"purpose\":\"local\",\"tier\":\"heavy\",\"fallback\":true}],\"tools\":{},\"chatApprovals\":{\"enabled\":true,\"timeoutSeconds\":" + written + "}}");

        FleetChatApprovalConfig settings = Open().Current.ChatApprovalSettings;

        Assert.True(settings.Enabled);
        Assert.Equal(expected, settings.TimeoutSeconds);
    }

    [Fact]
    public void A_saved_chat_approval_setting_is_kept_on_reopen()
    {
        FleetConfigStore store = Open();

        store.Save(store.Current with { ChatApprovals = new FleetChatApprovalConfig(Enabled: true, TimeoutSeconds: 120) });

        Assert.Equal(new FleetChatApprovalConfig(Enabled: true, TimeoutSeconds: 120, Mode: "all"), Open().Current.ChatApprovals);
    }

    [Theory]
    [InlineData("{\"enabled\":true}", "all")]
    [InlineData("{\"enabled\":false}", "off")]
    [InlineData("{}", "off")]
    [InlineData("{\"mode\":\"risky\",\"enabled\":false}", "risky")]
    [InlineData("{\"mode\":\"off\",\"enabled\":true}", "off")]
    [InlineData("{\"mode\":\"unknown\",\"enabled\":false}", "all")]
    [InlineData("{\"timeoutSeconds\":60}", "off")]
    public void Chat_approval_levels_resolve_without_weakening_unknown_values(string block, string expected)
    {
        var store = Open();
        var document = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        document["chatApprovals"] = JsonNode.Parse(block);
        File.WriteAllText(ConfigPath, document.ToJsonString());
        var settings = Open().Current.ChatApprovalSettings;
        Assert.Equal(expected, settings.Mode);
        Assert.Equal(expected != "off", settings.Enabled);
    }

    [Theory]
    [InlineData("off", true)]
    [InlineData("risky", false)]
    [InlineData("all", false)]
    public void Saving_an_approval_level_writes_consistent_legacy_enabled(string mode, bool enabled)
    {
        var store = Open();
        store.Save(store.Current with { ChatApprovals = new FleetChatApprovalConfig(enabled, Mode: mode) });
        var block = JsonNode.Parse(File.ReadAllText(ConfigPath))!["chatApprovals"]!;
        Assert.Equal(mode, block["mode"]!.GetValue<string>());
        Assert.Equal(mode != "off", block["enabled"]!.GetValue<bool>());
        Assert.Equal(mode, Open().Current.ChatApprovalSettings.Mode);
    }

    [Fact]
    public void An_unknown_approval_level_is_a_bad_request_and_cannot_change_the_file()
    {
        // The save endpoint turns this exception into a 400 with its message; the store must leave the file as it was.
        var store = Open();
        byte[] before = File.ReadAllBytes(ConfigPath);
        var error = Assert.Throws<InvalidOperationException>(() => store.Save(store.Current with { ChatApprovals = new FleetChatApprovalConfig(Mode: "unknown") }));
        Assert.Equal(FleetChatApprovalConfig.InvalidModeMessage, error.Message);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public void The_settings_used_by_config_responses_serialize_the_resolved_missing_block_level()
    {
        var config = Open().Current;
        Assert.Null(config.ChatApprovals);
        var json = JsonSerializer.SerializeToNode(config.ChatApprovalSettings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal("risky", json["mode"]!.GetValue<string>());
        Assert.True(json["enabled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("Risky", "risky")]
    [InlineData("OFF", "off")]
    [InlineData("All", "all")]
    public void Chat_approval_levels_in_capitals_are_read_as_their_level(string mode, string expected)
    {
        Open();
        var document = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        document["chatApprovals"] = JsonNode.Parse($"{{\"mode\":\"{mode}\"}}");
        File.WriteAllText(ConfigPath, document.ToJsonString());
        var settings = Open().Current.ChatApprovalSettings;
        Assert.Equal(expected, settings.Mode);
        Assert.Equal(expected != "off", settings.Enabled);
    }

    [Fact]
    public void Saving_only_the_wait_keeps_the_level_the_file_has()
    {
        var store = Open();
        store.Save(store.Current with { ChatApprovals = new FleetChatApprovalConfig(Mode: "risky") });
        FleetChatApprovalConfig current = store.Current.ChatApprovalSettings;
        store.Save(store.Current with { ChatApprovals = current with { TimeoutSeconds = 60 } });
        FleetChatApprovalConfig reread = Open().Current.ChatApprovalSettings;
        Assert.Equal("risky", reread.Mode);
        Assert.Equal(60, reread.TimeoutSeconds);
    }

    [Theory]
    [InlineData("Risky", "risky")]
    [InlineData("OFF", "off")]
    [InlineData("All", "all")]
    public void Saving_a_level_in_capitals_is_accepted_and_written_in_lower_case(string mode, string expected)
    {
        var store = Open();
        store.Save(store.Current with { ChatApprovals = new FleetChatApprovalConfig(Mode: mode) });
        var block = JsonNode.Parse(File.ReadAllText(ConfigPath))!["chatApprovals"]!;
        Assert.Equal(expected, block["mode"]!.GetValue<string>());
        Assert.Equal(expected != "off", block["enabled"]!.GetValue<bool>());
    }
}
