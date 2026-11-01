using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// What a plan step's model may not change on its own: the project's dependencies and settings. Measured: a worker ran
/// "npm install tsx@latest" to get rid of a type error and changed package.json in two runs of three, and nothing in the step had
/// asked for it. A denial is returned to the model as the tool's result and the round goes on, so the model can do something else.
/// The files a step names are writable as before.
/// </summary>
internal static partial class FileGuard
{
    private static readonly string[] FileTools = ["write_file", "edit_file", "delete_file"];

    // Files that fix or describe the project's dependencies and its compiler settings.
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock",
        "packages.lock.json", "poetry.lock", "Cargo.lock", "go.sum"
    };

    [GeneratedRegex(@"^tsconfig(\.[\w-]+)*\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex TsConfig();

    private const string NotWritable =
        "Do not change the project's dependencies or settings: work with what is installed. " +
        "If the step cannot be done without this, call report_blocker and say what it needs.";

    /// <summary>
    /// The denial for a file tool call that writes, edits, deletes or moves a protected file the step does not name, or null.
    /// Reads are never denied. Paths are taken relative to the project folder.
    /// </summary>
    public static string? FileToolDenial(string toolName, IReadOnlyDictionary<string, object?> arguments, string? workingDirectory, IEnumerable<string> declaredPaths)
    {
        string[] keys = toolName == "move_file" ? ["source", "destination"] : FileTools.Contains(toolName) ? ["path"] : [];
        string[] declared = declaredPaths.ToArray();
        foreach (string key in keys)
        {
            if (!arguments.TryGetValue(key, out object? raw) || CustomToolRunner.ValueOf(raw) is not { Length: > 0 } given)
            {
                continue;
            }

            string full;
            try
            {
                // A worker's long path to its own copy of the project is taken back to the path inside the project.
                string inProject = FailureLocation.ProjectRelative(given, workingDirectory);
                full = Path.GetFullPath(Path.IsPathRooted(inProject) ? inProject : Path.Combine(workingDirectory ?? string.Empty, inProject));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsProtected(full) && !declared.Any(path => NamedExactly(path, full)))
            {
                return $"DENIED: {given} is not writable in this step: it is a dependency or project settings file (or lives in node_modules) and the step does not list it. {NotWritable}";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a file tool call writes one of the files the step lists, or a file under a folder it lists. A scratch file next to
    /// the code is not the step's work. With no step scope known, every write counts.
    /// </summary>
    public static bool WritesDeclaredFile(string toolName, IReadOnlyDictionary<string, object?> arguments, string? workingDirectory, IReadOnlyList<string>? declaredPaths)
    {
        if (declaredPaths is null)
        {
            return true;
        }

        string key = toolName == "move_file" ? "destination" : "path";
        if (!arguments.TryGetValue(key, out object? raw) || CustomToolRunner.ValueOf(raw) is not { Length: > 0 } given)
        {
            return false;
        }

        string full;
        try
        {
            string inProject = FailureLocation.ProjectRelative(given, workingDirectory);
            full = Path.GetFullPath(Path.IsPathRooted(inProject) ? inProject : Path.Combine(workingDirectory ?? string.Empty, inProject));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return declaredPaths.Any(path =>
        {
            string declared = path.IndexOfAny(['*', '?']) >= 0 ? path : Path.GetFullPath(path);
            return NamedExactly(declared, full) || full.StartsWith(Path.TrimEndingDirectorySeparator(declared) + Path.DirectorySeparatorChar, comparison);
        });
    }

    private static bool IsProtected(string full)
    {
        string[] parts = full.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        string name = parts.Length == 0 ? string.Empty : parts[^1];
        return parts.Any(part => part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) || ProtectedNames.Contains(name) || TsConfig().IsMatch(name);
    }

    // A step names a protected file only by naming it (or a pattern that matches it), not by listing a folder it is in.
    private static bool NamedExactly(string declared, string full)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return declared.IndexOfAny(['*', '?']) >= 0
            ? FleetPlanContext.Names(declared, full)
            : string.Equals(Path.TrimEndingDirectorySeparator(declared), Path.TrimEndingDirectorySeparator(full), comparison);
    }

    /// <summary>
    /// The denial for a command that installs, removes or updates packages, or fetches and runs a package that is not installed,
    /// or null. Restoring what the lock file names (npm install, npm ci, yarn) and running what is installed (npx tsc) stay allowed.
    /// </summary>
    public static string? CommandDenial(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        foreach (string segment in Segment().Split(command))
        {
            string[] tokens = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(token => token.Trim('"', '\'')).ToArray();
            for (int index = 0; index < tokens.Length; index++)
            {
                if (ChangesPackages(Program(tokens[index]), tokens[(index + 1)..]))
                {
                    return $"DENIED: this command installs or changes packages, which a plan step may not do. {NotWritable}";
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"&&|\|\||[;|\r\n]")]
    private static partial Regex Segment();

    private static string Program(string token)
    {
        string name = Path.GetFileName(token.Replace('\\', '/')).ToLowerInvariant();
        foreach (string extension in new[] { ".cmd", ".exe", ".bat", ".ps1" })
        {
            if (name.EndsWith(extension, StringComparison.Ordinal))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    private static bool ChangesPackages(string program, string[] rest)
    {
        string[] words = rest.Where(token => !token.StartsWith('-')).ToArray();
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
        switch (program)
        {
            case "npm" or "pnpm" or "bun":
                return verb is "add" or "remove" or "rm" or "uninstall" or "un" or "unlink" or "update" or "up" or "upgrade" ||
                       (verb is "install" or "i" or "in" && words.Length > 1);
            case "yarn":
                return verb is "add" or "remove" or "upgrade" or "up" or "upgrade-interactive" or "global" || (verb == "install" && words.Length > 1);
            case "npx":
                return rest.TakeWhile(token => token.StartsWith('-')).Any(flag => flag is "-y" or "--yes" or "-p" or "--package" || flag.StartsWith("--package=", StringComparison.Ordinal)) ||
                       (words.Length > 0 && (words[0].StartsWith('@') || words[0].IndexOf('@', 1) > 0));
            case "pip" or "pip3":
                return verb is "install" or "uninstall";
            case "python" or "python3" or "py":
                int module = Array.IndexOf(rest, "-m");
                return module >= 0 && module + 2 < rest.Length && rest[module + 1] == "pip" &&
                       rest[(module + 2)..].FirstOrDefault(token => !token.StartsWith('-')) is "install" or "uninstall";
            default:
                return false;
        }
    }
}
