using System.Diagnostics;

namespace AgentFleet.Tests;

public sealed class PlanRestoreTests : PlanTestBase, IDisposable
{
    // Outside the test base's folder: git marks its object files read-only, which a plain recursive delete refuses.
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"fleet-restore-{Guid.NewGuid():N}");

    void IDisposable.Dispose()
    {
        if (Directory.Exists(_project))
        {
            foreach (string file in Directory.EnumerateFiles(_project, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_project, recursive: true);
        }

        Dispose();
    }

    // Runs a command the way the hub does and answers in the same "Exit code / stdout / stderr" shape.
    private static async Task<string> Run(string command, string? workingDirectory, CancellationToken cancellationToken)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c {command}")
            : new ProcessStartInfo("/bin/sh", ["-c", command]);
        start.WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process process = Process.Start(start)!;
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return $"Exit code: {process.ExitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}";
    }

    // A setup command that must work; its own output is the failure message when it does not. A virus scanner can hold a
    // freshly written git object for a moment ("unable to write file .git/objects/...: Permission denied", seen on a
    // developer machine and once while committing the repository itself), so that one failure is tried again.
    private static async Task Must(string command, string project)
    {
        string result = string.Empty;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            result = await Run(command, project, default);
            if (result.StartsWith("Exit code: 0", StringComparison.Ordinal) ||
                !result.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await Task.Delay(200 * attempt);
        }

        Assert.True(result.StartsWith("Exit code: 0", StringComparison.Ordinal), $"{command}\n{result}");
    }

    [Fact]
    public async Task A_tracked_file_no_step_names_is_put_back_after_the_check_deletes_it()
    {
        string project = _project;
        Directory.CreateDirectory(Path.Combine(project, "config"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "config", "watchlist.json"), "[\"AAPL\"]");
        File.WriteAllText(Path.Combine(project, "src", "old.ts"), "export {};");
        foreach (string command in new[] { "git init -q", "git add -A", "git -c user.name=t -c user.email=t@localhost commit -qm start" })
        {
            await Must(command, project);
        }

        // A check whose test cleans up with the project's own config file, next to a step that removes a file on purpose.
        string deleteBoth = OperatingSystem.IsWindows()
            ? @"del config\watchlist.json && del src\old.ts"
            : "rm config/watchlist.json src/old.ts";
        PlanRecord plan = Store.Create("Clean up", "Remove the old module", project, [], [], [], null, null,
            [new PlanStepInput("Remove the old module", "Delete src/old.ts", ["src/old.ts"], deleteBoth, "light")]);
        Store.Approve(plan.Id, exportToProject: false);
        var tools = new PlanTools(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)), Run);

        StepCompletion result = await tools.TryCompleteStepAsync(plan.Id, 1, null, default);

        Assert.True(result.Done);
        Assert.True(File.Exists(Path.Combine(project, "config", "watchlist.json")), "the file no step names is back");
        Assert.False(File.Exists(Path.Combine(project, "src", "old.ts")), "the file the step names stays deleted");
        Assert.Contains(Path.Combine("config", "watchlist.json"), result.Restored);
    }

    [Fact]
    public async Task Earlier_steps_work_that_an_attempt_resets_or_deletes_with_git_is_put_back_but_its_own_files_are_its_own()
    {
        string project = _project;
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "a.ts"), "export const a = 1;\n");
        string git = "git -c user.name=t -c user.email=t@localhost";
        foreach (string command in new[] { "git init -q", "git add -A", $"{git} commit -qm start" })
        {
            await Must(command, project);
        }

        PlanRecord plan = Store.Create("Board", "Build it", project, [], [], [], null, null,
        [
            new PlanStepInput("Change a", "Change a", ["src/a.ts", "src/new.ts"], "node -v", "light"),
            new PlanStepInput("Add b", "Add b", ["src/b.ts"], "node -v", "light")
        ]);
        Store.Approve(plan.Id, exportToProject: false);
        plan = Store.Update(plan.Id, current => FleetPlanStoreSteps.With(current, 1, step => step with { Status = StepStatus.Done }))!;
        var tools = new PlanTools(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)), Run);

        // Step 1's work, and a start on step 2's own file.
        File.WriteAllText(Path.Combine(project, "src", "a.ts"), "export const a = 2;\n");
        File.WriteAllText(Path.Combine(project, "src", "new.ts"), "export const n = 1;\n");
        File.WriteAllText(Path.Combine(project, "src", "b.ts"), "export const b = 1;\n");
        PlanTools.WorkSnapshot? work = await tools.SnapshotWorkAsync(plan, default);
        Assert.NotNull(work);

        // Step 2's attempt resets everything with git.
        await Must("git checkout HEAD -- src/a.ts", project);
        await Must("git clean -fq", project);

        IReadOnlyList<string> restored = await tools.RestoreDiscardedWorkAsync(plan, work, default);

        Assert.Equal("export const a = 2;\n", File.ReadAllText(Path.Combine(project, "src", "a.ts")));
        Assert.Equal("export const n = 1;\n", File.ReadAllText(Path.Combine(project, "src", "new.ts")));
        Assert.False(File.Exists(Path.Combine(project, "src", "b.ts")), "step 2's own file is step 2's to remove");
        Assert.Equal([Path.Combine("src", "a.ts"), Path.Combine("src", "new.ts")], restored.Order());

        // After a commit only deleted files come back: a file that matches the new commit is left as committed.
        work = await tools.SnapshotWorkAsync(plan, default);
        File.WriteAllText(Path.Combine(project, "src", "a.ts"), "export const a = 3;\n");
        await Must($"git add -A && {git} commit -qm wip", project);
        File.Delete(Path.Combine(project, "src", "new.ts"));
        Assert.Equal([Path.Combine("src", "new.ts")], await tools.RestoreDiscardedWorkAsync(plan, work, default));
        Assert.Equal("export const a = 3;\n", File.ReadAllText(Path.Combine(project, "src", "a.ts")));
    }

    private async Task<(PlanTools Tools, PlanRecord Plan)> RepoWithAsync(params (string Name, string Content)[] tracked)
    {
        Directory.CreateDirectory(Path.Combine(_project, "src"));
        foreach ((string name, string content) in tracked)
        {
            File.WriteAllText(Path.Combine(_project, name), content);
        }

        foreach (string command in new[] { "git init -q", "git add -A", "git -c user.name=t -c user.email=t@localhost commit -qm start" })
        {
            await Must(command, _project);
        }

        PlanRecord plan = Store.Create("Fix", "Fix it", _project, [], [], [], null, null,
            [new PlanStepInput("Fix it", "Fix it", ["src/a.ts"], "node -v", "light")]);
        Store.Approve(plan.Id, exportToProject: false);
        return (new PlanTools(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)), Run), Store.Get(plan.Id)!);
    }

    [Fact]
    public async Task Putting_the_project_back_to_a_checkpoint_restores_changed_new_and_deleted_files_and_nothing_else()
    {
        (PlanTools tools, PlanRecord plan) = await RepoWithAsync(
            ("src/a.ts", "a0\n"), ("src/b.ts", "b0\n"), ("src/c.ts", "c0\n"), ("src/gone.ts", "gone0\n"), ("README.md", "readme\n"));
        // Before the checkpoint: an earlier step's edit, this step's first try, a new file, and a tracked file deleted on purpose.
        File.WriteAllText(Path.Combine(_project, "README.md"), "readme, edited by an earlier step\n");
        File.WriteAllText(Path.Combine(_project, "src", "a.ts"), "a1\n");
        File.WriteAllText(Path.Combine(_project, "src", "new.ts"), "new1\n");
        File.Delete(Path.Combine(_project, "src", "gone.ts"));
        PlanTools.WorkSnapshot? best = await tools.SnapshotWorkAsync(plan, default);
        Assert.NotNull(best);

        // A worse round: more edits, a tracked file that was clean, a new file, a deletion, and the deleted file recreated.
        File.WriteAllText(Path.Combine(_project, "src", "a.ts"), "a2, worse\n");
        File.WriteAllText(Path.Combine(_project, "src", "b.ts"), "b1, worse\n");
        File.Delete(Path.Combine(_project, "src", "c.ts"));
        File.Delete(Path.Combine(_project, "src", "new.ts"));
        File.WriteAllText(Path.Combine(_project, "src", "new2.ts"), "new2\n");
        File.WriteAllText(Path.Combine(_project, "src", "gone.ts"), "recreated\n");

        PlanTools.WorkRestore restored = await tools.RestoreToSnapshotAsync(plan, best!, default);

        Assert.Null(restored.Refusal);
        Assert.Empty(restored.Left);
        Assert.Equal("a1\n", File.ReadAllText(Path.Combine(_project, "src", "a.ts")));
        // Git writes the files it restores with the platform's line endings.
        Assert.Equal("b0\n", File.ReadAllText(Path.Combine(_project, "src", "b.ts")).ReplaceLineEndings("\n"));
        Assert.Equal("c0\n", File.ReadAllText(Path.Combine(_project, "src", "c.ts")).ReplaceLineEndings("\n"));
        Assert.Equal("new1\n", File.ReadAllText(Path.Combine(_project, "src", "new.ts")));
        Assert.False(File.Exists(Path.Combine(_project, "src", "new2.ts")), "created after the checkpoint");
        Assert.False(File.Exists(Path.Combine(_project, "src", "gone.ts")), "it was deleted at the checkpoint");
        Assert.Equal("readme, edited by an earlier step\n", File.ReadAllText(Path.Combine(_project, "README.md")));
        Assert.Equal(["src/a.ts", "src/b.ts", "src/c.ts", "src/gone.ts", "src/new.ts", "src/new2.ts"], restored.Restored);

        // The project now looks exactly as it did at the checkpoint.
        PlanTools.WorkSnapshot? again = await tools.SnapshotWorkAsync(plan, default);
        Assert.Equal(best!.Files.Keys.Order(), again!.Files.Keys.Order());
        Assert.All(best.Files, pair => Assert.Equal(pair.Value, again.Files[pair.Key]));
        Assert.Equal(best.Deleted.Order(), again.Deleted.Order());
    }

    [Fact]
    public async Task A_project_that_was_committed_to_since_the_checkpoint_is_left_alone()
    {
        (PlanTools tools, PlanRecord plan) = await RepoWithAsync(("src/a.ts", "a0\n"));
        File.WriteAllText(Path.Combine(_project, "src", "a.ts"), "a1\n");
        PlanTools.WorkSnapshot? best = await tools.SnapshotWorkAsync(plan, default);
        await Must("git add -A && git -c user.name=t -c user.email=t@localhost commit -qm wip", _project);
        File.WriteAllText(Path.Combine(_project, "src", "a.ts"), "a2\n");

        PlanTools.WorkRestore restored = await tools.RestoreToSnapshotAsync(plan, best!, default);

        Assert.Contains("committed", restored.Refusal);
        Assert.Empty(restored.Restored);
        Assert.Equal("a2\n", File.ReadAllText(Path.Combine(_project, "src", "a.ts")));
    }

    [Fact]
    public async Task A_changed_file_too_big_to_keep_is_left_as_it_is_and_reported()
    {
        (PlanTools tools, PlanRecord plan) = await RepoWithAsync(("src/a.ts", "a0\n"), ("data.bin", "small\n"));
        File.WriteAllText(Path.Combine(_project, "src", "a.ts"), "a1\n");
        File.WriteAllBytes(Path.Combine(_project, "data.bin"), new byte[2_100_000]);
        PlanTools.WorkSnapshot? best = await tools.SnapshotWorkAsync(plan, default);
        Assert.EndsWith("data.bin", Assert.Single(best!.Uncaptured));

        File.WriteAllText(Path.Combine(_project, "src", "a.ts"), "a2\n");
        File.WriteAllBytes(Path.Combine(_project, "data.bin"), new byte[2_200_000]);
        PlanTools.WorkRestore restored = await tools.RestoreToSnapshotAsync(plan, best, default);

        Assert.Equal("a1\n", File.ReadAllText(Path.Combine(_project, "src", "a.ts")));
        Assert.Equal(["data.bin"], restored.Left);
        Assert.Equal(2_200_000, new FileInfo(Path.Combine(_project, "data.bin")).Length);
    }
}
