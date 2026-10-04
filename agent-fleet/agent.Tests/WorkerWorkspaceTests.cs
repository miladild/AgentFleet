namespace AgentFleet.Tests;

public sealed class WorkerWorkspaceTests : PlanTestBase
{
    [Fact]
    public void Windows_sftp_link_checks_skip_the_drive_pseudo_directory()
    {
        string[] paths = WorkerWorkspacePath.PathsForLinkCheck(
            "/C:/fleet-test/workspaces/agentfleet/plans/canary/worker-a/project",
            "windows");

        Assert.Equal(
            ["/C:/fleet-test", "/C:/fleet-test/workspaces", "/C:/fleet-test/workspaces/agentfleet",
             "/C:/fleet-test/workspaces/agentfleet/plans", "/C:/fleet-test/workspaces/agentfleet/plans/canary",
             "/C:/fleet-test/workspaces/agentfleet/plans/canary/worker-a", "/C:/fleet-test/workspaces/agentfleet/plans/canary/worker-a/project"],
            paths);
    }

    [Fact]
    public void Linux_sftp_link_checks_walk_from_the_root()
    {
        string[] paths = WorkerWorkspacePath.PathsForLinkCheck("/home/agentfleet/workspaces/project", "linux");

        Assert.Equal(["/home", "/home/agentfleet", "/home/agentfleet/workspaces", "/home/agentfleet/workspaces/project"], paths);
    }

    [Fact]
    public void Maps_hub_absolute_windows_paths_into_the_staged_project()
    {
        string relative = WorkerWorkspacePath.RelativeToProject(
            @"C:\fleet-test\project\src\server\marketHours.ts",
            @"C:\fleet-test\project");

        Assert.Equal("src/server/marketHours.ts", relative);
    }

    [Fact]
    public void Maps_worker_absolute_paths_back_to_project_relative_paths()
    {
        string relative = WorkerWorkspacePath.RelativeToProject(
            "/home/agentfleet/workspaces/agentfleet/plans/abc/project/src/app.ts",
            @"C:\fleet-test\project",
            "/home/agentfleet/workspaces/agentfleet/plans/abc/project");

        Assert.Equal("src/app.ts", relative);
    }

    [Fact]
    public void Windows_command_chains_run_through_cmd_with_powershell_safe_quoting()
    {
        string command = WorkerWorkspaceSession.BuildWindowsCommand(
            @"C:\worker workspace\project", "findstr /x OK result.txt && echo O'Brien");

        Assert.Equal(@"cmd.exe /d /s /c 'cd /d ""C:\worker workspace\project"" && findstr /x OK result.txt && echo O''Brien'", command);
    }

    [Fact]
    public void Windows_single_commands_keep_the_default_powershell_shell()
    {
        string command = WorkerWorkspaceSession.BuildWindowsCommand(@"C:\worker's workspace\project", "Get-Content seed.txt");

        Assert.Equal("Set-Location -LiteralPath 'C:\\worker''s workspace\\project' -ErrorAction Stop; [Environment]::CurrentDirectory = (Get-Location).ProviderPath; Get-Content seed.txt", command);
    }

    [Fact]
    public void Windows_powershell_commands_translate_the_sftp_drive_root()
    {
        string command = WorkerWorkspaceSession.BuildWindowsCommand("/C:/worker/workspaces/project", "Get-Content seed.txt");

        Assert.Equal("Set-Location -LiteralPath 'C:\\worker\\workspaces\\project' -ErrorAction Stop; [Environment]::CurrentDirectory = (Get-Location).ProviderPath; Get-Content seed.txt", command);
    }

    [Theory]
    [InlineData("../secrets.txt", @"C:\fleet-test\project")]
    [InlineData("src/../../secrets.txt", @"C:\fleet-test\project")]
    [InlineData("C:/test-user/.ssh/id_ed25519", @"C:\fleet-test\project")]
    [InlineData("/etc/shadow", "/home/fleet-test/project")]
    public void Refuses_paths_that_escape_the_project(string path, string hubRoot)
    {
        Assert.Throws<UnauthorizedAccessException>(() => WorkerWorkspacePath.RelativeToProject(path, hubRoot));
    }

