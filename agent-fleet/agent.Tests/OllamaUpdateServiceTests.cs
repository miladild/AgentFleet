using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text;

namespace AgentFleet.Tests;

public sealed class OllamaUpdateServiceTests
{
    private const string Hub = "http://127.0.0.1:11434/";
    private const string Worker = "http://192.0.2.10:11434/";
    private const string Second = "http://192.0.2.11:11434/";

    private static async Task<OllamaUpdateStatus> FinishedAsync(OllamaUpdateService service, string url)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            if (!service.IsUpdating)
            {
                return service.List().Single(job => job.Url == url);
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The update did not finish.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 600 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The condition was not reached in time.");
    }

    private static async Task<OllamaUpdateException> RefusedAsync(Func<Task> action) => await Assert.ThrowsAsync<OllamaUpdateException>(action);

    private static OllamaUpdateFixture LinuxMachine(string installed = "0.12.0")
    {
        var fixture = new OllamaUpdateFixture();
        fixture.UseNodes(OllamaUpdateFixture.Node("worker", "http://192.0.2.10:11434/v1", OllamaUpdateFixture.Ssh()));
        fixture.Versions[Worker] = installed;
        // The start command is what brings the new version up on a real machine.
        fixture.Remote.Respond = command =>
        {
            if (command.StartsWith("printf", StringComparison.Ordinal))
            {
                return new RemoteResult(0, "/home/" + OllamaUpdateFixture.LinuxUser, string.Empty);
            }

            if (command == "sudo -n systemctl start ollama")
            {
                fixture.Versions[Worker] = OllamaUpdateFixture.Latest;
            }

            return new RemoteResult(0, string.Empty, string.Empty);
        };
        return fixture;
    }

    private static OllamaUpdateFixture WindowsHub(string installed = "0.12.0")
    {
        var fixture = new OllamaUpdateFixture();
        fixture.Versions[Hub] = installed;
        fixture.Host.AfterInstaller = () => fixture.Versions[Hub] = OllamaUpdateFixture.Latest;
        return fixture;
    }

    // ---- Asking GitHub ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_asks_GitHub_until_the_check_is_clicked()
    {
        var fixture = LinuxMachine();

        OllamaVersionsReport before = await fixture.Service.ListAsync(CancellationToken.None);

        Assert.Empty(fixture.Requests);
        Assert.Null(before.Latest);
        OllamaMachineStatus machine = Assert.Single(before.Machines);
        Assert.Equal("0.12.0", machine.Version);
        Assert.False(machine.CanUpdate);
        Assert.Equal("Check for updates first.", machine.BlockedReason);
    }

    [Fact]
    public async Task The_check_asks_for_the_latest_release_only_with_a_generic_agent_and_no_credentials()
    {
        var fixture = LinuxMachine();

        OllamaReleaseInfo info = await fixture.Service.CheckLatestAsync(CancellationToken.None);

        (string url, string agent, bool authorization) = Assert.Single(fixture.Seen);
        Assert.Equal("https://api.github.com/repos/ollama/ollama/releases/latest", url);
        Assert.StartsWith("agent-fleet/", agent);
        Assert.False(authorization);
        Assert.Equal("0.13.0", info.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), info.PublishedAtUtc);
        Assert.Equal(["OllamaSetup.exe", "ollama-linux-amd64.tar.zst", "Ollama-darwin.zip"], info.Assets.Select(asset => asset.Name));
        Assert.Equal(OllamaUpdateFixture.LinuxSha, info.Assets.Single(asset => asset.Name == "ollama-linux-amd64.tar.zst").Sha256);

