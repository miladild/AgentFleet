using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>A fact that is skipped, not failed, on a machine where node is not on the path.</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (!NodeOnPath())
        {
            Skip = "node is not on the PATH";
        }
    }

    private static bool NodeOnPath()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return process is not null && process.WaitForExit(15_000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

// A node test run that finishes its tests and then stays alive (a server a test never closed) is ended a moment after the preload
// reports it, instead of waiting out the time limit. These tests run node for real, in a temporary folder.
public sealed class OpenHandleEarlyEndTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fleet-open-handle-" + Guid.NewGuid().ToString("N"));
    private readonly HubShellOptions _options = HubShellOptions.Load(new ConfigurationBuilder().Build());

    public OpenHandleEarlyEndTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        // A test that failed to end its node process must not leave it running.
        foreach (string pidFile in Directory.GetFiles(_folder, "*.pid"))
        {
            KillIfRunning(pidFile);
        }

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A folder that is still in use is left to the system's temp cleanup.
        }
    }

    private static void KillIfRunning(string pidFile)
    {
        try
        {
            using Process process = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Not running any more: the normal case.
        }
    }

    private const string Header =
        "const test = require('node:test');\nconst assert = require('node:assert');\nconst http = require('node:http');\n" +
        "const fs = require('node:fs');\nconst path = require('node:path');\n";

    // Two passing tests, and a server the tests never close.
    private const string OpenServerPasses = Header +
        "fs.writeFileSync(path.join(__dirname, 'open.pid'), String(process.pid));\n" +
        "const server = http.createServer(() => {});\n" +
        "test('first test', async () => { await new Promise(r => server.listen(0, r)); assert.ok(true); });\n" +
        "test('second test', async () => { assert.strictEqual(1, 1); });\n";

    // The same server, and a test that fails.
    private const string OpenServerFails = Header +
        "fs.writeFileSync(path.join(__dirname, 'fail.pid'), String(process.pid));\n" +
        "const server = http.createServer(() => {});\n" +
        "test('first test', async () => { await new Promise(r => server.listen(0, r)); assert.ok(true); });\n" +
        "test('failing test', async () => { assert.strictEqual(1, 2, 'distinctive mismatch text'); });\n";

    private const string Clean = Header +
        "test('first test', async () => { assert.ok(true); });\n" +
        "test('second test', async () => { assert.strictEqual(1, 1); });\n";

    private string Write(string name, string source)
    {
        File.WriteAllText(Path.Combine(_folder, name), source);
        return name;
    }

    private static string Preloaded(string file) => $"node {OpenHandlePreload.Argument()} --test {file}";

    private Task<string> RunAsync(string command, CancellationToken cancellationToken = default) =>
        WorkspaceTools.RunCommandAsync(command, _folder, _options, NullLogger.Instance, cancellationToken);

    private bool NodeProcessIsGone(string pidFile)
    {
        int pid = int.Parse(File.ReadAllText(Path.Combine(_folder, pidFile)).Trim());
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    [NodeFact]
    public async Task A_run_that_leaves_a_server_open_is_ended_early_with_its_test_lines_and_the_marker()
    {
        string file = Write("open.test.js", OpenServerPasses);
        var clock = Stopwatch.StartNew();

        string result = await RunAsync(Preloaded(file));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the run took {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains("[open-after-tests]", result, StringComparison.Ordinal);
        Assert.Contains("TCPServerWrap", OpenHandlePreload.ResourceFrom(result) ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("first test", result, StringComparison.Ordinal);
        Assert.Contains("second test", result, StringComparison.Ordinal);
        Assert.Equal(OutcomeClass.OpenHandle, OutcomeClassifier.OfRunnerResult(PlanTools.ExitCodeOf(result), false, result));
        Assert.False(CheckDefects.IsTimeout(result));
        Assert.DoesNotContain("timeout", result, StringComparison.OrdinalIgnoreCase);
        // The preload ends the file's own process with the tests' exit code, so the run is a normal one that carries the marker.
        Assert.StartsWith("Exit code: 0", result, StringComparison.Ordinal);
        Assert.True(NodeProcessIsGone("open.pid"), "the test's node process was left running");
    }

    [NodeFact]
    public async Task A_failing_run_that_leaves_a_server_open_is_ended_early_and_shows_the_failing_test()
    {
        string file = Write("fail.test.js", OpenServerFails);
        var clock = Stopwatch.StartNew();

        string result = await RunAsync(Preloaded(file));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the run took {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains("[open-after-tests]", result, StringComparison.Ordinal);
        Assert.Contains("failing test", result, StringComparison.Ordinal);
        Assert.Equal(OutcomeClass.OpenHandle, OutcomeClassifier.OfRunnerResult(PlanTools.ExitCodeOf(result), false, result));
        Assert.True(NodeProcessIsGone("fail.pid"), "the test's node process was left running");
    }

    [NodeFact]
    public async Task A_clean_run_ends_by_itself_with_no_marker_and_exit_zero()
    {
        string file = Write("clean.test.js", Clean);

        string result = await RunAsync(Preloaded(file));

        Assert.StartsWith("Exit code: 0", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[open-after-tests]", result, StringComparison.Ordinal);
        Assert.Contains("first test", result, StringComparison.Ordinal);
        Assert.Contains("second test", result, StringComparison.Ordinal);
        Assert.Equal(OutcomeClass.Pass, OutcomeClassifier.OfRunnerResult(PlanTools.ExitCodeOf(result), false, result));
    }

    [NodeFact]
    public async Task A_run_with_a_failing_test_and_nothing_open_keeps_its_exit_code_and_is_a_test_failure()
    {
        string file = Write("plain-fail.test.js", Header + "test('failing test', async () => { assert.strictEqual(1, 2); });\n");

        string result = await RunAsync(Preloaded(file));

        Assert.StartsWith("Exit code: 1", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[open-after-tests]", result, StringComparison.Ordinal);
        Assert.Equal(OutcomeClass.TestFailure, OutcomeClassifier.OfRunnerResult(PlanTools.ExitCodeOf(result), false, result));
    }

    [NodeFact]
    public async Task A_command_without_the_preload_is_never_ended_early_even_when_it_prints_the_marker()
    {
        // The marker is only the fleet's own when the command carries the preload: anything else runs to its end as before.
        string result = await RunAsync("node -e \"console.error('[open-after-tests] Fake'); setTimeout(() => console.log('finished'), 1500)\"");

        Assert.StartsWith("Exit code: 0", result, StringComparison.Ordinal);
        Assert.Contains("finished", result, StringComparison.Ordinal);
    }

    [NodeFact]
    public async Task The_early_end_waits_a_moment_so_the_rest_of_the_output_arrives_and_ends_the_whole_process_tree()
    {
        // A shell that starts node, which prints the marker and then one more line, and would otherwise run for a minute.
        string script = Write("marker.js",
            "require('node:fs').writeFileSync(require('node:path').join(__dirname, 'marker.pid'), String(process.pid));\n" +
            "console.error('[open-after-tests] Fake');\n" +
            "setTimeout(() => console.log('late line'), 100);\n" +
            "setTimeout(() => {}, 60000);\n");
        var clock = Stopwatch.StartNew();

        string result = await HubShellTools.RunCommandAsync($"node {script}", _folder, _options, NullLogger.Instance, default,
            stopWhenLine: OpenHandlePreload.IsMarkerLine);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the run took {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains("[open-after-tests] Fake", result, StringComparison.Ordinal);
        Assert.Contains("late line", result, StringComparison.Ordinal);
        Assert.True(NodeProcessIsGone("marker.pid"), "the node process was left running");
    }
}
