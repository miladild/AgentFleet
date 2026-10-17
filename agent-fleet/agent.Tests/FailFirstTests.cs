using System.Diagnostics;
using AgentFleet;
using Microsoft.Extensions.Logging;

namespace AgentFleet.Tests;

public sealed class FailFirstTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fleet-failfirst-" + Guid.NewGuid().ToString("N")));

    public FailFirstTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DeleteTree(_root);

    internal static void DeleteTree(string root)
    {
        if (!Directory.Exists(root)) return;
        // Git stores its objects read-only, and Windows does not delete read-only files.
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Overlay_restores_old_code_removes_new_files_and_leaves_tests_untouched()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        string changed = Path.Combine(root, "src", "changed.cs");
        string headOnly = Path.Combine(root, "src", "head.cs");
        string created = Path.Combine(root, "src", "created.cs");
        string deleted = Path.Combine(root, "src", "deleted.cs");
        string test = Path.Combine(root, "tests", "FeatureTests.cs");
        var snapshot = new PlanTools.WorkSnapshot("base", new Dictionary<string, byte[]> { [changed] = "old"u8.ToArray() },
            new HashSet<string> { created }, new HashSet<string>(), new HashSet<string>());

        IReadOnlyDictionary<string, byte[]?> overlay = FailFirst.CreateOverlay(snapshot,
            [changed, headOnly, created, deleted, test], root,
            (_, path) => path == "src/head.cs" ? HeadFile.Of("from-head"u8.ToArray()) : HeadFile.Of("deleted-old"u8.ToArray()))!;

        Assert.Equal("old", System.Text.Encoding.UTF8.GetString(overlay["src/changed.cs"]!));
        Assert.Equal("from-head", System.Text.Encoding.UTF8.GetString(overlay["src/head.cs"]!));
        Assert.Null(overlay["src/created.cs"]);
        Assert.Equal("deleted-old", System.Text.Encoding.UTF8.GetString(overlay["src/deleted.cs"]!));
        Assert.DoesNotContain("tests/FeatureTests.cs", overlay.Keys);
    }

    [Fact]
    public void A_file_that_was_deleted_before_the_step_stays_absent_in_the_overlay()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        string gone = Path.Combine(root, "src", "gone.cs");
        var snapshot = new PlanTools.WorkSnapshot("base", new Dictionary<string, byte[]>(), new HashSet<string>(),
            new HashSet<string> { gone }, new HashSet<string>());

        IReadOnlyDictionary<string, byte[]?> overlay = FailFirst.CreateOverlay(snapshot, [gone], root,
            (_, _) => HeadFile.Of("from-head"u8.ToArray()))!;

        Assert.True(overlay.ContainsKey("src/gone.cs"));
        Assert.Null(overlay["src/gone.cs"]);
    }

    [Fact]
    public void Overlay_is_unavailable_when_snapshot_has_uncaptured_files()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var snapshot = new PlanTools.WorkSnapshot("base", new Dictionary<string, byte[]>(), new HashSet<string>(),
            new HashSet<string>(), new HashSet<string> { Path.Combine(root, "large.cs") });

        Assert.Null(FailFirst.CreateOverlay(snapshot, [Path.Combine(root, "large.cs")], root, (_, _) => HeadFile.Missing));
    }

    [Fact]
    public void Overlay_is_unavailable_when_a_committed_file_cannot_be_read_so_it_is_never_treated_as_absent()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        string file = Path.Combine(root, "src", "kept.cs");
        var snapshot = new PlanTools.WorkSnapshot("base", new Dictionary<string, byte[]>(), new HashSet<string>(),
            new HashSet<string>(), new HashSet<string>());

        Assert.Null(FailFirst.CreateOverlay(snapshot, [file], root, (_, _) => HeadFile.Broken));
    }

    [Fact]
    public void Reading_a_committed_file_returns_the_bytes_it_had_in_that_commit()
    {
        string repo = CreateRepository();
        string committed = "committed line\n";
        File.WriteAllText(Path.Combine(repo, "a.txt"), committed);
        Git(repo, "add", "a.txt");
        Git(repo, "commit", "-qm", "start");
        string head = Git(repo, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(repo, "a.txt"), "changed after the commit\n");

        HeadFile result = FailFirst.ReadHead(repo, head, "a.txt");

        Assert.Equal(HeadFileState.Present, result.State);
        Assert.Equal(committed, System.Text.Encoding.UTF8.GetString(result.Content!));
    }

    [Fact]
    public void A_file_created_after_the_commit_is_absent_at_that_commit()
    {
        string repo = CreateRepository();
        File.WriteAllText(Path.Combine(repo, "a.txt"), "committed\n");
        Git(repo, "add", "a.txt");
        Git(repo, "commit", "-qm", "start");
        string head = Git(repo, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(repo, "created.txt"), "new\n");

        HeadFile result = FailFirst.ReadHead(repo, head, "created.txt");

        Assert.Equal(HeadFileState.Absent, result.State);
        Assert.Null(result.Content);
    }

    [Fact]
    public void A_commit_id_git_does_not_know_is_a_failed_read_not_an_absent_file()
    {
        string repo = CreateRepository();
        File.WriteAllText(Path.Combine(repo, "a.txt"), "committed\n");
        Git(repo, "add", "a.txt");
        Git(repo, "commit", "-qm", "start");

        HeadFile result = FailFirst.ReadHead(repo, "0123456789abcdef0123456789abcdef01234567", "a.txt");

        Assert.Equal(HeadFileState.Failed, result.State);
    }

    [Fact]
    public void A_folder_that_is_not_a_repository_is_a_failed_read()
    {
        string plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.Equal(HeadFileState.Failed, FailFirst.ReadHead(plain, "HEAD", "a.txt").State);
    }

    [Fact]
    public void A_folder_at_the_commit_is_not_read_as_a_file()
    {
        string repo = CreateRepository();
        Directory.CreateDirectory(Path.Combine(repo, "folder"));
        File.WriteAllText(Path.Combine(repo, "folder", "inner.txt"), "inner\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-qm", "start");
        string head = Git(repo, "rev-parse", "HEAD").Trim();

        Assert.Equal(HeadFileState.Failed, FailFirst.ReadHead(repo, head, "folder").State);
    }

    private string CreateRepository()
    {
        string repo = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        return repo;
    }

    // A virus scanner can hold a freshly written git object for a moment ("Permission denied" while writing .git/objects),
    // so that one failure is tried again, as the restore tests do.
    internal static string Git(string directory, params string[] arguments)
    {
        string error = string.Empty;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("user.name=test");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("user.email=test@localhost");
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> errorText = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            error = errorText.Result;
            if (process.ExitCode == 0) return output;
            if (!error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) break;
            Thread.Sleep(200 * attempt);
        }

        Assert.Fail($"git {string.Join(' ', arguments)} failed: {error}");
        return string.Empty;
    }

    // Runs a shell command the way the restore tests do, so the runner's git probes see the real repository.
    internal static async Task<string> RunShell(string command, string? directory, CancellationToken cancellationToken)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c {command}")
            : new ProcessStartInfo("/bin/sh", ["-c", command]);
        start.WorkingDirectory = directory ?? Environment.CurrentDirectory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process process = Process.Start(start)!;
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return $"Exit code: {process.ExitCode}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}";
    }

    [Fact]
    public void Windows_copy_script_uses_the_powershell_exit_code_variable_and_treats_robocopy_codes_up_to_seven_as_success()
    {
        string copy = WorkerWorkspaceSession.BuildScratchCopyCommand("windows", "C:/agentfleet/plans/p1/m/project",
            "C:/agentfleet/plans/p1/m/project-scratch");

        Assert.Contains("$LASTEXITCODE", copy);
        Assert.DoesNotContain("$$", copy);
        Assert.Contains("robocopy", copy);
        Assert.Contains(" /E ", copy);
        Assert.Contains("-gt 7", copy);
        Assert.EndsWith("exit 0", copy);
    }

    [Fact]
    public void Windows_scripts_escape_an_apostrophe_in_the_path()
    {
        const string root = "C:/agentfleet/O'Brien/project";
        string scratch = WorkerWorkspaceSession.ScratchPath(root);

        string copy = WorkerWorkspaceSession.BuildScratchCopyCommand("windows", root, scratch);
        string delete = WorkerWorkspaceSession.BuildScratchDeleteCommand("windows", scratch);

        Assert.Contains(@"'C:\agentfleet\O''Brien\project'", copy);
        Assert.Contains(@"'C:\agentfleet\O''Brien\project-scratch'", copy);
        Assert.Contains(@"'C:\agentfleet\O''Brien\project-scratch'", delete);
    }

    [Fact]
    public void Posix_copy_quotes_both_paths_and_copies_with_archive_mode()
    {
        const string root = "/srv/worker/agentfleet/plans/p1/m/project";
        string scratch = WorkerWorkspaceSession.ScratchPath(root);

        Assert.Equal($"rm -rf -- '{root}-scratch' && cp -a -- '{root}' '{root}-scratch'",
            WorkerWorkspaceSession.BuildScratchCopyCommand("linux", root, scratch));
        Assert.Contains(@"cp -a -- '/srv/O'\''Brien/project' ", WorkerWorkspaceSession.BuildScratchCopyCommand("linux",
            "/srv/O'Brien/project", WorkerWorkspaceSession.ScratchPath("/srv/O'Brien/project")));
    }

    [Theory]
    [InlineData("windows", "C:/agentfleet/plans/p1/m/project")]
    [InlineData("linux", "/srv/worker/agentfleet/plans/p1/m/project")]
    public void Delete_commands_only_ever_name_the_scratch_folder(string platform, string root)
    {
        string scratch = WorkerWorkspaceSession.ScratchPath(root);
        string delete = WorkerWorkspaceSession.BuildScratchDeleteCommand(platform, scratch);
        string quotedRoot = platform == "windows" ? @"'C:\agentfleet\plans\p1\m\project'" : $"'{root}'";
        string quotedScratch = platform == "windows" ? @"'C:\agentfleet\plans\p1\m\project-scratch'" : $"'{root}-scratch'";

        Assert.DoesNotContain(quotedRoot, delete);
        Assert.Contains(quotedScratch, delete);
    }

    [Theory]
    [InlineData("/srv/a/project")]
    [InlineData("/srv/a/project/")]
    [InlineData("C:/agentfleet/project")]
    [InlineData("C:\\agentfleet\\project")]
    public void The_scratch_folder_is_a_sibling_of_the_plan_folder_and_never_inside_it(string root)
    {
        string trimmed = root.TrimEnd('/', '\\');

        string scratch = WorkerWorkspaceSession.ScratchPath(root);

        Assert.Equal(trimmed + "-scratch", scratch);
        Assert.NotEqual(trimmed, scratch);
        Assert.False(scratch.StartsWith(trimmed + "/", StringComparison.Ordinal));
        Assert.False(scratch.StartsWith(trimmed + "\\", StringComparison.Ordinal));
    }

    [Fact]
    public void A_root_with_no_folder_name_has_no_scratch_folder()
    {
        Assert.Throws<ArgumentException>(() => WorkerWorkspaceSession.ScratchPath("/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("off")]
    [InlineData("OFF")]
    public void Unset_empty_and_off_are_off_without_a_warning(string? value)
    {
        var logger = new WarningLogger();

        Assert.Equal(FailFirstMode.Off, FailFirst.ParseMode(value, logger));
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("warn", "Warn")]
    [InlineData("WARN", "Warn")]
    [InlineData(" enforce ", "Enforce")]
    [InlineData("Enforce", "Enforce")]
    public void Warn_and_enforce_are_read_without_regard_to_case_and_surrounding_spaces(string value, string expected)
    {
        var logger = new WarningLogger();

        Assert.Equal(expected, FailFirst.ParseMode(value, logger).ToString());
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("enforced")]
    [InlineData("1")]
    public void An_unknown_value_is_off_and_logs_one_warning_that_names_the_accepted_values(string value)
    {
        var logger = new WarningLogger();

        Assert.Equal(FailFirstMode.Off, FailFirst.ParseMode(value, logger));
        string warning = Assert.Single(logger.Warnings);
        Assert.Contains("off, warn, enforce", warning, StringComparison.Ordinal);
        Assert.Contains(value, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_value_with_no_logger_is_still_off()
    {
        Assert.Equal(FailFirstMode.Off, FailFirst.ParseMode("maybe", null));
    }

    // Keeps the warnings a test expects to see, and nothing else.
    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
