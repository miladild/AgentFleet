using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A test run that a step's check starts can leave files in the project folder. The runner removes the files that run created,
/// right after the check, so the step is not sent back for them. Files a round wrote before the check, and any run that cannot be
/// told apart from the project (no git repository), keep the old rule: the extra round for files the plan does not name.
/// </summary>
public sealed class PlanRunLeftoversTests : PlanTestBase
{
    private const string Pass = "Exit code: 0\n--- stdout ---\nok";
    private const string UnlistedKind = "unlisted-files";
    private static readonly FakeValidator Valid = new(code => new DiagramCheck(true, true, code, null));

    // A git repository with one committed file, so the runner's listings see real untracked files.
    private string Repository()
    {
        string repo = Path.Combine(PlansDirectory, "repo");
        Directory.CreateDirectory(repo);
        FailFirstTests.Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "old a");
        FailFirstTests.Git(repo, "add", "-A");
        FailFirstTests.Git(repo, "commit", "-q", "-m", "baseline");
        // Git stores its objects read-only, and Windows does not delete read-only files when the test folder is removed afterwards.
        foreach (string file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        return repo;
    }

    // Git's own answer for the commands the runner sends to git, in the shape the runner reads: the listings see what a run really made.
    internal static string RunGit(string command, string? directory)
    {
        string[] arguments = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return $"Exit code: {process.ExitCode}\n--- stdout ---\n{output}--- stderr ---\n{error.Result}";
    }

    private PlanRunner Runner(FakeStepAgent agent, Func<string, string, string> verify) =>
        new(Store, new PlanTools(Store, Valid, (command, directory, _) => Task.FromResult(
                command.StartsWith("git ", StringComparison.Ordinal) ? RunGit(command, directory) : verify(command, directory!))),
            agent, NullLogger.Instance, transientDelay: _ => TimeSpan.Zero, delayAsync: (_, _) => Task.CompletedTask);

    private PlanRecord OnePlan(string folder, string file) =>
        Store.Approve(Store.Create("Plan", "goal", folder, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("edit a", "detail", [file], "check", "standard")]).Id)!;

    // What a check that runs tests leaves behind: ten files in a data folder, as one test run did.
    private static string WriteTenDataFiles(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        for (int index = 0; index < 10; index++)
        {
            File.WriteAllText(Path.Combine(folder, "data", $"{index}.json"), "{}");
        }

        return Pass;
    }

    [Fact]
    public async Task A_check_that_writes_ten_files_passes_without_an_extra_round_and_the_files_are_removed()
    {
        string repo = Repository();
        PlanRecord plan = OnePlan(repo, "a.txt");
        var agent = new FakeStepAgent((_, _, _) =>
        {
            File.WriteAllText(Path.Combine(repo, "a.txt"), "new a");
            return Task.FromResult("Changed a.");
        });

        await Runner(agent, (_, folder) => WriteTenDataFiles(folder)).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Single(agent.Calls);
        Assert.False(Directory.Exists(Path.Combine(repo, "data")));
        Assert.Equal(0, after.Events!.Count(runEvent => runEvent.Kind == UnlistedKind));
        PlanRunEvent removal = Assert.Single(after.Events!, runEvent => runEvent.Kind == RunEventKind.LeftoversRemoved);
        Assert.Equal(10, removal.ChangedFiles?.Count);
        Assert.Contains("data/0.json", removal.ChangedFiles!, StringComparer.Ordinal);
        Assert.Contains(after.Events!, runEvent => runEvent.Kind == RunEventKind.StepDone && runEvent.Detail.Contains("data/0.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_the_round_wrote_before_the_check_is_kept_and_keeps_its_extra_round()
    {
        string repo = Repository();
        PlanRecord plan = OnePlan(repo, "a.txt");
        int round = 0;
        bool scratchKept = false;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(repo, "a.txt"), $"new {round}");
            if (round == 1)
            {
                File.WriteAllText(Path.Combine(repo, "scratch.js"), "try it");
            }
            else
            {
                scratchKept = File.Exists(Path.Combine(repo, "scratch.js"));
                File.Delete(Path.Combine(repo, "scratch.js"));
            }

            return Task.FromResult("Changed a.");
        });

        await Runner(agent, (_, folder) => WriteTenDataFiles(folder)).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(PlanStatus.Done, after.Status);
        Assert.Equal(2, agent.Calls.Count);
        Assert.True(scratchKept, "a file the round wrote before the check is not the runner's to remove");
        Assert.Contains("scratch.js", agent.Sessions[0].Messages[1].Text, StringComparison.Ordinal);
        Assert.Single(after.Events!, runEvent => runEvent.Kind == UnlistedKind);
        Assert.False(Directory.Exists(Path.Combine(repo, "data")));
    }

    [Fact]
    public async Task A_folder_that_is_not_a_git_repository_keeps_today_s_extra_round_and_removes_nothing()
    {
        string folder = Path.Combine(PlansDirectory, "plain");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "old a");
        PlanRecord plan = OnePlan(folder, "a.txt");
        int round = 0;
        bool filesKept = false;
        var agent = new FakeStepAgent((_, _, _) =>
        {
            round++;
            File.WriteAllText(Path.Combine(folder, "a.txt"), $"new {round}");
            if (round == 2)
            {
                filesKept = Directory.Exists(Path.Combine(folder, "data"));
                Directory.Delete(Path.Combine(folder, "data"), recursive: true);
            }

            return Task.FromResult("Changed a.");
        });

        await Runner(agent, (_, directory) => WriteTenDataFiles(directory)).RunPlanAsync(plan.Id, default);

        PlanRecord after = Store.Get(plan.Id)!;
        Assert.Equal(2, agent.Calls.Count);
        Assert.True(filesKept, "without a listing from git the files are left for the worker to delete");
        Assert.Single(after.Events!, runEvent => runEvent.Kind == UnlistedKind);
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.LeftoversRemoved);
        // A folder that is not a git repository has no listing by nature, so the skip is not written to the run log.
        Assert.DoesNotContain(after.Events!, runEvent => runEvent.Kind == RunEventKind.LeftoversSkipped);
    }

    [Fact]
    public async Task The_plan_tools_removal_takes_only_what_the_run_created_and_reports_nothing_outside_a_repository()
    {
        string repo = Repository();
        Directory.CreateDirectory(Path.Combine(repo, "data"));
        File.WriteAllText(Path.Combine(repo, "data", "kept.json"), "{}");
        var tools = new PlanTools(Store, Valid, (command, directory, _) => Task.FromResult(
            command.StartsWith("git ", StringComparison.Ordinal) ? RunGit(command, directory) : Pass));
        IReadOnlyList<string>? before = await tools.UntrackedFilesAsync(repo, default);
        Assert.NotNull(before);
        Assert.Contains("data/kept.json", before, StringComparer.Ordinal);

        File.WriteAllText(Path.Combine(repo, "data", "new.json"), "{}");
        LeftoverRemoval removal = await tools.RemoveTestRunLeftoversAsync(repo, before, [], default);

        Assert.Equal(["data/new.json"], removal.Removed);
        Assert.Null(removal.Skipped);
        Assert.True(File.Exists(Path.Combine(repo, "data", "kept.json")));

        string plain = Path.Combine(PlansDirectory, "plain-seam");
        Directory.CreateDirectory(plain);
        LeftoverRemoval unavailable = await tools.RemoveTestRunLeftoversAsync(plain, [], [], default);
        Assert.Empty(unavailable.Removed);
        Assert.NotNull(unavailable.Skipped);
    }
}