        OllamaVersionsReport after = await fixture.Service.ListAsync(CancellationToken.None);
        Assert.Equal("0.13.0", after.Latest!.Version);
        Assert.Equal(info.CheckedAtUtc, after.Latest.CheckedAtUtc);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task A_pre_release_answer_from_GitHub_is_not_kept()
    {
        var fixture = LinuxMachine();
        fixture.ReleaseJson = OllamaUpdateFixture.Release("v0.14.0-rc1", prerelease: true);

        OllamaUpdateException refused = await RefusedAsync(() => fixture.Service.CheckLatestAsync(CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, refused.Status);
        Assert.Null((await fixture.Service.ListAsync(CancellationToken.None)).Latest);
    }

    [Fact]
    public async Task An_oversized_answer_from_GitHub_is_refused_unread()
    {
        var fixture = LinuxMachine();
        fixture.ReleaseJson = new string('x', 3 * 1024 * 1024);

        OllamaUpdateException refused = await RefusedAsync(() => fixture.Service.CheckLatestAsync(CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, refused.Status);
        Assert.Null((await fixture.Service.ListAsync(CancellationToken.None)).Latest);
    }

    [Fact]
    public async Task A_rate_limited_answer_from_GitHub_is_reported_and_nothing_is_kept()
    {
        var fixture = LinuxMachine();
        fixture.ReleaseStatus = HttpStatusCode.Forbidden;

        OllamaUpdateException refused = await RefusedAsync(() => fixture.Service.CheckLatestAsync(CancellationToken.None));

        Assert.Equal(StatusCodes.Status502BadGateway, refused.Status);
        Assert.Contains("HTTP 403", refused.Message);
        Assert.Null((await fixture.Service.ListAsync(CancellationToken.None)).Latest);
    }

    // ---- What is shown --------------------------------------------------------------------------------------

    [Fact]
    public async Task Nodes_that_share_an_address_are_listed_once_and_each_address_is_asked_once()
    {
        var fixture = new OllamaUpdateFixture();
        fixture.UseNodes(
            OllamaUpdateFixture.Node("worker", "http://192.0.2.10:11434/v1"),
            OllamaUpdateFixture.Node("eyes", "http://192.0.2.10:11434", vision: true),
            OllamaUpdateFixture.Node("small", "192.0.2.11"),
            OllamaUpdateFixture.Node("sleeper", "http://192.0.2.12:11434/v1", vision: true, visionMode: "off"));
        int asked = 0;
        fixture.Host.Probe = _ =>
        {
            Interlocked.Increment(ref asked);
            return "0.12.0";
        };

        OllamaVersionsReport report = await fixture.Service.ListAsync(CancellationToken.None);

        Assert.Equal([Worker, Second], report.Machines.Select(machine => machine.Url));
        Assert.Equal(["worker", "eyes"], report.Machines[0].Nodes);
        Assert.Equal(["small"], report.Machines[1].Nodes);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task A_machine_that_does_not_answer_is_listed_without_a_version_and_cannot_be_updated()
    {
        var fixture = LinuxMachine();
        fixture.Versions[Worker] = null;
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        OllamaMachineStatus machine = Assert.Single((await fixture.Service.ListAsync(CancellationToken.None)).Machines);

        Assert.False(machine.Answers);
        Assert.Null(machine.Version);
        Assert.False(machine.CanUpdate);
        Assert.Contains("does not answer", machine.BlockedReason);
    }

    [Theory]
    [InlineData("0.13.0")]
    [InlineData("0.13.1")]
    [InlineData("0.14.0")]
    public async Task A_machine_at_or_above_the_latest_version_is_never_offered_an_update(string installed)
    {
        var fixture = LinuxMachine(installed);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        OllamaMachineStatus machine = Assert.Single((await fixture.Service.ListAsync(CancellationToken.None)).Machines);
        OllamaUpdateException refused = await RefusedAsync(() => fixture.Service.StartAsync(Worker, CancellationToken.None));

        Assert.True(machine.UpToDate);
        Assert.False(machine.CanUpdate);
        Assert.Empty(machine.ManualSteps);
        Assert.Equal("Already up to date.", refused.Message);
        Assert.Equal(0, fixture.Remote.Opened);
    }

    [Fact]
    public async Task An_older_machine_with_a_method_can_be_updated_once_the_release_is_checked()
    {
        var fixture = LinuxMachine();
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        OllamaMachineStatus machine = Assert.Single((await fixture.Service.ListAsync(CancellationToken.None)).Machines);

        Assert.True(machine.CanUpdate);
        Assert.Null(machine.BlockedReason);
        Assert.Equal("ssh-linux", machine.UpdateMethod);
        Assert.Equal("linux", machine.Platform);
        Assert.Empty(machine.ManualSteps);
    }

    [Fact]
    public async Task A_machine_without_a_method_gets_the_manual_steps_for_its_platform_instead()
    {
        var fixture = new OllamaUpdateFixture();
        fixture.UseNodes(OllamaUpdateFixture.Node("worker", "http://192.0.2.10:11434/v1", platform: "windows"));
        fixture.Versions[Worker] = "0.12.0";
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        OllamaMachineStatus machine = Assert.Single((await fixture.Service.ListAsync(CancellationToken.None)).Machines);
        OllamaManualStep step = Assert.Single(machine.ManualSteps);

        Assert.False(machine.CanUpdate);
        Assert.Contains("No update method", machine.BlockedReason);
        Assert.Equal("windows", step.Platform);
        Assert.Equal("https://github.com/ollama/ollama/releases/download/v0.13.0/OllamaSetup.exe", step.Url);
        Assert.Equal(OllamaUpdateFixture.InstallerSha, step.Sha256);
        Assert.Equal([".\\OllamaSetup.exe /SILENT /NORESTART"], step.Commands);
    }

    [Fact]
    public async Task The_local_method_is_only_for_the_machine_the_fleet_runs_on_and_only_on_Windows()
    {
        var elsewhere = new OllamaUpdateFixture();
        elsewhere.UseNodes(OllamaUpdateFixture.Node("worker", "http://192.0.2.10:11434/v1", OllamaUpdateFixture.Method("local")));
        elsewhere.Versions[Worker] = "0.12.0";
        await elsewhere.Service.CheckLatestAsync(CancellationToken.None);
        Assert.Contains("only works for the machine the fleet runs on", Assert.Single((await elsewhere.Service.ListAsync(CancellationToken.None)).Machines).BlockedReason);

        var notWindows = WindowsHub();
        notWindows.Host.IsWindows = false;
        await notWindows.Service.CheckLatestAsync(CancellationToken.None);
        Assert.Contains("Windows only", Assert.Single((await notWindows.Service.ListAsync(CancellationToken.None)).Machines).BlockedReason);

        var service = WindowsHub();
        service.Host.IsWindowsService = true;
        await service.Service.CheckLatestAsync(CancellationToken.None);
        Assert.Contains("Windows service", Assert.Single((await service.Service.ListAsync(CancellationToken.None)).Machines).BlockedReason);
    }

    // ---- Refusals ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_update_before_the_check_is_refused()
    {
        var fixture = LinuxMachine();

        OllamaUpdateException refused = await RefusedAsync(() => fixture.Service.StartAsync(Worker, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
        Assert.Contains("Check for updates first", refused.Message);
        Assert.Equal(0, fixture.Remote.Opened);
    }

    [Fact]
    public async Task A_running_or_approved_plan_gives_a_409_and_nothing_is_touched()
    {
        var fixture = LinuxMachine();
        await fixture.Service.CheckLatestAsync(CancellationToken.None);
        fixture.PlanRunning = true;

        OllamaUpdateException refused = await RefusedAsync(() => fixture.Service.StartAsync(Worker, CancellationToken.None));
        OllamaUpdateException all = await RefusedAsync(() => fixture.Service.StartAsync(null, CancellationToken.None));
        OllamaMachineStatus machine = Assert.Single((await fixture.Service.ListAsync(CancellationToken.None)).Machines);

        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
        Assert.Equal(StatusCodes.Status409Conflict, all.Status);
        Assert.Contains("plan is running", refused.Message);
        Assert.False(machine.CanUpdate);
        Assert.Equal(0, fixture.Remote.Opened);
        Assert.Empty(fixture.Service.List());
    }

    [Fact]
    public async Task An_address_that_is_not_a_fleet_machine_or_not_an_address_is_a_400()
    {
        var fixture = LinuxMachine();
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        OllamaUpdateException stranger = await RefusedAsync(() => fixture.Service.StartAsync("http://203.0.113.9:11434", CancellationToken.None));
        OllamaUpdateException nonsense = await RefusedAsync(() => fixture.Service.StartAsync("ftp://x", CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, stranger.Status);
        Assert.Equal(StatusCodes.Status400BadRequest, nonsense.Status);
        Assert.Equal(0, fixture.Remote.Opened);
    }

    [Fact]
    public async Task A_file_without_a_digest_or_missing_from_the_release_is_refused_before_anything_is_opened_or_downloaded()
    {
        var noDigest = LinuxMachine();
        noDigest.ReleaseJson = OllamaUpdateFixture.Release("0.13.0", linuxDigest: "none");
        await noDigest.Service.CheckLatestAsync(CancellationToken.None);
        OllamaUpdateException refused = await RefusedAsync(() => noDigest.Service.StartAsync(Worker, CancellationToken.None));
        Assert.Contains("no SHA-256", refused.Message);
        Assert.Equal(0, noDigest.Remote.Opened);

        var noFile = LinuxMachine();
        noFile.ReleaseJson = OllamaUpdateFixture.Release("0.13.0", linuxAsset: false);
        await noFile.Service.CheckLatestAsync(CancellationToken.None);
        await RefusedAsync(() => noFile.Service.StartAsync(Worker, CancellationToken.None));
        Assert.Equal(0, noFile.Remote.Opened);

        var windows = WindowsHub();
        windows.ReleaseJson = OllamaUpdateFixture.Release("0.13.0", windowsDigest: "none");
        await windows.Service.CheckLatestAsync(CancellationToken.None);
        await RefusedAsync(() => windows.Service.StartAsync(Hub, CancellationToken.None));
        Assert.Single(windows.Requests);
        Assert.Empty(windows.Host.Events);
    }

    // ---- ssh-linux ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_linux_update_runs_exactly_these_commands_and_reports_the_new_version()
    {
        var fixture = LinuxMachine();
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Worker, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Worker);

        const string folder = "/home/fleet-updater/.ollama-update";
        const string file = folder + "/ollama-linux-amd64.tar.zst";
        Assert.Equal(
            [
                "sudo -n true",
                "command -v curl",
                "command -v zstd",
                "printf '%s' \"$HOME\"",
                $"mkdir -p -m 700 '{folder}'",
                $"curl -fsSL -o '{file}' 'https://github.com/ollama/ollama/releases/download/v0.13.0/ollama-linux-amd64.tar.zst'",
                $"echo '{OllamaUpdateFixture.LinuxSha}  {file}' | sha256sum -c -",
                "sudo -n systemctl stop ollama",
                "sudo -n rm -rf /usr/lib/ollama",
                $"sudo -n tar -I zstd -xf '{file}' -C /usr",
                "sudo -n systemctl start ollama",
                $"rm -f '{file}'"
            ],
            fixture.Remote.Commands);
        Assert.Equal(OllamaUpdateService.Done, job.State);
        Assert.Equal("0.12.0", job.FromVersion);
        Assert.Equal("0.13.0", job.TargetVersion);
        Assert.Equal("0.13.0", job.FinalVersion);
        Assert.Null(job.Error);
        Assert.Equal(["worker"], job.Nodes);
    }

    [Fact]
    public async Task A_digest_mismatch_on_linux_stops_before_anything_is_stopped_or_replaced()
    {
        var fixture = LinuxMachine();
        Func<string, RemoteResult> normal = fixture.Remote.Respond;
        fixture.Remote.Respond = command => command.Contains("sha256sum -c", StringComparison.Ordinal)
            ? new RemoteResult(1, string.Empty, "ollama-linux-amd64.tar.zst: FAILED")
            : normal(command);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Worker, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Worker);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains("SHA-256", job.Error);
        Assert.Contains("Nothing on that machine was changed", job.Error);
        Assert.DoesNotContain(fixture.Remote.Commands, command => command.Contains("systemctl") || command.Contains("rm -rf") || command.Contains("tar -I"));
        Assert.StartsWith("rm -f '", fixture.Remote.Commands[^1]);
        Assert.Equal("0.12.0", (await fixture.Service.ListAsync(CancellationToken.None)).Machines.Single().Version);
    }

    [Fact]
    public async Task A_failure_after_the_stop_starts_ollama_again_and_says_exactly_what_failed()
    {
        var fixture = LinuxMachine();
        Func<string, RemoteResult> normal = fixture.Remote.Respond;
        fixture.Remote.Respond = command => command.StartsWith("sudo -n tar", StringComparison.Ordinal)
            ? new RemoteResult(2, string.Empty, "tar: Unexpected EOF in archive")
            : normal(command);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Worker, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Worker);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains("unpacking the new files", job.Error);
        Assert.Contains("exit 2", job.Error);
        Assert.Contains("Unexpected EOF", job.Error);
        Assert.Contains("started again", job.Error);
        int tar = fixture.Remote.Commands.FindIndex(command => command.StartsWith("sudo -n tar", StringComparison.Ordinal));
        Assert.Equal("sudo -n systemctl start ollama", fixture.Remote.Commands[tar + 1]);
        Assert.StartsWith("rm -f '", fixture.Remote.Commands[^1]);
    }

    [Fact]
    public async Task When_starting_again_also_fails_the_message_says_ollama_may_be_down()
    {
        var fixture = LinuxMachine();
        fixture.Remote.Respond = command => command.StartsWith("printf", StringComparison.Ordinal)
            ? new RemoteResult(0, "/home/fleet-updater", string.Empty)
            : command.StartsWith("sudo -n rm -rf", StringComparison.Ordinal) || command == "sudo -n systemctl start ollama"
                ? new RemoteResult(1, string.Empty, "sudo: a password is required")
                : new RemoteResult(0, string.Empty, string.Empty);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Worker, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Worker);

        Assert.Contains("removing the old files", job.Error);
        Assert.Contains("may be stopped", job.Error);
        Assert.Contains("sudo systemctl start ollama", job.Error);
    }

    [Theory]
    [InlineData("sudo -n true", "cannot use sudo")]
    [InlineData("command -v curl", "curl is not installed")]
    [InlineData("command -v zstd", "zstd is not installed")]
    public async Task A_missing_requirement_is_reported_before_anything_is_downloaded(string failing, string expected)
    {
        var fixture = LinuxMachine();
        Func<string, RemoteResult> normal = fixture.Remote.Respond;
        fixture.Remote.Respond = command => command == failing ? new RemoteResult(1, string.Empty, string.Empty) : normal(command);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Worker, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Worker);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains(expected, job.Error);
        Assert.DoesNotContain(fixture.Remote.Commands, command => command.StartsWith("curl", StringComparison.Ordinal) || command.Contains("systemctl"));
    }

    [Theory]
    [InlineData("relative/home")]
    [InlineData("/home/a b")]
    [InlineData("/home/x;id")]
    [InlineData("/home/x/../../etc")]
    [InlineData("")]
    public async Task A_home_folder_with_odd_characters_is_refused_so_nothing_odd_enters_a_command(string home)
    {
        var fixture = LinuxMachine();
        fixture.Remote.Respond = command => command.StartsWith("printf", StringComparison.Ordinal)
            ? new RemoteResult(0, home, string.Empty)
            : new RemoteResult(0, string.Empty, string.Empty);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Worker, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Worker);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.DoesNotContain(fixture.Remote.Commands, command => command.StartsWith("mkdir", StringComparison.Ordinal) || command.StartsWith("curl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_machine_that_never_reports_the_new_version_is_a_failure_that_says_what_it_reports()
    {
        var fixture = LinuxMachine();
        Func<string, RemoteResult> normal = fixture.Remote.Respond;
        fixture.Remote.Respond = command => command == "sudo -n systemctl start ollama" ? new RemoteResult(0, string.Empty, string.Empty) : normal(command);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Worker, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Worker);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains("still reports version 0.12.0", job.Error);
        Assert.Null(job.FinalVersion);
    }

    // ---- local, Windows ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_local_update_downloads_verifies_stops_installs_and_reports_the_new_version()
    {
        var fixture = WindowsHub();
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Hub, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Hub);

        Assert.Equal(OllamaUpdateService.Done, job.State);
        Assert.Equal("0.13.0", job.FinalVersion);
        Assert.Equal("https://github.com/ollama/ollama/releases/download/v0.13.0/OllamaSetup.exe", fixture.Requests[^1]);
        Assert.Equal(["stop", "install"], fixture.Host.Events);
        Assert.Equal(["ollama app", "ollama"], fixture.Host.StoppedProcesses);
        Assert.Equal(["/SILENT", "/NORESTART"], fixture.Host.InstallerArguments);
        Assert.True(fixture.Host.InstallerFileExisted);
        Assert.False(File.Exists(fixture.Host.InstallerPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Host.InstallerPath)));
        Assert.Equal(0, fixture.Host.ServeStarts);
    }

