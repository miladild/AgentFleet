using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentFleet.Tests;

/// <summary>
/// The edit check hooks in the file tools, through a real worker session over an in-memory file layer: the check suffix,
/// a refused edit, a failed edit that runs no check, a move or delete that switches the check off, and the calls with no
/// gate or no worker scope.
/// </summary>
public sealed class EditCheckWorkerTests
{
    private const string Original = "const answer = 42;\n";
    private const string Passing = "Exit code: 0\n--- stdout ---\nok";

    private static string Failing(int count) =>
        "Exit code: 1\n--- stdout ---\n" + string.Join('\n', Enumerable.Range(1, count).Select(index => $"✖ shop session case {index} (1ms)"));

    private static string DoesNotLoad => PlanCheckOutputFixtures.Round01;

    private sealed class FileLayer : IWorkerWorkspaceFileAccess
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

    private static WorkerWorkspaceSession NewSession(FileLayer layer) => new("worker-edit-check",
        new FleetWorkerWorkspaceConfig("worker.example.test", "fleet-test", "C:/fleet-test/key", "SHA256:synthetic-host-key",
            "/tmp/fleet-test/workspaces", "linux"),
        "C:/fleet-test/project", NullLogger.Instance, TimeSpan.FromSeconds(1), fileAccess: layer);

    /// <summary>The gate's view of the worker's files: the same remote files the session edits.</summary>
    private sealed class WorkerFiles(WorkerWorkspaceSession session, FileLayer layer) : IEditCheckFiles
    {
        private string Remote(string path) => session.RemoteRoot + "/" + Canonical(path);

        public Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(layer.Files.TryGetValue(Remote(path), out string? text) ? Encoding.UTF8.GetBytes(text) : null);

        public Task WriteBytesAsync(string path, byte[]? contents, CancellationToken cancellationToken)
        {
            if (contents is null) layer.Files.Remove(Remote(path));
            else layer.Files[Remote(path)] = Encoding.UTF8.GetString(contents);
            return Task.CompletedTask;
        }

        public string Canonical(string path) => path.Replace('\\', '/').TrimStart('.').TrimStart('/').ToLowerInvariant();
    }

    private sealed class ScriptedCheck(params string[] outputs)
    {
        private int _next;

        public int Calls { get; private set; }

        public Task<string> RunAsync(CancellationToken cancellationToken)
        {
            Calls++;
            string output = outputs[Math.Min(_next, outputs.Length - 1)];
            _next++;
            return Task.FromResult(output);
        }
    }

    private static EditCheckGate NewGate(WorkerWorkspaceSession session, FileLayer layer, ScriptedCheck check) =>
        new(new WorkerFiles(session, layer), check.RunAsync, output => RoundScore.Of(PlanFailure.FailureSignature(output), output),
            new EditCheckSettings(), _ => { });

    private static void SeedApp(WorkerWorkspaceSession session, FileLayer layer) =>
        layer.Files[session.RemoteRoot + "/src/app.js"] = Original;

    [Fact]
    public async Task A_worker_edit_gets_the_check_suffix_and_the_edit_is_kept()
    {
        var layer = new FileLayer();
        using WorkerWorkspaceSession session = NewSession(layer);
        SeedApp(session, layer);
        var check = new ScriptedCheck(Failing(3), Failing(2));
        EditCheckGate gate = NewGate(session, layer, check);

        using (WorkerWorkspaceContext.Push(session))
        using (EditCheckContext.Push(gate))
        {
            string reply = await WorkspaceTools.EditFileAsync("src/app.js", "42", "43", null, CancellationToken.None);

            Assert.EndsWith("\n[Check after this edit: 2 failing, best so far 2. This is the best state so far.]", reply);
        }

        Assert.Contains("43", layer.Files[session.RemoteRoot + "/src/app.js"]);
        Assert.Equal(2, check.Calls);
    }

    [Fact]
    public async Task A_worker_edit_that_breaks_loading_is_refused_and_the_file_is_byte_identical_afterwards()
    {
        var layer = new FileLayer();
        using WorkerWorkspaceSession session = NewSession(layer);
        SeedApp(session, layer);
        var check = new ScriptedCheck(Failing(3), DoesNotLoad);
        EditCheckGate gate = NewGate(session, layer, check);
        string remote = session.RemoteRoot + "/src/app.js";

        string reply;
        using (WorkerWorkspaceContext.Push(session))
        using (EditCheckContext.Push(gate))
        {
            reply = await WorkspaceTools.EditFileAsync("src/app.js", "42", "4 2 (", null, CancellationToken.None);
        }

        Assert.StartsWith("Error: nothing was changed in src/app.js. After this edit the project no longer loads:", reply);
        Assert.EndsWith("[Check after this edit: does not load, best so far 3. The edit was undone.]", reply);
        Assert.Equal(Encoding.UTF8.GetBytes(Original), Encoding.UTF8.GetBytes(layer.Files[remote]));
    }

