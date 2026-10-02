using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// One repair the runner may run itself. Cause names what is wrong ("node-modules"): a cause is repaired once per step.
/// Command is null when the problem is known but the fleet may not repair it; Advice then says exactly what to do.
/// </summary>
internal sealed record EnvironmentRepair(string Cause, string? Command, string Advice);

/// <summary>
/// The environment doctor: the few project-local repairs the runner makes by itself when a check fails because the
/// project's own dependencies are not installed on the machine that ran it (a fresh worker copy has no node_modules,
/// no restored packages). Every command is a constant from this list, run in the project folder through the same
/// command path and limited account as the model's own commands. Never a global install, never elevation, never a
/// package the project does not already declare.
/// </summary>
internal static partial class EnvironmentDoctor
{
    // 'jest' is not recognized / jest: not found, from a script inside the check (the check's own program is not this).
    [GeneratedRegex(@"(?ix)
        '(?<tool>[\w.\-@/]+)'\sis\snot\srecognized\sas\san\sinternal\sor\sexternal\scommand
        |The\sterm\s'(?<tool>[\w.\-@/]+)'\sis\snot\srecognized
        |(?:^|[\r\n])(?:[^\r\n]*?:\s)?(?<tool>[\w.\-@/]+):\s(?:command\s)?not\sfound")]
    private static partial Regex MissingTool();

    [GeneratedRegex(@"(?ix)(?:Cannot\sfind\s(?:module|package)\s'(?<module>[^']+)'|ERR_MODULE_NOT_FOUND[^\r\n]*?'(?<module>[^']+)')")]
    private static partial Regex MissingNodeModule();

    [GeneratedRegex(@"(?ix)ModuleNotFoundError:\sNo\smodule\snamed\s'(?<module>[\w.\-]+)'|ImportError:\sNo\smodule\snamed\s'?(?<module>[\w.\-]+)'?")]
    private static partial Regex MissingPythonModule();

    [GeneratedRegex(@"(?ix)NETSDK1004|NETSDK1064|project\.assets\.json'?\snot\sfound|Run\sa\sNuGet\spackage\srestore")]
    private static partial Regex NeedsRestore();

    [GeneratedRegex(@"ENOENT[^\r\n]*\.env\b|\.env['""]?\s+(?:file\s+)?(?:not found|does not exist|is missing)|no such file or directory[^\r\n]*\.env\b|Cannot find path '[^']*\.env'", RegexOptions.IgnoreCase)]
    private static partial Regex MissingEnvFile();

    /// <summary>
    /// What to repair for this output, or null when the failure is not a missing dependency. A dependency counts as
    /// missing only when the project declares it (package.json, requirements.txt) or when its restore is what the
    /// tool asks for; a package the project does not declare is the model's to add, not the fleet's to install.
    /// </summary>
    public static EnvironmentRepair? Diagnose(string output, string? projectRoot, string? venvPython = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return null;
        }

        if (NeedsRestore().IsMatch(output) && HasProjectFile(projectRoot))
        {
            return new EnvironmentRepair("dotnet-restore", "dotnet restore", "Run `dotnet restore` in the project folder.");
        }

        string packagePath = Path.Combine(projectRoot, "package.json");
        if (File.Exists(packagePath))
        {
            HashSet<string> declared = DeclaredNodePackages(packagePath);
            string? missing = MissingNodeModule().Matches(output).Select(match => match.Groups["module"].Value).Select(PackageName)
                .FirstOrDefault(name => name.Length > 0 && declared.Contains(name));
            missing ??= MissingTool().Matches(output).Select(match => match.Groups["tool"].Value)
                .FirstOrDefault(tool => DeclaredTool(declared, tool));
            if (missing is not null)
            {
                bool lock_ = File.Exists(Path.Combine(projectRoot, "package-lock.json")) || File.Exists(Path.Combine(projectRoot, "npm-shrinkwrap.json"));
                if (File.Exists(Path.Combine(projectRoot, "yarn.lock")) || File.Exists(Path.Combine(projectRoot, "pnpm-lock.yaml")))
                {
                    return new EnvironmentRepair("node-modules", null,
                        $"`{missing}` is declared in package.json but not installed on the machine that ran the check, and this project uses yarn or pnpm: install its dependencies there with that tool.");
                }

                return new EnvironmentRepair("node-modules", lock_ ? "npm ci" : "npm install",
                    $"Install the project's declared dependencies (`{missing}` is one of them) with {(lock_ ? "npm ci" : "npm install")}.");
            }
        }

        string requirements = Path.Combine(projectRoot, "requirements.txt");
        if (File.Exists(requirements))
        {
            HashSet<string> declared = DeclaredPythonPackages(requirements);
            string? missing = MissingPythonModule().Matches(output).Select(match => PythonPackage(match.Groups["module"].Value))
                .FirstOrDefault(declared.Contains);
            if (missing is not null)
            {
                return venvPython is null
                    ? new EnvironmentRepair("python-requirements", null,
                        $"`{missing}` is in requirements.txt but not installed, and the project has no virtual environment, so the fleet does not install into the machine's Python. " +
                        "Create one in the project folder (`python -m venv .venv`, then `.venv/bin/pip install -r requirements.txt`, or `.venv\\Scripts\\pip.exe` on Windows) and run the check with it.")
                    : new EnvironmentRepair("python-requirements", $"{venvPython} -m pip install -r requirements.txt",
                        $"Install requirements.txt into the project's virtual environment (`{missing}` is one of them).");
            }
        }

        // Not repaired by the fleet: worker copies leave .env files out on purpose, and a .env made from the example would
        // sync back into the user's own project with placeholder values. The note says exactly what to do.
        if (MissingEnvFile().IsMatch(output) && File.Exists(Path.Combine(projectRoot, ".env.example")) && !File.Exists(Path.Combine(projectRoot, ".env")))
        {
            return new EnvironmentRepair("env-file", null,
                "A .env file is missing where the check ran, and the project has a .env.example. The fleet does not create it (worker copies leave .env files out on purpose, and a created one would sync back into your project with placeholder values): " +
                "copy .env.example to .env and fill in the values the check needs, or change the check so that it does not need them.");
        }

        return null;
    }

    /// <summary>The project's own virtual environment's Python, relative to the project folder, when it has one.</summary>
    public static string? VirtualEnvironmentPython(string projectRoot)
    {
        foreach (string folder in new[] { ".venv", "venv" })
        {
            string windows = Path.Combine(projectRoot, folder, "Scripts", "python.exe");
            if (File.Exists(windows))
            {
                return $"{folder}/Scripts/python.exe";
            }

            if (File.Exists(Path.Combine(projectRoot, folder, "bin", "python")))
            {
                return $"{folder}/bin/python";
            }
        }

        return null;
    }

    private static bool HasProjectFile(string root) =>
        Directory.EnumerateFiles(root).Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase));

    private static string PackageName(string module)
    {
        // @scope/pkg/sub -> @scope/pkg; pkg/sub -> pkg; a path is not a package.
        if (module.StartsWith('.') || module.StartsWith('/') || module.Contains(':') || module.Contains('\\'))
        {
            return string.Empty;
        }

        string[] parts = module.Split('/');
        return module.StartsWith('@') && parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : parts[0];
    }

    // A tool name is declared when a dependency has that name, or a bin of that name ("tsc" belongs to typescript, "jest" to jest).
    private static bool DeclaredTool(HashSet<string> declared, string tool) =>
        declared.Contains(tool) || ToolPackages.TryGetValue(tool, out string? package) && declared.Contains(package);

    private static readonly Dictionary<string, string> ToolPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tsc"] = "typescript", ["tsx"] = "tsx", ["ts-node"] = "ts-node", ["jest"] = "jest", ["vitest"] = "vitest", ["mocha"] = "mocha",
        ["eslint"] = "eslint", ["prettier"] = "prettier", ["next"] = "next", ["vite"] = "vite", ["webpack"] = "webpack", ["playwright"] = "@playwright/test",
        ["rollup"] = "rollup", ["esbuild"] = "esbuild", ["ava"] = "ava", ["tap"] = "tap", ["cypress"] = "cypress", ["nodemon"] = "nodemon"
    };

    // The name a module is imported by is not always the name it is installed by.
    private static readonly Dictionary<string, string> PythonImportNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yaml"] = "pyyaml", ["PIL"] = "pillow", ["cv2"] = "opencv-python", ["sklearn"] = "scikit-learn", ["bs4"] = "beautifulsoup4",
        ["dateutil"] = "python-dateutil", ["dotenv"] = "python-dotenv", ["jwt"] = "pyjwt", ["attr"] = "attrs", ["OpenSSL"] = "pyopenssl"
    };

    private static string PythonPackage(string module)
    {
        string top = module.Split('.')[0];
        return PythonImportNames.TryGetValue(top, out string? package) ? package : top.ToLowerInvariant().Replace('_', '-');
    }

    private static HashSet<string> DeclaredNodePackages(string packagePath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packagePath));
            foreach (string section in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
            {
                if (document.RootElement.TryGetProperty(section, out JsonElement dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty dependency in dependencies.EnumerateObject())
                    {
                        names.Add(dependency.Name);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return names;
    }

    private static HashSet<string> DeclaredPythonPackages(string requirementsPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string line in File.ReadLines(requirementsPath))
            {
                string text = line.Split('#')[0].Trim();
                if (text.Length == 0 || text.StartsWith('-'))
                {
                    continue;
                }

                Match name = Regex.Match(text, @"^[A-Za-z0-9][A-Za-z0-9._\-]*");
                if (name.Success)
                {
                    names.Add(name.Value.ToLowerInvariant().Replace('_', '-'));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return names;
    }
}