    [Fact]
    public async Task A_download_whose_digest_does_not_match_is_never_run_and_the_machine_is_left_alone()
    {
        var fixture = WindowsHub();
        fixture.Download = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("tampered")) };
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Hub, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Hub);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains("SHA-256 does not match", job.Error);
        Assert.Empty(fixture.Host.Events);
        Assert.Null(fixture.Host.InstallerPath);
        Assert.Equal("0.12.0", fixture.Versions[Hub]);
    }

    [Fact]
    public async Task A_download_that_fails_leaves_the_machine_alone_and_no_temporary_folder_behind()
    {
        var fixture = WindowsHub();
        fixture.Download = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Hub, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Hub);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains("HTTP 404", job.Error);
        Assert.Empty(fixture.Host.Events);
    }

    [Fact]
    public async Task An_unexpected_error_fails_the_job_instead_of_leaving_it_running()
    {
        var fixture = WindowsHub();
        fixture.Host.AfterInstaller = () => throw new FormatException("something nobody expected");
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Hub, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Hub);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains("something nobody expected", job.Error);
        Assert.False(fixture.Service.IsUpdating);
    }

    [Fact]
    public async Task An_installer_that_fails_is_reported_and_ollama_is_started_when_nothing_answers()
    {
        var fixture = WindowsHub();
        fixture.Host.InstallerExit = 5;
        fixture.Host.AfterInstaller = () => fixture.Versions[Hub] = null;
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Hub, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Hub);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains("exited with code 5", job.Error);
        Assert.Contains("is not answering", job.Error);
        Assert.Equal(1, fixture.Host.ServeStarts);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Host.InstallerPath)));
    }

    [Fact]
    public async Task When_the_installer_finishes_but_nothing_answers_the_server_is_started_hidden_and_the_update_can_still_succeed()
    {
        var fixture = WindowsHub();
        fixture.Host.AfterInstaller = () => fixture.Versions[Hub] = null;
        fixture.Host.Probe = root =>
            fixture.Host.ServeStarts > 0 ? OllamaUpdateFixture.Latest : fixture.Versions.GetValueOrDefault(root.AbsoluteUri);
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Hub, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Hub);

        Assert.Equal(OllamaUpdateService.Done, job.State);
        Assert.Equal(1, fixture.Host.ServeStarts);
    }

    [Fact]
    public async Task A_machine_that_keeps_reporting_the_old_version_after_the_installer_is_a_failure()
    {
        var fixture = WindowsHub();
        fixture.Host.AfterInstaller = null;
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Hub, CancellationToken.None);
        OllamaUpdateStatus job = await FinishedAsync(fixture.Service, Hub);

        Assert.Equal(OllamaUpdateService.Failed, job.State);
        Assert.Contains("still reports version 0.12.0", job.Error);
        Assert.Equal(0, fixture.Host.ServeStarts);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Host.InstallerPath)));
    }

    // ---- One at a time ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_update_while_one_runs_is_refused()
    {
        var fixture = WindowsHub();
        var release = new TaskCompletionSource();
        fixture.Host.BeforeInstallerFinishes = () => release.Task;
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Hub, CancellationToken.None);
        await WaitUntilAsync(() => fixture.Host.InstallerPath is not null);
        OllamaUpdateException refused = await RefusedAsync(() => fixture.Service.StartAsync(Hub, CancellationToken.None));
        OllamaMachineStatus machine = Assert.Single((await fixture.Service.ListAsync(CancellationToken.None)).Machines);

        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
        Assert.Contains("already running", refused.Message);
        Assert.False(machine.CanUpdate);
        Assert.True((await fixture.Service.ListAsync(CancellationToken.None)).UpdateRunning);
        Assert.Equal(OllamaUpdateService.Running, Assert.Single(fixture.Service.List()).State);

        release.SetResult();
        Assert.Equal(OllamaUpdateService.Done, (await FinishedAsync(fixture.Service, Hub)).State);
    }

    [Fact]
    public async Task Update_all_goes_through_the_machines_one_at_a_time_and_skips_the_ones_that_need_nothing()
    {
        var fixture = new OllamaUpdateFixture();
        fixture.UseNodes(
            OllamaUpdateFixture.Node("one", "http://192.0.2.10:11434/v1", OllamaUpdateFixture.Ssh()),
            OllamaUpdateFixture.Node("two", "http://192.0.2.11:11434/v1", OllamaUpdateFixture.Ssh() with { Host = "192.0.2.11" }),
            OllamaUpdateFixture.Node("current", "http://192.0.2.12:11434/v1", OllamaUpdateFixture.Ssh() with { Host = "192.0.2.12" }),
            OllamaUpdateFixture.Node("manual", "http://192.0.2.13:11434/v1"));
        fixture.Versions[Worker] = "0.12.0";
        fixture.Versions[Second] = "0.11.5";
        fixture.Versions["http://192.0.2.12:11434/"] = "0.13.0";
        fixture.Versions["http://192.0.2.13:11434/"] = "0.10.0";
        fixture.Remote.Respond = command =>
        {
            if (command.StartsWith("printf", StringComparison.Ordinal))
            {
                return new RemoteResult(0, "/home/fleet-updater", string.Empty);
            }

            if (command == "sudo -n systemctl start ollama")
            {
                fixture.Versions[Worker] = OllamaUpdateFixture.Latest;
                fixture.Versions[Second] = OllamaUpdateFixture.Latest;
            }

            return new RemoteResult(0, string.Empty, string.Empty);
        };
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        IReadOnlyList<OllamaUpdateStatus> started = await fixture.Service.StartAsync(null, CancellationToken.None);
        await FinishedAsync(fixture.Service, Worker);

        Assert.Equal([Worker, Second], started.Select(job => job.Url));
        Assert.Equal([OllamaUpdateService.Done, OllamaUpdateService.Done], fixture.Service.List().Select(job => job.State));
        Assert.Equal(2, fixture.Remote.Opened);
        Assert.Equal(1, fixture.Remote.MostAtOnce);
        Assert.Equal(2, fixture.Remote.Commands.Count(command => command == "sudo -n systemctl stop ollama"));
    }

    [Fact]
    public async Task A_plan_that_starts_between_machines_stops_the_rest_of_the_run()
    {
        var fixture = new OllamaUpdateFixture();
        fixture.UseNodes(
            OllamaUpdateFixture.Node("one", "http://192.0.2.10:11434/v1", OllamaUpdateFixture.Ssh()),
            OllamaUpdateFixture.Node("two", "http://192.0.2.11:11434/v1", OllamaUpdateFixture.Ssh() with { Host = "192.0.2.11" }));
        fixture.Versions[Worker] = "0.12.0";
        fixture.Versions[Second] = "0.12.0";
        fixture.Remote.Respond = command =>
        {
            if (command.StartsWith("printf", StringComparison.Ordinal))
            {
                return new RemoteResult(0, "/home/fleet-updater", string.Empty);
            }

            if (command == "sudo -n systemctl start ollama")
            {
                fixture.Versions[Worker] = OllamaUpdateFixture.Latest;
                fixture.PlanRunning = true;
            }

            return new RemoteResult(0, string.Empty, string.Empty);
        };
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(null, CancellationToken.None);
        await FinishedAsync(fixture.Service, Worker);

        IReadOnlyList<OllamaUpdateStatus> jobs = fixture.Service.List();
        Assert.Equal(OllamaUpdateService.Done, jobs.Single(job => job.Url == Worker).State);
        OllamaUpdateStatus skipped = jobs.Single(job => job.Url == Second);
        Assert.Equal(OllamaUpdateService.Failed, skipped.State);
        Assert.Contains("plan started", skipped.Error);
        Assert.Equal(1, fixture.Remote.Opened);
    }

    [Fact]
    public async Task A_finished_update_raises_the_finished_event_once_per_machine()
    {
        var fixture = LinuxMachine();
        var finished = new List<OllamaUpdateStatus>();
        fixture.Service.Finished += finished.Add;
        await fixture.Service.CheckLatestAsync(CancellationToken.None);

        await fixture.Service.StartAsync(Worker, CancellationToken.None);
        await FinishedAsync(fixture.Service, Worker);

        OllamaUpdateStatus only = Assert.Single(finished);
        Assert.Equal(OllamaUpdateService.Done, only.State);
    }
}
