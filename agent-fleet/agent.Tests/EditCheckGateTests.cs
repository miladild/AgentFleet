using System.Text;

namespace AgentFleet.Tests;

/// <summary>
/// The edit check gate: the approved check runs after each file edit, a breaking edit is undone, and a run that stops
/// improving goes back to its best state. Files and the check are fakes; scoring is the runner's own.
/// </summary>
public sealed class EditCheckGateTests : IDisposable
{
    private const string Passing = "Exit code: 0\n--- stdout ---\nok";
    private const string Path3 = "src/shop.ts";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edit-check-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string Failing(int count) =>
        "Exit code: 1\n--- stdout ---\n" + string.Join('\n', Enumerable.Range(1, count).Select(index => $"✖ shop session case {index} (1ms)"));

    private static string DoesNotLoad => PlanCheckOutputFixtures.Round01;

    private sealed class MemoryFiles : IEditCheckFiles
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public List<string> Writes { get; } = [];

        public bool FailWrites { get; set; }

        public Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(_store.TryGetValue(Canonical(path), out byte[]? bytes) ? bytes : null);

        public Task WriteBytesAsync(string path, byte[]? contents, CancellationToken cancellationToken)
        {
            if (FailWrites) throw new IOException("disk full");
            Writes.Add(path);
            if (contents is null) _store.Remove(Canonical(path));
            else _store[Canonical(path)] = contents;
            return Task.CompletedTask;
        }

        public string Canonical(string path) => path.Replace('\\', '/').TrimStart('.').TrimStart('/').ToLowerInvariant();

        public void Seed(string path, string text) => _store[Canonical(path)] = Encoding.UTF8.GetBytes(text);

        public string? Text(string path) =>
            _store.TryGetValue(Canonical(path), out byte[]? bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    /// <summary>A check that answers the given outputs in order, then repeats the last one.</summary>
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

    private static EditCheckGate NewGate(MemoryFiles files, Func<CancellationToken, Task<string>> check,
        EditCheckSettings? settings = null, List<string>? log = null) =>
        new(files, check, output => RoundScore.Of(PlanFailure.FailureSignature(output), output), settings ?? new EditCheckSettings(),
            line => log?.Add(line));

    /// <summary>One model edit as the file tools make it: the gate looks before, the file changes, the gate looks after.</summary>
    private static async Task<string> Write(EditCheckGate gate, MemoryFiles files, string path, string? content)
    {
        await gate.BeforeMutationAsync(path, CancellationToken.None);
        await files.WriteBytesAsync(path, content is null ? null : Encoding.UTF8.GetBytes(content), CancellationToken.None);
        return await gate.AfterMutationAsync(path, $"Wrote {path}", CancellationToken.None);
    }

    [Fact]
    public async Task A_run_that_stops_improving_is_put_back_to_its_best_state()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var check = new ScriptedCheck(Failing(7), Failing(6), Failing(8), Failing(8), Failing(8), Failing(8));
        EditCheckGate gate = NewGate(files, check.RunAsync);

        string first = await Write(gate, files, Path3, "v1");
        Assert.Equal("Wrote src/shop.ts\n[Check after this edit: 6 failing, best so far 6. This is the best state so far.]", first);

        for (int edit = 2; edit <= 4; edit++)
        {
            string reply = await Write(gate, files, Path3, $"v{edit}");
            Assert.DoesNotContain("Restored:", reply);
        }

        string restored = await Write(gate, files, Path3, "v5");

        Assert.Equal("v1", files.Text(Path3));
        Assert.Contains("Restored: the last 4 edits did not help.", restored);
        Assert.Contains("back at the best state so far (6 failing): src/shop.ts.", restored);
        Assert.Contains("Read them again before you edit them.", restored);
        Assert.EndsWith("\n[Check after this edit: 8 failing, best so far 6. The files were put back to the best state.]", restored);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.Equal(1, summary.Restored);
        Assert.Equal(6, summary.Best!.Value.Failures);
        Assert.False(summary.FinalRestored);
        Assert.Equal("v1", files.Text(Path3));
    }