    [Fact]
    public async Task A_worker_edit_that_fails_runs_no_check_after_the_edit()
    {
        var layer = new FileLayer();
        using WorkerWorkspaceSession session = NewSession(layer);
        SeedApp(session, layer);
        var check = new ScriptedCheck(Failing(3));
        EditCheckGate gate = NewGate(session, layer, check);

        string reply;
        using (WorkerWorkspaceContext.Push(session))
        using (EditCheckContext.Push(gate))
        {
            reply = await WorkspaceTools.EditFileAsync("src/app.js", "text that is not in the file", "x", null, CancellationToken.None);
        }

        Assert.StartsWith("Error:", reply);
        Assert.DoesNotContain("[Check after this edit", reply);
        Assert.Equal(1, check.Calls);
        Assert.Equal(Original, layer.Files[session.RemoteRoot + "/src/app.js"]);
    }

    [Fact]
    public async Task With_no_gate_pushed_a_worker_edit_is_the_same_call_as_before()
    {
        var hooked = new FileLayer();
        var direct = new FileLayer();
        using WorkerWorkspaceSession hookedSession = NewSession(hooked);
        using WorkerWorkspaceSession directSession = NewSession(direct);
        SeedApp(hookedSession, hooked);
        SeedApp(directSession, direct);

        string viaTools;
        using (WorkerWorkspaceContext.Push(hookedSession))
        {
            viaTools = await WorkspaceTools.EditFileAsync("src/app.js", "42", "43", null, CancellationToken.None);
        }

        string viaSession = await directSession.EditFileAsync("src/app.js", "42", "43", null, CancellationToken.None);

        Assert.Equal(viaSession, viaTools);
        Assert.DoesNotContain("[Check after this edit", viaTools);
        Assert.Equal(direct.Files[directSession.RemoteRoot + "/src/app.js"], hooked.Files[hookedSession.RemoteRoot + "/src/app.js"]);
    }

    [Fact]
    public async Task A_move_in_worker_scope_switches_the_check_off_so_a_later_edit_runs_no_check()
    {
        var layer = new FileLayer();
        using WorkerWorkspaceSession session = NewSession(layer);
        SeedApp(session, layer);
        layer.Files[session.RemoteRoot + "/src/other.js"] = "const other = 1;\n";
        var check = new ScriptedCheck(Failing(3), Failing(2));
        EditCheckGate gate = NewGate(session, layer, check);

        string later;
        using (WorkerWorkspaceContext.Push(session))
        using (EditCheckContext.Push(gate))
        {
            await WorkspaceTools.EditFileAsync("src/app.js", "42", "43", null, CancellationToken.None);
            Assert.Equal(2, check.Calls); // the baseline and the first edit
            Record.Exception(() => WorkspaceTools.MoveFile("src/other.js", "src/moved.js", null));
            later = await WorkspaceTools.EditFileAsync("src/app.js", "43", "44", null, CancellationToken.None);
        }

        Assert.DoesNotContain("[Check after this edit", later);
        Assert.Equal(2, check.Calls);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.True(summary.Disabled);
        Assert.Equal("a file was moved or deleted", summary.DisabledReason);
    }

    [Fact]
    public async Task A_delete_in_worker_scope_switches_the_check_off()
    {
        var layer = new FileLayer();
        using WorkerWorkspaceSession session = NewSession(layer);
        SeedApp(session, layer);
        layer.Files[session.RemoteRoot + "/src/other.js"] = "const other = 1;\n";
        var check = new ScriptedCheck(Failing(3), Failing(2));
        EditCheckGate gate = NewGate(session, layer, check);

        string later;
        using (WorkerWorkspaceContext.Push(session))
        using (EditCheckContext.Push(gate))
        {
            await WorkspaceTools.EditFileAsync("src/app.js", "42", "43", null, CancellationToken.None);
            Record.Exception(() => WorkspaceTools.DeleteFile("src/other.js"));
            later = await WorkspaceTools.EditFileAsync("src/app.js", "43", "44", null, CancellationToken.None);
        }

        Assert.DoesNotContain("[Check after this edit", later);
        Assert.Equal(2, check.Calls);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.True(summary.Disabled);
        Assert.Equal("a file was moved or deleted", summary.DisabledReason);
    }
}
