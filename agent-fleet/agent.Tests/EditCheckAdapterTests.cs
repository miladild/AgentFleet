using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// The worker session's bytes route for the edit check: a file comes back and goes back exactly as it is, under the file
/// tools' path rules, and a path the tools would refuse is refused here.
/// </summary>
public sealed class EditCheckAdapterTests
{
    private const string Frozen = "tests/LimitTests.cs";

    private sealed class Layer : IWorkerWorkspaceFileAccess
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public bool Exists(string path) => Files.ContainsKey(path);

        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Files[path]);

        public void CreateDirectoryTree(string path) { }

        public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            Files[path] = content;
            return Task.CompletedTask;
        }
    }

    private static WorkerWorkspaceSession Session(Layer layer, string platform = "linux", params PlanFrozenTestFile[] frozen) => new("worker-adapter",
        new FleetWorkerWorkspaceConfig("worker.example.test", "fleet-test", "C:/fleet-test/key", "SHA256:synthetic-host-key",
            "/tmp/fleet-test/workspaces", platform),
        Path.GetTempPath(), NullLogger.Instance, TimeSpan.FromSeconds(1), fileAccess: layer, frozenFiles: frozen);

    [Fact]
    public async Task Bytes_come_back_and_go_back_unchanged_with_a_byte_order_mark_and_crlf_line_breaks()
    {
        var layer = new Layer();
        using WorkerWorkspaceSession session = Session(layer);
        string remote = session.RemoteRoot + "/src/a.ts";
        string text = "\uFEFFfirst line\r\nsecond line\r\n";
        layer.Files[remote] = text;
        byte[] original = Encoding.UTF8.GetBytes(text);

        byte[]? read = await session.ReadBytesAsync("src/a.ts", default);
        Assert.Equal(original, read);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, read![..3]);

        await session.WriteBytesAsync("src/a.ts", read, default);
        Assert.Equal(original, Encoding.UTF8.GetBytes(layer.Files[remote]));

        byte[] changed = Encoding.UTF8.GetBytes("\uFEFFnew line\r\n");
        await session.WriteBytesAsync("src/a.ts", changed, default);
        Assert.Equal(changed, Encoding.UTF8.GetBytes(layer.Files[remote]));
    }

    [Fact]
    public async Task A_file_that_does_not_exist_reads_as_null_and_deleting_it_is_not_an_error()
    {
        var layer = new Layer();
        using WorkerWorkspaceSession session = Session(layer);

        Assert.Null(await session.ReadBytesAsync("src/none.ts", default));
        await session.WriteBytesAsync("src/none.ts", null, default);
        Assert.False(layer.Files.ContainsKey(session.RemoteRoot + "/src/none.ts"));
    }

    [Fact]
    public async Task Deleting_an_existing_file_through_the_in_memory_layer_says_so_rather_than_pretending()
    {
        var layer = new Layer();
        using WorkerWorkspaceSession session = Session(layer);
        string remote = session.RemoteRoot + "/src/a.ts";
        layer.Files[remote] = "kept\n";

        await Assert.ThrowsAsync<NotSupportedException>(() => session.WriteBytesAsync("src/a.ts", null, default));
        Assert.Equal("kept\n", layer.Files[remote]);
    }

    [Fact]
    public async Task A_frozen_test_file_is_refused_for_reads_and_writes_and_nothing_is_written()
    {
        var layer = new Layer();
        using WorkerWorkspaceSession session = Session(layer, "linux", new PlanFrozenTestFile(Frozen, "0"));
        string remote = session.RemoteRoot + "/" + Frozen;
        layer.Files[remote] = "fixed\n";

        await Assert.ThrowsAsync<WorkerPathRefusedException>(() => session.ReadBytesAsync(Frozen, default));
        await Assert.ThrowsAsync<WorkerPathRefusedException>(() => session.WriteBytesAsync("./tests/LimitTests.cs", Encoding.UTF8.GetBytes("changed\n"), default));
        Assert.Equal("fixed\n", layer.Files[remote]);
    }

    [Fact]
    public async Task A_private_file_and_a_path_outside_the_project_are_refused_and_nothing_is_written()
    {
        var layer = new Layer();
        using WorkerWorkspaceSession session = Session(layer);
        string privateFile = session.RemoteRoot + "/.npmrc";
        layer.Files[privateFile] = "notes\n";

        await Assert.ThrowsAsync<WorkerPathRefusedException>(() => session.WriteBytesAsync(".npmrc", Encoding.UTF8.GetBytes("changed\n"), default));
        await Assert.ThrowsAsync<WorkerPathRefusedException>(() => session.ReadBytesAsync("../outside.txt", default));
        Assert.Equal("notes\n", layer.Files[privateFile]);
        Assert.Equal(["/.npmrc"], layer.Files.Keys.Select(key => key[session.RemoteRoot.Length..]).ToArray());
    }

    [Fact]
    public void One_path_has_one_key_and_a_windows_worker_ignores_case()
    {
        var linux = new Layer();
        using WorkerWorkspaceSession unix = Session(linux);
        Assert.Equal(unix.Canonical("src/a.ts"), unix.Canonical("./src/a.ts"));
        Assert.Equal(unix.Canonical("src/a.ts"), unix.Canonical("src\\a.ts"));
        Assert.NotEqual(unix.Canonical("src/a.ts"), unix.Canonical("Src/A.ts"));

        var windows = new Layer();
        using WorkerWorkspaceSession desktop = Session(windows, "windows");
        Assert.Equal(desktop.Canonical("src/a.ts"), desktop.Canonical("Src\\A.ts"));
        Assert.Equal(desktop.Canonical("src/a.ts"), desktop.Canonical("./src/a.ts"));
        Assert.Equal(desktop.Canonical("src/a.ts").ToLowerInvariant(), desktop.Canonical("src/a.ts"));
    }
}