    [Theory]
    [InlineData("nul")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("src/con")]
    [InlineData("aux.js")]
    [InlineData("prn")]
    [InlineData("com1")]
    [InlineData("lpt9.log")]
    [InlineData("nul.")]
    [InlineData("nul ")]
    [InlineData("src/nul/index.js")]
    public void A_name_Windows_reserves_is_not_synced_from_a_worker_to_a_Windows_hub(string relativePath)
    {
        // A Linux worker can create it (a command ending "> nul" did); a Unix hub can hold it, a Windows hub cannot.
        Assert.Equal(!OperatingSystem.IsWindows(), WorkerWorkspaceSession.IsAllowedProjectFile(relativePath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("nullable.js")]
    [InlineData("src/console.js")]
    [InlineData("auxiliary.txt")]
    [InlineData("com10")]
    [InlineData("lpt")]
    public void A_name_that_only_looks_like_a_reserved_one_is_synced(string relativePath)
    {
        Assert.True(WorkerWorkspaceSession.IsAllowedProjectFile(relativePath));
    }

    [Theory]
    [InlineData(".env", false)]
    [InlineData("src/.ssh/id_ed25519", false)]
    [InlineData("certs/dev.pem", false)]
    [InlineData(".npmrc", false)]
    [InlineData("AGENTS.md", false)]
    [InlineData("src/marketHours.ts", true)]
    public void Filters_private_files_from_worker_staging_and_prompt_context(string relativePath, bool allowed)
    {
        Assert.Equal(allowed, WorkerWorkspaceSession.IsAllowedProjectFile(relativePath));
    }

    [Fact]
    public void A_script_staged_on_a_linux_worker_keeps_the_right_to_run_and_arrives_whole()
    {
        string script = WriteHubFile("check.sh", "#!/bin/sh\necho ok\n", executable: true);

        (string Uploaded, List<(string Path, short Mode)> Modes) result = StageOne(script, linuxWorker: true);

        Assert.Equal("#!/bin/sh\necho ok\n", result.Uploaded);
        Assert.Equal([("/work/check.sh", (short)755)], result.Modes);
    }

    [Fact]
    public void A_plain_file_staged_on_a_linux_worker_is_left_as_the_upload_made_it()
    {
        string file = WriteHubFile("notes.txt", "hello\n", executable: false);

        (string Uploaded, List<(string Path, short Mode)> Modes) result = StageOne(file, linuxWorker: true);

        Assert.Equal("hello\n", result.Uploaded);
        Assert.Empty(result.Modes);
    }

    [Fact]
    public void An_empty_file_staged_on_a_linux_worker_is_not_taken_for_a_script()
    {
        string file = WriteHubFile("empty.txt", "", executable: false);

        (string Uploaded, List<(string Path, short Mode)> Modes) result = StageOne(file, linuxWorker: true);

        Assert.Equal("", result.Uploaded);
        Assert.Empty(result.Modes);
    }

    [Fact]
    public void A_windows_worker_never_gets_a_mode_change()
    {
        string script = WriteHubFile("check.sh", "#!/bin/sh\necho ok\n", executable: true);

        (string Uploaded, List<(string Path, short Mode)> Modes) result = StageOne(script, linuxWorker: false);

        Assert.Equal("#!/bin/sh\necho ok\n", result.Uploaded);
        Assert.Empty(result.Modes);
    }

    // A Windows hub has no execute bit, so a script is told by its "#!" line; a Unix hub's own mode decides.
    private string WriteHubFile(string name, string content, bool executable)
    {
        string path = Path.Combine(PlansDirectory, name);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, executable ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    private static (string Uploaded, List<(string Path, short Mode)> Modes) StageOne(string source, bool linuxWorker)
    {
        string uploaded = "";
        var modes = new List<(string Path, short Mode)>();
        WorkerWorkspaceSession.UploadKeepingMode(
            source, "/work/" + Path.GetFileName(source), linuxWorker,
            (stream, _) => uploaded = new StreamReader(stream).ReadToEnd(),
            (path, mode) => modes.Add((path, mode)));
        return (uploaded, modes);
    }

    [Fact]
    public void Restart_reconciliation_accepts_an_unchanged_worker_snapshot()
    {
        PlanRecord plan = NewPlan(new PlanStepInput("recover", "detail", ["a.cs"], "dotnet build", "standard"));
        PlanStep step = plan.Steps.Single();
        var hashes = new Dictionary<string, string> { ["a.cs"] = "baseline" };

        WorkerWorkspaceRecoveryAnalysis result = WorkerWorkspaceSession.AnalyzeInterruptedFiles(plan, step, hashes, hashes, hashes);

        Assert.True(result.Safe);
        Assert.Empty(result.ChangedFiles);
        Assert.Empty(result.FilesToApply);
    }

    [Fact]
    public void Staged_worker_hashes_survive_as_private_plan_store_state()
    {
        PlanRecord plan = NewPlan(Step("recover"));
        var hashes = new Dictionary<string, string> { ["a.cs"] = "synthetic-hash" };

        Store.SaveWorkerBaseline(plan.Id, 1, 2, "worker-a", hashes);
        WorkerWorkspaceBaseline? saved = Store.GetWorkerBaseline(plan.Id, 1, 2);

        Assert.NotNull(saved);
        Assert.Equal("worker-a", saved.Machine);
        Assert.Equal(hashes, saved.Files);
        Store.DeleteWorkerBaseline(plan.Id, 1, 2);
        Assert.Null(Store.GetWorkerBaseline(plan.Id, 1, 2));
    }

    [Fact]
    public void Restart_reconciliation_recovers_declared_worker_edits_when_the_hub_is_unchanged()
    {
        PlanRecord plan = NewPlan(new PlanStepInput("recover", "detail", ["a.cs"], "dotnet build", "standard"));
        PlanStep step = plan.Steps.Single();
        var baseline = new Dictionary<string, string> { ["a.cs"] = "baseline" };
        var remote = new Dictionary<string, string> { ["a.cs"] = "worker-edit" };
        var hub = new Dictionary<string, string> { ["a.cs"] = "baseline" };

        WorkerWorkspaceRecoveryAnalysis result = WorkerWorkspaceSession.AnalyzeInterruptedFiles(plan, step, baseline, remote, hub);

        Assert.True(result.Safe);
        Assert.Equal(["a.cs"], result.ChangedFiles);
        Assert.Equal(["a.cs"], result.FilesToApply);
    }

    [Fact]
    public void Restart_reconciliation_blocks_conflicting_or_undeclared_worker_edits()
    {
        PlanRecord plan = NewPlan(new PlanStepInput("recover", "detail", ["a.cs"], "dotnet build", "standard"));
        PlanStep step = plan.Steps.Single();
        var baseline = new Dictionary<string, string> { ["a.cs"] = "baseline", ["b.cs"] = "baseline-b" };
        var remote = new Dictionary<string, string> { ["a.cs"] = "worker-edit", ["b.cs"] = "worker-edit-b" };
        var hub = new Dictionary<string, string> { ["a.cs"] = "hub-edit", ["b.cs"] = "baseline-b" };

        WorkerWorkspaceRecoveryAnalysis result = WorkerWorkspaceSession.AnalyzeInterruptedFiles(plan, step, baseline, remote, hub);

        Assert.False(result.Safe);
        Assert.Equal(["a.cs", "b.cs"], result.Conflicts);
        Assert.Empty(result.FilesToApply);
    }

    // worker's copy, the hub's copy (null: the hub no longer has the file), what the worker held at the last sync (null: none recorded)
    [Theory]
    [InlineData("w", "w", null, "Matches")]
    [InlineData("w", "w", "older", "Matches")]
    // The runner rolled a round back: the hub holds the older content, the worker is as it was synced.
    [InlineData("worse", "better", "worse", "Replace")]
    // The worker's file was changed after the last sync: that is unsynced work, whatever the hub holds.
    [InlineData("edited", "better", "worse", "Refuse")]
    // Nothing is known of the worker's copy (a restart): refuse, as before.
    [InlineData("worse", "better", null, "Refuse")]
    // The file is gone from the hub (a rollback to before it existed): removed only when it is as it was synced.
    [InlineData("worse", null, "worse", "Remove")]
    [InlineData("edited", null, "worse", "Refuse")]
    [InlineData("worse", null, null, "Refuse")]
    public void An_existing_worker_file_is_replaced_only_when_nobody_touched_it_since_the_last_sync(
        string worker, string? hub, string? lastSynced, string expected)
    {
        Assert.Equal(expected, WorkerWorkspaceSession.JudgeExistingWorkerFile(worker, hub, lastSynced).ToString());
    }
}
