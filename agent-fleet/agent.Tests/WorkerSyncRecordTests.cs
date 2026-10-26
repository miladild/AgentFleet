using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

// The record of what each worker held at its last sync survives a backend restart, so a worker file the hub no longer has
// can still be told apart from unsynced work after a deploy.
public sealed class WorkerSyncRecordTests : PlanTestBase
{
    private const string PlanIdForTests = "0123456789abcdef0123456789abcdef";
    private const string OtherPlanIdForTests = "fedcba9876543210fedcba9876543210";

    private WorkerWorkspaceManager Manager(string? recordDirectory)
    {
        string configDirectory = Path.Combine(PlansDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FLEET_CONFIG_PATH"] = Path.Combine(configDirectory, "fleet.config.json")
        }).Build();
        FleetOptions options = FleetOptions.Load(configuration, new FleetConfigStore(configuration));
        return new WorkerWorkspaceManager(options, NullLogger.Instance, TimeSpan.FromSeconds(1), recordDirectory);
    }

    private static Dictionary<string, string> Hashes(params (string Path, string Hash)[] files) =>
        files.ToDictionary(file => file.Path, file => file.Hash, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<(string Path, string Hash)> Ordered(IReadOnlyDictionary<string, string> hashes) =>
        hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value));

    private string RecordPath(string planId, string machine) =>
        Path.Combine(PlansDirectory, WorkerWorkspaceManager.SyncRecordFileName(planId, machine));

    [Fact]
    public void A_recorded_sync_is_returned_by_a_manager_started_later_over_the_same_folder_for_that_plan_and_machine_only()
    {
        Dictionary<string, string> hashes = Hashes(("src/a.cs", "AB12"), ("src/b.cs", "CD34"));
        Manager(PlansDirectory).RecordSynced(PlanIdForTests, "worker-a", hashes);

        IReadOnlyDictionary<string, string>? restarted = Manager(PlansDirectory).LastSynced(PlanIdForTests, "worker-a");

        Assert.NotNull(restarted);
        Assert.Equal(Ordered(hashes), Ordered(restarted));
        Assert.Null(Manager(PlansDirectory).LastSynced(PlanIdForTests, "worker-b"));
        Assert.Null(Manager(PlansDirectory).LastSynced(OtherPlanIdForTests, "worker-a"));
    }

    [Fact]
    public void The_record_is_a_json_object_named_after_the_plan_and_machine_and_no_plan_listing_sees_it()
    {
        FleetPlanStore store = Store;
        PlanRecord plan = NewPlan(Step("one"));
        Manager(PlansDirectory).RecordSynced(plan.Id, "worker-a", Hashes(("src/a.cs", "AB12")));

        string path = RecordPath(plan.Id, "worker-a");
        Assert.Equal(plan.Id + ".worker-a.synced", Path.GetFileName(path));
        Assert.True(File.Exists(path));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal("AB12", document.RootElement.GetProperty("src/a.cs").GetString());

        Assert.Equal([plan.Id], store.List().Select(summary => summary.Id));
        Assert.Equal([plan.Id], store.ListRecords().Select(record => record.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"src/a.cs\": 5}")]
    public void A_missing_empty_or_corrupt_record_means_no_record_and_never_throws(string? content)
    {
        if (content is not null) File.WriteAllText(RecordPath(PlanIdForTests, "worker-a"), content);

        IReadOnlyDictionary<string, string>? read = Manager(PlansDirectory).LastSynced(PlanIdForTests, "worker-a");

        Assert.Null(read);
    }

    [Theory]
    [InlineData("..\\x")]
    [InlineData("a/b")]
    [InlineData("CON")]
    [InlineData("worker a")]
    public void A_machine_name_with_characters_outside_a_file_name_round_trips_and_stays_inside_the_folder(string machine)
    {
        Dictionary<string, string> hashes = Hashes(("src/a.cs", "AB12"));
        Manager(PlansDirectory).RecordSynced(PlanIdForTests, machine, hashes);

        IReadOnlyDictionary<string, string>? read = Manager(PlansDirectory).LastSynced(PlanIdForTests, machine);

        Assert.NotNull(read);
        Assert.Equal(Ordered(hashes), Ordered(read));
        string[] written = Directory.EnumerateFiles(PlansDirectory, "*.synced", SearchOption.AllDirectories).ToArray();
        Assert.Single(written);
        Assert.Equal(PlansDirectory, Path.GetDirectoryName(written[0]));
    }

    [Fact]
    public void Two_machine_names_that_sanitise_alike_keep_separate_records()
    {
        Manager(PlansDirectory).RecordSynced(PlanIdForTests, "a/b", Hashes(("src/a.cs", "AAAA")));
        Manager(PlansDirectory).RecordSynced(PlanIdForTests, "a_b", Hashes(("src/a.cs", "BBBB")));

        Assert.Equal("AAAA", Manager(PlansDirectory).LastSynced(PlanIdForTests, "a/b")!["src/a.cs"]);
        Assert.Equal("BBBB", Manager(PlansDirectory).LastSynced(PlanIdForTests, "a_b")!["src/a.cs"]);
    }

    [Theory]
    [InlineData("not-a-plan-id")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("../0123456789abcdef0123456789abcdef")]
    public void A_plan_id_that_is_not_32_lower_case_hex_is_never_written_or_read(string planId)
    {
        Manager(PlansDirectory).RecordSynced(planId, "worker-a", Hashes(("src/a.cs", "AB12")));

        Assert.Empty(Directory.EnumerateFiles(PlansDirectory, "*.synced", SearchOption.AllDirectories));
        Assert.Null(Manager(PlansDirectory).LastSynced(planId, "worker-a"));
    }

    [Fact]
    public void A_record_made_in_this_run_is_returned_only_for_its_own_plan_and_machine()
    {
        var manager = Manager(PlansDirectory);
        manager.RecordSynced(PlanIdForTests, "worker-a", Hashes(("src/a.cs", "AB12")));

        Assert.Equal("AB12", manager.LastSynced(PlanIdForTests, "worker-a")!["src/a.cs"]);
        Assert.Null(manager.LastSynced(PlanIdForTests, "worker-b"));
        Assert.Null(manager.LastSynced(new string('b', 32), "worker-a"));
    }

    [Fact]
    public void Without_a_folder_nothing_is_written_and_a_manager_started_later_has_no_record()
    {
        var manager = Manager(null);
        manager.RecordSynced(PlanIdForTests, "worker-a", Hashes(("src/a.cs", "AB12")));

        Assert.Equal("AB12", manager.LastSynced(PlanIdForTests, "worker-a")!["src/a.cs"]);
        Assert.Null(Manager(null).LastSynced(PlanIdForTests, "worker-a"));
        Assert.Empty(Directory.EnumerateFiles(PlansDirectory, "*.synced", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_record_read_after_a_restart_lets_a_worker_file_the_hub_dropped_be_removed_when_nobody_touched_it()
    {
        Manager(PlansDirectory).RecordSynced(PlanIdForTests, "worker-a", Hashes(("debug/stray.txt", "AB12")));

        IReadOnlyDictionary<string, string>? synced = Manager(PlansDirectory).LastSynced(PlanIdForTests, "worker-a");

        Assert.Equal(WorkerWorkspaceSession.ExistingWorkerFile.Remove,
            WorkerWorkspaceSession.JudgeExistingWorkerFile("AB12", null, synced!.GetValueOrDefault("debug/stray.txt")));
        Assert.Equal(WorkerWorkspaceSession.ExistingWorkerFile.Refuse,
            WorkerWorkspaceSession.JudgeExistingWorkerFile("EDITED", null, synced.GetValueOrDefault("debug/stray.txt")));
    }
}
