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

    // A setup command that must work; its own output is the failure message when it does not.
    private static async Task Must(string command, string project)
    {
        string result = await Run(command, project, default);
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
}