    [Fact]
    public async Task Finish_puts_the_best_state_back_when_the_last_state_is_worse()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(7), Failing(6), Failing(8)).RunAsync);

        await Write(gate, files, Path3, "v1");
        await Write(gate, files, Path3, "v2");
        Assert.Equal("v2", files.Text(Path3));

        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);

        Assert.Equal("v1", files.Text(Path3));
        Assert.True(summary.FinalRestored);
        Assert.Equal(0, summary.Restored);
        Assert.Equal(6, summary.Best!.Value.Failures);
    }

    [Fact]
    public async Task Finish_never_throws_and_reports_a_write_that_fails()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(7), Failing(6), Failing(8)).RunAsync);
        await Write(gate, files, Path3, "v1");
        await Write(gate, files, Path3, "v2");
        files.FailWrites = true;

        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);

        Assert.True(summary.FailedWrites > 0);
    }

    [Fact]
    public async Task A_breaking_edit_is_undone_byte_for_byte_and_the_reply_says_why()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var check = new ScriptedCheck(Failing(3), DoesNotLoad);
        EditCheckGate gate = NewGate(files, check.RunAsync);

        string reply = await Write(gate, files, Path3, "broken");

        Assert.Equal("v0", files.Text(Path3));
        Assert.StartsWith("Error: nothing was changed in src/shop.ts. After this edit the project no longer loads:\n", reply);
        Assert.Contains("Transform failed with 1 error:", reply);
        Assert.Contains("ERROR: Expected", reply);
        Assert.Contains("The file is exactly as it was before this edit. Fix the cause in your next edit.", reply);
        Assert.EndsWith("[Check after this edit: does not load, best so far 3. The edit was undone.]", reply);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.Equal(1, summary.Refused);
    }

    [Fact]
    public async Task A_refused_new_file_is_deleted_again()
    {
        var files = new MemoryFiles();
        var gate = NewGate(files, new ScriptedCheck(Failing(3), DoesNotLoad).RunAsync);

        string reply = await Write(gate, files, "src/new.ts", "const x = ");

        Assert.StartsWith("Error: nothing was changed in src/new.ts.", reply);
        Assert.Null(files.Text("src/new.ts"));
        Assert.Equal(new[] { "src/new.ts", "src/new.ts" }, files.Writes.ToArray());
    }

    [Fact]
    public async Task Refused_edits_do_not_count_as_not_improving()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var check = new ScriptedCheck(Failing(3), DoesNotLoad, Failing(3), Failing(3), Failing(3), Failing(3));
        EditCheckGate gate = NewGate(files, check.RunAsync);

        await Write(gate, files, Path3, "broken");
        for (int edit = 1; edit <= 3; edit++)
        {
            string reply = await Write(gate, files, Path3, $"same{edit}");
            Assert.DoesNotContain("Restored:", reply);
        }

        string restored = await Write(gate, files, Path3, "same4");

        Assert.Contains("Restored: the last 4 edits did not help.", restored);
    }

    [Fact]
    public async Task The_third_refusal_on_one_path_keeps_the_edit_with_a_warning()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(3), DoesNotLoad, DoesNotLoad, DoesNotLoad).RunAsync);

        string first = await Write(gate, files, Path3, "broken1");
        string second = await Write(gate, files, Path3, "broken2");
        string third = await Write(gate, files, Path3, "broken3");

        Assert.StartsWith("Error: nothing was changed", first);
        Assert.StartsWith("Error: nothing was changed", second);
        Assert.StartsWith("Warning:", third);
        Assert.Contains("ERROR: Expected", third);
        Assert.Equal("broken3", files.Text(Path3));
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.Equal(2, summary.Refused);
    }

    [Fact]
    public async Task Two_paths_count_their_refusals_separately()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        files.Seed("src/other.ts", "o0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(3), DoesNotLoad, DoesNotLoad, DoesNotLoad).RunAsync);

        Assert.StartsWith("Error:", await Write(gate, files, Path3, "a1"));
        Assert.StartsWith("Error:", await Write(gate, files, Path3, "a2"));
        Assert.StartsWith("Error:", await Write(gate, files, "src/other.ts", "b1"));
        Assert.Equal("o0", files.Text("src/other.ts"));
    }

    [Fact]
    public async Task Paths_in_different_spellings_are_one_file_for_the_refusals()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(3), DoesNotLoad, DoesNotLoad, DoesNotLoad).RunAsync);

        Assert.StartsWith("Error:", await Write(gate, files, "src\\Shop.ts", "a1"));
        Assert.StartsWith("Error:", await Write(gate, files, "./src/shop.ts", "a2"));
        Assert.StartsWith("Warning:", await Write(gate, files, "SRC/shop.ts", "a3"));
    }

    [Fact]
    public async Task A_baseline_that_does_not_load_refuses_nothing_and_a_loading_state_becomes_the_best()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(DoesNotLoad, Failing(5), DoesNotLoad).RunAsync);

        string loads = await Write(gate, files, Path3, "v1");
        Assert.Equal("Wrote src/shop.ts\n[Check after this edit: 5 failing, best so far 5. This is the best state so far.]", loads);

        string broke = await Write(gate, files, Path3, "v2");
        Assert.DoesNotContain("Error:", broke);
        Assert.Equal("v2", files.Text(Path3));
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.Equal(0, summary.Refused);
        Assert.Equal(5, summary.Best!.Value.Failures);
    }

    [Fact]
    public async Task A_run_that_passes_stops_the_round_and_says_so()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(2), Passing).RunAsync);
        Assert.False(gate.StopRequested);

        string reply = await Write(gate, files, Path3, "v1");

        Assert.True(gate.StopRequested);
        Assert.Contains("All checks pass. The fleet runs the final check now.", reply);
        Assert.StartsWith("Wrote src/shop.ts\n", reply);
        Assert.EndsWith("\n[Check after this edit: 0 failing, best so far 0. This is the best state so far.]", reply);
    }

    [Fact]
    public async Task Two_restores_in_one_attempt_stop_the_run()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var outputs = new List<string> { Failing(7), Failing(6) };
        outputs.AddRange(Enumerable.Repeat(Failing(8), 8));
        EditCheckGate gate = NewGate(files, new ScriptedCheck(outputs.ToArray()).RunAsync);

        await Write(gate, files, Path3, "v1");
        for (int edit = 2; edit <= 5; edit++) await Write(gate, files, Path3, $"v{edit}");
        Assert.False(gate.StopRequested);

        for (int edit = 6; edit <= 9; edit++) await Write(gate, files, Path3, $"v{edit}");

        Assert.True(gate.StopRequested);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.Equal(2, summary.Restored);
        Assert.True(summary.Stopped);
    }

    [Fact]
    public async Task A_failed_check_that_names_no_test_keeps_the_edit_and_says_so()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(3), "Exit code: 1\n--- stdout ---\nsomething odd happened").RunAsync);

        string reply = await Write(gate, files, Path3, "v1");

        Assert.Equal("v1", files.Text(Path3));
        Assert.Equal("Wrote src/shop.ts\n[Check after this edit: no failing test named, best so far 3. The edit is kept and not counted.]", reply);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.Equal(0, summary.Refused);
        Assert.Equal(0, summary.Restored);
    }

    [Fact]
    public async Task A_check_that_did_not_run_disables_the_gate_and_the_reply_is_unchanged()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var check = new ScriptedCheck("Error: the command was refused by the command policy");
        EditCheckGate gate = NewGate(files, check.RunAsync);

        string reply = await Write(gate, files, Path3, "v1");
        string again = await Write(gate, files, Path3, "v2");

        Assert.Equal("Wrote src/shop.ts", reply);
        Assert.Equal("Wrote src/shop.ts", again);
        Assert.Equal(1, check.Calls);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.True(summary.Disabled);
        Assert.Contains("did not run", summary.DisabledReason);
    }

    [Fact]
    public async Task A_baseline_slower_than_the_maximum_disables_the_gate()
    {
        var files = new MemoryFiles();
        var settings = new EditCheckSettings { MaximumCheckTime = TimeSpan.FromMilliseconds(50) };
        var check = new ScriptedCheck(Failing(3));
        EditCheckGate gate = NewGate(files, async ct =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), ct);
            return await check.RunAsync(ct);
        }, settings);

        string reply = await Write(gate, files, Path3, "v1");

        Assert.Equal("Wrote src/shop.ts", reply);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.True(summary.Disabled);
        Assert.Contains("longer", summary.DisabledReason);
    }

    [Fact]
    public async Task A_check_slower_than_three_times_the_maximum_after_an_edit_disables_the_gate()
    {
        var files = new MemoryFiles();
        var settings = new EditCheckSettings { MaximumCheckTime = TimeSpan.FromMilliseconds(50) };
        int calls = 0;
        EditCheckGate gate = NewGate(files, async ct =>
        {
            calls++;
            if (calls == 1) return Failing(3);
            await Task.Delay(Timeout.Infinite, ct);
            return Failing(3);
        }, settings);

        string reply = await Write(gate, files, Path3, "v1");

        Assert.Equal("Wrote src/shop.ts", reply);
        Assert.Equal("v1", files.Text(Path3));
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.True(summary.Disabled);
    }

    [Fact]
    public async Task A_check_that_throws_disables_the_gate_and_the_reply_is_unchanged()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, _ => throw new InvalidOperationException("boom"));

        string reply = await Write(gate, files, Path3, "v1");

        Assert.Equal("Wrote src/shop.ts", reply);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.True(summary.Disabled);
    }

    [Fact]
    public async Task Disabling_the_gate_makes_it_inert_for_the_rest_of_the_attempt()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var check = new ScriptedCheck(Failing(3), DoesNotLoad);
        EditCheckGate gate = NewGate(files, check.RunAsync);

        gate.Disable("a file was moved or deleted");
        string reply = await Write(gate, files, Path3, "broken");

        Assert.Equal("Wrote src/shop.ts", reply);
        Assert.Equal("broken", files.Text(Path3));
        Assert.Equal(0, check.Calls);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.Equal("a file was moved or deleted", summary.DisabledReason);
    }

    [Fact]
    public async Task A_switched_off_gate_runs_no_check_and_changes_nothing()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var check = new ScriptedCheck(Failing(3), DoesNotLoad);
        EditCheckGate gate = NewGate(files, check.RunAsync, new EditCheckSettings { Enabled = false });

        string reply = await Write(gate, files, Path3, "broken");

        Assert.Equal("Wrote src/shop.ts", reply);
        Assert.Equal("broken", files.Text(Path3));
        Assert.Equal(0, check.Calls);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.True(summary.Disabled);
        Assert.False(gate.StopRequested);
    }

    [Fact]
    public async Task The_gate_writes_one_log_line_per_checked_edit_without_file_contents()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var log = new List<string>();
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(3), Failing(2)).RunAsync, log: log);

        await Write(gate, files, Path3, "SECRET-CONTENT-MARKER");

        Assert.Single(log, line => line.Contains("src/shop.ts"));
        Assert.DoesNotContain(log, line => line.Contains("SECRET-CONTENT-MARKER"));
    }

    [Fact]
    public async Task An_undone_edit_keeps_the_last_checked_state_so_finish_still_puts_the_best_back()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(5), Failing(8), DoesNotLoad).RunAsync);

        await Write(gate, files, Path3, "v1");
        string refused = await Write(gate, files, Path3, "broken");
        Assert.StartsWith("Error: nothing was changed in src/shop.ts.", refused);
        Assert.Equal("v1", files.Text(Path3));

        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);

        Assert.Equal("v0", files.Text(Path3));
        Assert.True(summary.FinalRestored);
        Assert.Equal(5, summary.Best!.Value.Failures);
    }

    [Fact]
    public async Task An_undone_edit_after_an_unscored_edit_keeps_the_state_unknown_so_finish_does_not_restore()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(3), "Exit code: 1\n--- stdout ---\nsomething odd happened", DoesNotLoad).RunAsync);

        await Write(gate, files, Path3, "v1");
        string refused = await Write(gate, files, Path3, "broken");
        Assert.StartsWith("Error: nothing was changed in src/shop.ts.", refused);
        Assert.Equal("v1", files.Text(Path3));

        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);

        Assert.Equal("v1", files.Text(Path3));
        Assert.False(summary.FinalRestored);
    }

    [Fact]
    public async Task A_check_that_names_no_failure_leaves_the_state_unknown_so_finish_does_not_restore()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(3), "Exit code: 1\n--- stdout ---\nsomething odd happened").RunAsync);

        await Write(gate, files, Path3, "v1");
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);

        Assert.Equal("v1", files.Text(Path3));
        Assert.False(summary.FinalRestored);
        Assert.Equal(3, summary.Best!.Value.Failures);
    }

    [Fact]
    public async Task A_later_check_without_the_exit_code_disables_the_gate_and_keeps_the_edit()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        var check = new ScriptedCheck(Failing(3), "refused by policy: the command is not allowed");
        EditCheckGate gate = NewGate(files, check.RunAsync);

        string reply = await Write(gate, files, Path3, "v1");

        Assert.Equal("Wrote src/shop.ts", reply);
        Assert.Equal("v1", files.Text(Path3));
        Assert.Equal(2, check.Calls);

        string following = await Write(gate, files, Path3, "v2");

        Assert.Equal("Wrote src/shop.ts", following);
        Assert.Equal("v2", files.Text(Path3));
        Assert.Equal(2, check.Calls);
        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);
        Assert.True(summary.Disabled);
        Assert.Equal("the check did not run: its output does not start with the exit code", summary.DisabledReason);
    }

    [Fact]
    public void Problem_lines_are_at_most_eight_lines()
    {
        string output = "Exit code: 1\n" + string.Join('\n', Enumerable.Range(1, 30).Select(index => $"ERROR: problem number {index}"));

        string[] lines = EditCheckGate.ProblemLines(output).Split('\n');

        Assert.Equal(8, lines.Length);
        Assert.Equal("ERROR: problem number 1", lines[0]);
        Assert.Equal("ERROR: problem number 8", lines[7]);
    }

    [Fact]
    public void Problem_lines_are_cut_at_nine_hundred_characters_not_dropped()
    {
        string first = "Error: " + new string('a', 400);
        string second = "Error: " + new string('b', 400);
        string third = "Error: " + new string('c', 400);
        string output = string.Join('\n', first, second, third);

        string block = EditCheckGate.ProblemLines(output);

        // Two full lines and their breaks take 816 characters; the third line is cut to the 84 that remain.
        Assert.Equal($"{first}\n{second}\n{third[..84]}", block);
        Assert.Equal(900, block.Length);
    }

    [Fact]
    public void A_single_very_long_error_line_is_cut_to_nine_hundred_characters()
    {
        string longLine = "Error: " + new string('x', 2000);

        Assert.Equal(longLine[..900], EditCheckGate.ProblemLines(longLine));
    }

    [Fact]
    public void Problem_lines_start_at_the_first_error_like_line()
    {
        const string output = "header line A\nhello world\nError: boom\nlast line";

        Assert.Equal("Error: boom\nlast line", EditCheckGate.ProblemLines(output));
    }

    [Fact]
    public async Task The_restore_reply_quotes_the_first_expected_and_actual_lines_when_the_output_has_them()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        string assertion = Failing(8) + "\n    AssertionError: expected 'open' to equal 'closed'\n    expected: \"closed\"\n    actual: \"open\"";
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(7), Failing(6), Failing(8), Failing(8), Failing(8), assertion).RunAsync);
        for (int edit = 1; edit <= 4; edit++) await Write(gate, files, Path3, $"v{edit}");

        string restored = await Write(gate, files, Path3, "v5");

        Assert.Contains("Restored: the last 4 edits did not help.", restored);
        Assert.Contains("The first failing test:\n✖ shop session case 8 (1ms)\n    AssertionError: expected 'open' to equal 'closed'\n    expected: \"closed\"\n    actual: \"open\"", restored);
    }

    [Fact]
    public async Task The_restore_reply_has_no_first_failing_test_when_the_output_has_no_expected_line()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(7), Failing(6), Failing(8), Failing(8), Failing(8), Failing(8)).RunAsync);
        for (int edit = 1; edit <= 4; edit++) await Write(gate, files, Path3, $"v{edit}");

        string restored = await Write(gate, files, Path3, "v5");

        Assert.Contains("Restored: the last 4 edits did not help.", restored);
        Assert.DoesNotContain("The first failing test:", restored);
    }

    [Fact]
    public void The_suffix_and_refusal_wording_are_fixed()
    {
        Assert.Equal(
            "[Check after this edit: 4 failing, best so far 3. It did not improve on the best state.]",
            EditCheckGate.SuffixLine(new RoundScore(true, 4), new RoundScore(true, 3), "It did not improve on the best state."));
        Assert.Equal(
            "[Check after this edit: does not load, best so far 3. The edit was undone.]",
            EditCheckGate.SuffixLine(new RoundScore(false, 1), new RoundScore(true, 3), "The edit was undone."));
    }

    [Fact]
    public async Task The_summary_says_in_one_line_what_the_gate_did()
    {
        var files = new MemoryFiles();
        files.Seed(Path3, "v0");
        EditCheckGate gate = NewGate(files, new ScriptedCheck(Failing(3), DoesNotLoad).RunAsync);
        await Write(gate, files, Path3, "broken");

        EditCheckSummary summary = await gate.FinishAsync(CancellationToken.None);

        Assert.Equal("2 checks, 1 refused, 0 restored, best 3 failing, not switched off.", summary.Describe());
    }

    [Fact]
    public async Task The_hub_path_is_unchanged_when_a_gate_is_pushed_but_no_worker_is_set()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "a.txt");
        var files = new MemoryFiles();
        int calls = 0;
        EditCheckGate gate = NewGate(files, _ =>
        {
            calls++;
            return Task.FromResult(Passing);
        });

        using (EditCheckContext.Push(gate))
        {
            await WorkspaceTools.WriteFileAsync(path, "hello", CancellationToken.None);
            await WorkspaceTools.EditFileAsync(path, "hello", "hello again", null, CancellationToken.None);
        }

        Assert.Equal("hello again", File.ReadAllText(path));
        Assert.Equal(0, calls);
        Assert.False(gate.StopRequested);
    }

    [Fact]
    public void The_gate_context_is_empty_outside_a_push()
    {
        var files = new MemoryFiles();
        EditCheckGate gate = NewGate(files, _ => Task.FromResult(Passing));

        Assert.Null(EditCheckContext.Current);
        using (EditCheckContext.Push(gate))
        {
            Assert.Same(gate, EditCheckContext.Current);
        }

        Assert.Null(EditCheckContext.Current);
    }
}
