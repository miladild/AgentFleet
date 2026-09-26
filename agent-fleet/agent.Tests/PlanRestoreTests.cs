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
            Assert.StartsWith("Exit code: 0", await Run(command, project, default));
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
}
