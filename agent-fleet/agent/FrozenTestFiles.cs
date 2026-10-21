using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>The rules every place shares for a frozen test file: the refusal text and the project-relative comparison.</summary>
internal static class FrozenTestFiles
{
    // Said to the model when a file tool is asked to change a test file that an accepted tests step fixed for the plan.
    internal const string RefusalMessage = "This test file is fixed for this plan; change the code, not the test. If you believe the test is wrong, call dispute_test with the test and the reason.";

    // Said when a path names a file in the short (8.3) form Windows also accepts: that form cannot be matched to a fixed test by name.
    internal const string ShortNameRefusal = "This path uses a short (8.3) file name, which cannot be checked against the fixed tests. Use the full file name instead.";

    // A Windows short name: up to eight characters, a tilde, a number and an optional short extension (LIMITT~1.CS).
    private static readonly Regex ShortName = new(@"^[^~]{1,8}~\d{1,6}(\.[^.~]{0,3})?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // On Windows a name is the same name without a trailing dot or space, and a colon starts a stream name: "x.cs.", "x.cs::$DATA"
    // and "x.cs:stream" all name "x.cs". Other platforms keep the name as it is.
    internal static string Canonical(string relative, bool windowsNames)
    {
        if (!windowsNames) return relative;
        return string.Join('/', relative.Split('/').Select(part =>
        {
            int colon = part.IndexOf(':');
            return (colon >= 0 ? part[..colon] : part).TrimEnd(' ', '.');
        }));
    }

    private static StringComparison Comparison(bool windowsNames) => windowsNames ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // The one rule for whether a project-relative path is a frozen file. Windows names are compared as Windows reads them (see Canonical).
    internal static bool Contains(IEnumerable<PlanFrozenTestFile> files, string relative, bool windowsNames)
    {
        string path = Canonical(relative, windowsNames);
        return files.Any(frozen => string.Equals(Canonical(frozen.Path, windowsNames), path, Comparison(windowsNames)));
    }

    // Whether a path is a frozen file or a folder that holds one. A move or a delete of such a path would carry the file away or replace it.
    internal static bool Covers(IEnumerable<PlanFrozenTestFile> files, string relative, bool windowsNames)
    {
        string path = Canonical(relative, windowsNames);
        StringComparison comparison = Comparison(windowsNames);
        return files.Any(frozen =>
        {
            string frozenPath = Canonical(frozen.Path, windowsNames);
            return path.Length == 0 || string.Equals(frozenPath, path, comparison) || frozenPath.StartsWith(path + "/", comparison);
        });
    }

    // A short name can name a frozen file without matching its full name, so on Windows such a path is refused while tests are frozen.
    internal static bool HasShortName(string relative) => relative.Split('/').Any(part => ShortName.IsMatch(part));

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
    public string? Refusal(params string[] paths) => Check(covering: false, paths);

    /// <summary>The refusal for a move or a delete: a path that holds a frozen file is refused as well as the file itself.</summary>
    public string? CoveringRefusal(params string[] paths) => Check(covering: true, paths);

    private string? Check(bool covering, string[] paths)
    {
        bool windowsNames = OperatingSystem.IsWindows();
        foreach (string path in paths)
        {
            // Resolved the way the tool resolves it, so the check looks at the file the tool would change. A name that GetFullPath
            // cannot resolve is compared as given, so it is not let through unchecked.
            string resolved;
            try
            {
                resolved = HubFileSystemTools.Resolve(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                resolved = HubFileSystemTools.ResolveAsGiven(path);
            }

            string? relative = FrozenTestFiles.ProjectRelativeOrNull(resolved, _root);
            if (relative is null) continue;
            if (windowsNames && FrozenTestFiles.HasShortName(relative)) return FrozenTestFiles.ShortNameRefusal;
            bool hit = covering ? FrozenTestFiles.Covers(_files, relative, windowsNames) : FrozenTestFiles.Contains(_files, relative, windowsNames);
            if (hit) return FrozenTestFiles.RefusalMessage;
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
