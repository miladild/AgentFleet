namespace AgentFleet.Tests;

public sealed class WorkerWorkspaceTests
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
}
