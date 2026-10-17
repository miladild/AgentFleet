using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

internal sealed record FailFirstResult(string Outcome, IReadOnlyList<string> TestFiles, int? ExitCode, double DurationSeconds = 0);

/// <summary>What a file held at one commit: its bytes, nothing (the path is not in that commit), or a read that failed.</summary>
internal sealed record HeadFile(HeadFileState State, byte[]? Content = null)
{
    public static HeadFile Of(byte[] content) => new(HeadFileState.Present, content);

    public static HeadFile Missing { get; } = new(HeadFileState.Absent);

    public static HeadFile Broken { get; } = new(HeadFileState.Failed);
}

internal enum HeadFileState
{
    Present,
    Absent,
    Failed
}

/// <summary>The FLEET_PLAN_FAIL_FIRST setting, read once at startup and passed to the plan runner.</summary>
internal enum FailFirstMode
{
    Off,
    Warn,
    Enforce
}

internal static class FailFirst
{
    /// <summary>
    /// Parses the FLEET_PLAN_FAIL_FIRST setting. Unset, empty and "off" are off; "warn" and "enforce" are read without regard to case.
    /// Any other value is ignored with one warning, and the mode is off.
    /// </summary>
    internal static FailFirstMode ParseMode(string? value, ILogger? logger)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
            return FailFirstMode.Off;
        if (string.Equals(trimmed, "warn", StringComparison.OrdinalIgnoreCase))
            return FailFirstMode.Warn;
        if (string.Equals(trimmed, "enforce", StringComparison.OrdinalIgnoreCase))
            return FailFirstMode.Enforce;

        logger?.LogWarning("Unknown FLEET_PLAN_FAIL_FIRST value '{Value}'. Accepted values are: off, warn, enforce. Ignoring it and using off.", value);
        return FailFirstMode.Off;
    }

    // Git says "does not exist in" or "exists on disk, but not in" for a path that is not in the commit. Git exits 128 for that
    // and for every other failure too, so only this message counts as absent; a bad commit or a missing repository is a failure.
    /// <summary>The run event's detail: a camel-case JSON object, as the run log's other evidence is written.</summary>
    internal static string Evidence(FailFirstResult result) => JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static readonly Regex PathNotInCommit =new("^fatal: path '.*' (does not exist in|exists on disk, but not in) '", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The new overlay, or null when it cannot be known: an uncaptured file, or a file at the step's start that could not be read.
    /// Null means the step's check is not run on the old code, so a failed read never looks like a file that did not exist.
    /// </summary>
    internal static IReadOnlyDictionary<string, byte[]?>? CreateOverlay(
        PlanTools.WorkSnapshot snapshot,
        IReadOnlyList<string> changedFiles,
        string projectDirectory,
        Func<string, string, HeadFile> readHead)
    {
        var overlay = new Dictionary<string, byte[]?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string changedPath in changedFiles.Where(path => !PlanRunner.IsTestPath(path)))
        {
            string path = Path.GetFullPath(Path.IsPathRooted(changedPath) ? changedPath : Path.Combine(projectDirectory, changedPath));
            if (snapshot.Uncaptured.Contains(path)) return null;
            string relative = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
            if (snapshot.Files.TryGetValue(path, out byte[]? previous))
            {
                overlay[relative] = previous;
            }
            else if (snapshot.Untracked.Contains(path) || snapshot.Deleted.Contains(path))
            {
                // Not on disk at the step's start: the old code has no such file.
                overlay[relative] = null;
            }
            else
            {
                HeadFile head = readHead(snapshot.Head, relative);
                switch (head.State)
                {
                    case HeadFileState.Present:
                        overlay[relative] = head.Content;
                        break;
                    case HeadFileState.Absent:
                        overlay[relative] = null;
                        break;
                    default:
                        return null;
                }
            }
        }
        return overlay;
    }

    /// <summary>The one reader of a file's content at a commit (the step's start), used by <see cref="CreateOverlay"/>.</summary>
    internal static HeadFile ReadHead(string directory, string head, string relative)
    {
        // Git words an unknown commit id the same way as a missing path, so the commit is checked on its own first.
        if (RunGit(directory, ["cat-file", "-e", $"{head}^{{commit}}"]).ExitCode != 0)
            return HeadFile.Broken;
        GitResult blob = RunGit(directory, ["cat-file", "blob", $"{head}:{relative.Replace('\\', '/')}"]);
        if (blob.ExitCode == 0)
            return HeadFile.Of(blob.Output);
        return blob.ExitCode == 128 && PathNotInCommit.IsMatch(blob.Error) ? HeadFile.Missing : HeadFile.Broken;
    }

    private sealed record GitResult(int ExitCode, byte[] Output, string Error);

    private static GitResult RunGit(string directory, string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        // Git's wording is matched above, so the messages are asked for in English whatever the machine's language.
        process.StartInfo.Environment["LC_ALL"] = "C";
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new GitResult(-1, [], string.Empty);
        }

        Task<string> error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        return new GitResult(process.ExitCode, output.ToArray(), error.GetAwaiter().GetResult());
    }
}
