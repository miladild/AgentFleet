using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// Accepted test files are frozen for the rest of a plan: the plan keeps their hashes, the worker refuses edits to them,
/// a restore puts back a changed or deleted one from the hub copy, and the implementation step must pass them.
/// </summary>
public sealed class FrozenTestFilesTests : PlanTestBase
{
    private const string FixedMessage = "This test file is fixed for this plan; change the code, not the test. If you believe the test is wrong, call dispute_test with the test and the reason.";
    private const string HubCopy = "hub copy\n";

    private sealed class MemoryFiles : IWorkerWorkspaceFileAccess
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Writes { get; } = [];
        public bool Exists(string path) => Files.ContainsKey(path);
        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Files[path]);
        public void CreateDirectoryTree(string path) { }
        public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            Writes.Add(path);
            Files[path] = content;
            return Task.CompletedTask;
        }
    }

    private static readonly PlanFrozenTestFile Frozen = new("tests/LimitTests.cs", Sha256Hex(HubCopy));

    private static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static WorkerWorkspaceSession FrozenSession(MemoryFiles files, string hubRoot, string platform = "linux",
        params PlanFrozenTestFile[] frozen) => new("worker-test",
            new FleetWorkerWorkspaceConfig("worker.example.test", "fleet-test", "/tmp/fleet-test/key", "SHA256:synthetic-host-key",
                "/tmp/fleet-test/workspaces", platform),
            hubRoot, NullLogger.Instance, TimeSpan.FromSeconds(1), fileAccess: files, frozenFiles: frozen);

    private static string NewHub()
    {
        string hub = Path.Combine(Path.GetTempPath(), "fleet-frozen-hub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(hub, "tests"));
        File.WriteAllText(Path.Combine(hub, "tests", "LimitTests.cs"), HubCopy);
        return hub;
    }

    [Fact]
    public void Frozen_test_sets_are_saved_and_reloaded_with_the_plan()
    {
        PlanRecord plan = NewPlan(Step("tests", verify: "dotnet test"));
        Store.AddFrozenTests(plan.Id, new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile("tests/LimitTests.cs", "ABCDEF")]));

        var restarted = new FleetPlanStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = PlansDirectory })
            .Build());
        PlanFrozenTestSet set = Assert.Single(restarted.Get(plan.Id)!.FrozenTests!);
        Assert.Equal(1, set.StepId);
        Assert.Equal("dotnet test", set.Command);
        PlanFrozenTestFile file = Assert.Single(set.Files);
        Assert.Equal("tests/LimitTests.cs", file.Path);
        Assert.Equal("ABCDEF", file.Sha256);
    }

    [Fact]
    public void Adding_a_set_for_a_step_that_has_one_replaces_it()
    {
        PlanRecord plan = NewPlan(Step("tests", verify: "dotnet test"));
        Store.AddFrozenTests(plan.Id, new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile("tests/A.cs", "1")]));
        Store.AddFrozenTests(plan.Id, new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile("tests/B.cs", "2")]));

        PlanFrozenTestSet set = Assert.Single(Store.Get(plan.Id)!.FrozenTests!);
        Assert.Equal("tests/B.cs", Assert.Single(set.Files).Path);
    }

    [Fact]
    public void A_plan_saved_before_frozen_test_sets_existed_still_loads()
    {
        PlanRecord plan = NewPlan(Step("one"));
        string file = Path.Combine(PlansDirectory, plan.Id + ".json");
        JsonObject json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json.Remove("frozenTests");
        File.WriteAllText(file, json.ToJsonString());

        PlanRecord loaded = Store.Get(plan.Id)!;
        Assert.Null(loaded.FrozenTests);
        Assert.Equal(plan.Title, loaded.Title);
    }

    [Fact]
    public void A_later_step_is_given_every_earlier_frozen_file_in_order()
    {
        PlanRecord plan = NewPlan(Step("first tests"), Step("second tests"));
        plan = plan with
        {
            FrozenTests =
            [
                new PlanFrozenTestSet(1, "dotnet test", [new PlanFrozenTestFile("tests/A.cs", "1")]),
                new PlanFrozenTestSet(2, "dotnet test", [new PlanFrozenTestFile("tests/B.cs", "2")]),
            ]
        };

        Assert.Equal(["tests/A.cs", "tests/B.cs"], WorkerWorkspaceManager.FrozenFilesOf(plan).Select(file => file.Path));
        Assert.Empty(WorkerWorkspaceManager.FrozenFilesOf(NewPlan(Step("plain"))));
    }

    [Fact]
    public async Task Write_edit_move_and_delete_are_refused_on_a_frozen_test_file()
    {
        var files = new MemoryFiles();
        using WorkerWorkspaceSession session = FrozenSession(files, Path.GetTempPath(), frozen: Frozen);
        string remote = session.RemoteRoot + "/tests/LimitTests.cs";
        files.Files[remote] = "original\n";

        Assert.Equal(FixedMessage, await session.WriteFileAsync("tests/LimitTests.cs", "changed\n", default));
        Assert.Equal(FixedMessage, await session.EditFileAsync("tests/LimitTests.cs", "original", "changed", null, default));
        Assert.Equal(FixedMessage, session.MoveFile("tests/LimitTests.cs", "tests/Moved.cs", null));
        Assert.Equal(FixedMessage, session.MoveFile("src/Limit.cs", "tests/LimitTests.cs", true));
        Assert.Equal(FixedMessage, session.DeleteFile("tests/LimitTests.cs"));
        Assert.Empty(files.Writes);
        Assert.Equal("original\n", files.Files[remote]);
    }

    [Theory]
    [InlineData("./tests/LimitTests.cs")]
    [InlineData("tests\\LimitTests.cs")]
    [InlineData("tests//LimitTests.cs")]
    [InlineData("tests/nested/../LimitTests.cs")]
    public async Task Other_spellings_of_a_frozen_path_are_refused(string path)
    {
        var files = new MemoryFiles();
        using WorkerWorkspaceSession session = FrozenSession(files, Path.GetTempPath(), frozen: Frozen);

        Assert.Equal(FixedMessage, await session.WriteFileAsync(path, "changed\n", default));
        Assert.Empty(files.Writes);
    }

    [Fact]
    public async Task A_windows_worker_refuses_a_frozen_path_written_in_another_case()
    {
        var files = new MemoryFiles();
        using WorkerWorkspaceSession session = FrozenSession(files, Path.GetTempPath(), "windows", Frozen);

        Assert.Equal(FixedMessage, await session.WriteFileAsync("TESTS/limittests.CS", "changed\n", default));
        Assert.Empty(files.Writes);
    }

    [Fact]
    public async Task A_linux_worker_treats_another_case_as_another_file()
    {
        var files = new MemoryFiles();
        using WorkerWorkspaceSession session = FrozenSession(files, Path.GetTempPath(), "linux", Frozen);

        string result = await session.WriteFileAsync("TESTS/limittests.CS", "other\n", default);

        Assert.NotEqual(FixedMessage, result);
        Assert.Single(files.Writes);
    }

    [Fact]
    public async Task A_new_test_file_is_not_refused_when_only_other_files_are_frozen()
    {
        var files = new MemoryFiles();
        using WorkerWorkspaceSession session = FrozenSession(files, Path.GetTempPath(), frozen: Frozen);

        string result = await session.WriteFileAsync("tests/NewTests.cs", "new tests\n", default);

        Assert.NotEqual(FixedMessage, result);
        Assert.Equal([session.RemoteRoot + "/tests/NewTests.cs"], files.Writes);
    }

    [Fact]
    public async Task A_frozen_file_changed_on_the_worker_is_put_back_from_the_hub_copy()
    {
        string hub = NewHub();
        try
        {
            var files = new MemoryFiles();
            using WorkerWorkspaceSession session = FrozenSession(files, hub, frozen: Frozen);
            string remote = session.RemoteRoot + "/tests/LimitTests.cs";
            files.Files[remote] = "worker edit\n";

            IReadOnlyList<string> restored = await session.RestoreFrozenTestsAsync(default);

            Assert.Equal(["tests/LimitTests.cs"], restored);
            Assert.Equal(HubCopy, files.Files[remote]);
            Assert.Empty(await session.RestoreFrozenTestsAsync(default));
        }
        finally { Directory.Delete(hub, recursive: true); }
    }

    [Fact]
    public async Task A_frozen_file_deleted_on_the_worker_is_restored()
    {
        string hub = NewHub();
        try
        {
            var files = new MemoryFiles();
            using WorkerWorkspaceSession session = FrozenSession(files, hub, frozen: Frozen);
            string remote = session.RemoteRoot + "/tests/LimitTests.cs";

            IReadOnlyList<string> restored = await session.RestoreFrozenTestsAsync(default);

            Assert.Equal(["tests/LimitTests.cs"], restored);
            Assert.Equal(HubCopy, files.Files[remote]);
        }
        finally { Directory.Delete(hub, recursive: true); }
    }

    [Fact]
    public async Task A_frozen_file_that_still_matches_is_left_alone()
    {
        string hub = NewHub();
        try
        {
            var files = new MemoryFiles();
            using WorkerWorkspaceSession session = FrozenSession(files, hub, frozen: Frozen);
            files.Files[session.RemoteRoot + "/tests/LimitTests.cs"] = HubCopy;

            Assert.Empty(await session.RestoreFrozenTestsAsync(default));
            Assert.Empty(files.Writes);
        }
        finally { Directory.Delete(hub, recursive: true); }
    }

    [Fact]
    public async Task A_session_without_frozen_files_restores_nothing()
    {
        var files = new MemoryFiles();
        using WorkerWorkspaceSession session = FrozenSession(files, Path.GetTempPath());

        Assert.Empty(await session.RestoreFrozenTestsAsync(default));
        Assert.Empty(files.Writes);
    }

    [Theory]
    [InlineData("tests/LimitTests.cs.bak")]
    [InlineData("tests/LimitTests2.cs")]
    [InlineData("tests/LimitTests")]
    [InlineData("tests/Limit.cs")]
    public async Task A_similar_name_is_not_a_frozen_file(string path)
    {
        var files = new MemoryFiles();
        using WorkerWorkspaceSession session = FrozenSession(files, Path.GetTempPath(), frozen: Frozen);

        string result = await session.WriteFileAsync(path, "other\n", default);

        Assert.NotEqual(FixedMessage, result);
        Assert.Single(files.Writes);
    }

    [Fact]
    public async Task A_frozen_file_deleted_on_the_worker_comes_back_from_an_untouched_hub_copy_and_a_second_restore_finds_nothing()
    {
        string hub = NewHub();
        try
        {
            var files = new MemoryFiles();
            using WorkerWorkspaceSession session = FrozenSession(files, hub, frozen: Frozen);
            string remote = session.RemoteRoot + "/tests/LimitTests.cs";

            IReadOnlyList<string> restored = await session.RestoreFrozenTestsAsync(default);

            Assert.Equal(["tests/LimitTests.cs"], restored);
            Assert.Equal(HubCopy, files.Files[remote]);
            Assert.Empty(await session.RestoreFrozenTestsAsync(default));
        }
        finally { Directory.Delete(hub, recursive: true); }
    }

    [Fact]
    public async Task Restore_refuses_a_hub_copy_that_no_longer_matches_the_accepted_test()
    {
        string hub = NewHub();
        try
        {
            File.WriteAllText(Path.Combine(hub, "tests", "LimitTests.cs"), "edited on the hub\n");
            var files = new MemoryFiles();
            using WorkerWorkspaceSession session = FrozenSession(files, hub, frozen: Frozen);
            string remote = session.RemoteRoot + "/tests/LimitTests.cs";
            files.Files[remote] = "worker edit\n";

            IOException error = await Assert.ThrowsAsync<IOException>(() => session.RestoreFrozenTestsAsync(default));

            Assert.Contains("tests/LimitTests.cs", error.Message);
            Assert.Contains("no longer matches the accepted test", error.Message);
            Assert.Empty(files.Writes);
            Assert.Equal("worker edit\n", files.Files[remote]);
        }
        finally { Directory.Delete(hub, recursive: true); }
    }

    [Fact]
    public async Task Restore_refuses_a_missing_hub_copy_and_leaves_the_worker_file_alone()
    {
        string hub = NewHub();
        try
        {
            File.Delete(Path.Combine(hub, "tests", "LimitTests.cs"));
            var files = new MemoryFiles();
            using WorkerWorkspaceSession session = FrozenSession(files, hub, frozen: Frozen);
            string remote = session.RemoteRoot + "/tests/LimitTests.cs";
            files.Files[remote] = "worker edit\n";

            IOException error = await Assert.ThrowsAsync<IOException>(() => session.RestoreFrozenTestsAsync(default));

            Assert.Contains("missing from the hub copy", error.Message);
            Assert.Empty(files.Writes);
            Assert.Equal("worker edit\n", files.Files[remote]);
        }
        finally { Directory.Delete(hub, recursive: true); }
    }
}
