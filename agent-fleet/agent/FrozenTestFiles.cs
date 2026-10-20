namespace AgentFleet;

/// <summary>The rules every place shares for a frozen test file: the refusal text and the project-relative comparison.</summary>
internal static class FrozenTestFiles
{
    // Said to the model when a file tool is asked to change a test file that an accepted tests step fixed for the plan.
    internal const string RefusalMessage = "This test file is fixed for this plan; change the code, not the test. If you believe the test is wrong, call dispute_test with the test and the reason.";

    // The one rule for whether a project-relative path is a frozen file. Case is ignored only where names do not depend on it.
    internal static bool Contains(IEnumerable<PlanFrozenTestFile> files, string relative, bool ignoreCase) =>
        files.Any(frozen => string.Equals(frozen.Path, relative, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    // The project-relative form of a path, or null when it is outside the project root (the caller then has no frozen file to match).
    internal static string? ProjectRelativeOrNull(string path, string root)
    {
        try
        {
            return WorkerWorkspacePath.RelativeToProject(path, root);
        }
        catch (WorkerPathRefusedException)
        {
            return null;
        }
    }
}

/// <summary>
/// The frozen test files of a plan step that runs on the hub. Ambient like the worker scope: it is set only while the model of such
/// an attempt runs, so the hub's own file tools refuse to change a frozen file, and no other request sees it.
/// </summary>
internal sealed class HubFrozenTestScope
{
    private static readonly AsyncLocal<HubFrozenTestScope?> CurrentScope = new();
    private readonly string _root;
    private readonly IReadOnlyList<PlanFrozenTestFile> _files;

    private HubFrozenTestScope(string root, IReadOnlyList<PlanFrozenTestFile> files)
    {
        _root = root;
        _files = files;
    }

    public static HubFrozenTestScope? Current => CurrentScope.Value;

    public static IDisposable Push(string projectRoot, IReadOnlyList<PlanFrozenTestFile> files)
    {
        HubFrozenTestScope? previous = CurrentScope.Value;
        CurrentScope.Value = new HubFrozenTestScope(Path.GetFullPath(projectRoot), files);
        return new Restore(previous);
    }

    /// <summary>The refusal for the first given path that names a frozen file, or null when none does.</summary>
    public string? Refusal(params string[] paths)
    {
        bool ignoreCase = OperatingSystem.IsWindows();
        foreach (string path in paths)
        {
            // Resolved the way the tool resolves it, so the check looks at the file the tool would change.
            string resolved;
            try
            {
                resolved = HubFileSystemTools.Resolve(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // The tool reports an unusable path itself.
                continue;
            }

            string? relative = FrozenTestFiles.ProjectRelativeOrNull(resolved, _root);
            if (relative is not null && FrozenTestFiles.Contains(_files, relative, ignoreCase)) return FrozenTestFiles.RefusalMessage;
        }

        return null;
    }

    private sealed class Restore(HubFrozenTestScope? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }
}

/// <summary>Raised when a frozen test file on the hub was changed and no verified accepted copy exists to put it back.</summary>
internal sealed class FrozenTestsUnrestorableException(string message) : Exception(message);
