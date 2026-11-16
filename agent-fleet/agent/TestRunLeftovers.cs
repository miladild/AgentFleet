namespace AgentFleet;

/// <summary>The files a test run left in the project folder that may be removed, or why nothing may be (see TestRunLeftovers).</summary>
internal sealed record LeftoverPlan(IReadOnlyList<string> Remove, bool Fallback, string? Reason);

/// <summary>The names a test run removed from the project and, when none were removed, why the listing could not be used.</summary>
internal sealed record LeftoverRemoval(IReadOnlyList<string> Removed, string? Skipped);

/// <summary>
/// The files a test run creates in the project folder and that nothing in the plan wants: data files, reports and caches a test
/// writes next to the code. A test run that leaves them behind would have a passing check sent back for them (see PlanRunnerUnlisted).
/// The runner lists the untracked files git sees before the run and after it; the files only in the second list are the run's.
/// Anything doubtful removes nothing, and the existing rule for unlisted files applies.
/// </summary>
internal static class TestRunLeftovers
{
    /// <summary>More files than this in one run is a bigger change than a test's output, so nothing is removed.</summary>
    internal const int MaxRemovedFiles = 500;

    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The files to remove: those in the listing after the run and not the listing before it, less the dependency and version-control
    /// folders, incidental files and files the plan names. Paths are relative to the project folder. Falls back, removing nothing, when
    /// either listing is missing, when a file would be outside the project folder, or when more than <see cref="MaxRemovedFiles"/> remain.
    /// </summary>
    internal static LeftoverPlan Plan(string projectRoot, IReadOnlyList<string>? before, IReadOnlyList<string>? after, IEnumerable<string>? plannedPaths)
    {
        if (before is null || after is null)
        {
            return Fallback("The untracked files were not listed before or after the run.");
        }

        var known = new HashSet<string>(before.Select(Slash), Comparer);
        string[] created = after.Select(Slash).Where(file => !known.Contains(file)).ToArray();
        if (created.FirstOrDefault(file => !IsInside(projectRoot, file)) is { } outside)
        {
            return Fallback($"A file the run created is not inside the project folder: {outside}.");
        }

        string[] planned = (plannedPaths ?? []).ToArray();
        string[] remove = created.Where(file => !IsProtected(projectRoot, file, planned)).ToArray();
        if (remove.Length > MaxRemovedFiles)
        {
            return Fallback($"The run created {remove.Length} files, more than {MaxRemovedFiles}, so none is removed.");
        }

        return new LeftoverPlan(remove, false, null);
    }

    /// <summary>
    /// Removes the files, with the folders they leave empty, through <paramref name="deleteOne"/> (the project folder's own delete when null;
    /// it takes a full path and reports whether it removed something). A file in <paramref name="before"/> is never removed, and a folder
    /// that held a file before the run, or still holds one, stays. Returns the files removed.
    /// </summary>
    internal static IReadOnlyList<string> Remove(string projectRoot, IReadOnlyList<string> files, IReadOnlyList<string> before, Func<string, bool>? deleteOne = null)
    {
        Func<string, bool> delete = deleteOne ?? DeleteLocal;
        var known = new HashSet<string>(before.Select(Slash), Comparer);
        var removed = new List<string>();
        foreach (string file in files)
        {
            if (known.Contains(Slash(file)) || !IsInside(projectRoot, file))
            {
                continue;
            }

            if (delete(FullPath(projectRoot, file)))
            {
                removed.Add(file);
            }
        }

        // The deepest folders first, so a folder emptied by its own folder's removal goes too.
        string[] folders = removed.SelectMany(Ancestors).Distinct(Comparer)
            .OrderByDescending(folder => folder.Count(character => character == '/'))
            .ToArray();
        foreach (string folder in folders)
        {
            string prefix = folder + "/";
            if (known.Any(file => file.StartsWith(prefix, Comparison)))
            {
                continue;
            }

            delete(FullPath(projectRoot, folder));
        }

        return removed;
    }

    private static LeftoverPlan Fallback(string reason) => new([], true, reason);

    private static string Slash(string path) => path.Replace('\\', '/');

    private static string FullPath(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    // The folders above a file, nearest first, without the project folder itself.
    private static IEnumerable<string> Ancestors(string relative)
    {
        string[] parts = Slash(relative).Split('/');
        for (int depth = parts.Length - 1; depth > 0; depth--)
        {
            yield return string.Join('/', parts[..depth]);
        }
    }

    // A path the run's files may use: relative, with no drive, no stream name and no step up out of the folder, and inside the folder.
    private static bool IsInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.StartsWith('/') || relative.StartsWith('\\') ||
            Path.IsPathRooted(relative) || Slash(relative).Split('/').Any(part => part == ".."))
        {
            return false;
        }

        try
        {
            string folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return FullPath(root, relative).StartsWith(folder, Comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    // A file the plan names (a file, a folder it is in, or a pattern it matches), or one the fleet or a dependency tool owns.
    private static bool IsProtected(string root, string relative, string[] planned) =>
        PlanRunner.IsFleetOrDependencyPath(relative) || RepairLadder.IsIncidentalFile(relative) ||
        planned.Any(declared => FleetPlanContext.Names(declared, FullPath(root, relative)));

    // The project folder's own removal for a path on this machine: a file, or an empty folder. Anything else is left alone.
    private static bool DeleteLocal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }
}
