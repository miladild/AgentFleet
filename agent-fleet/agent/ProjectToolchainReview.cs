using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>Checks the project's explicit runtime pins using the same environment that runs plan checks.</summary>
internal static class ProjectToolchainReview
{
    private static readonly HashSet<string> DotnetCommands = new(StringComparer.OrdinalIgnoreCase) { "dotnet" };
    private static readonly HashSet<string> NodeCommands = new(StringComparer.OrdinalIgnoreCase) { "node", "npm", "npx", "yarn", "pnpm" };
    private static readonly HashSet<string> PythonCommands = new(StringComparer.OrdinalIgnoreCase) { "python", "python3", "py" };
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<IReadOnlyList<string>> ProblemsAsync(
        string? workingDirectory,
        IReadOnlyList<PlanStepInput> steps,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return [];
        }

        string root;
        try
        {
            root = Path.GetFullPath(workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }
        bool usesDotnet = Uses(steps, DotnetCommands);
        bool usesNode = Uses(steps, NodeCommands);
        bool usesPython = Uses(steps, PythonCommands);
        var problems = new List<string>();

        if (usesDotnet && FindInParentDirectories(root, "global.json") is { } globalJson && QuickProcess.FindOnPath("dotnet") is not null)
        {
            string? sdkPin = ReadDotnetPin(globalJson);
            QuickProcess.Result? result = sdkPin is null
                ? null
                : await QuickProcess.RunAsync("dotnet", ["--version"], ProbeTimeout, cancellationToken, root);
            if (sdkPin is null)
            {
                problems.Add($"This project's {globalJson} has no readable sdk.version. Correct it before proposing a plan.");
            }
            else if (result is null || result.TimedOut || result.ExitCode != 0)
            {
                problems.Add(
                    $"This project pins .NET SDK {sdkPin} in {Path.GetFileName(globalJson)}, but the Fleet backend cannot select that SDK from {root}. " +
                    "Install a compatible SDK on the hub or update global.json, then restart Fleet. Check installed SDKs with dotnet --list-sdks. " +
                    "Fleet does not install runtimes from project files.");
            }
        }

        VersionPin[] nodePins = usesNode
            ? ReadNodePins(root).DistinctBy(pin => pin.Text, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        if (nodePins.Length > 0)
        {
            QuickProcess.Result? result = QuickProcess.FindOnPath("node") is null
                ? null
                : await QuickProcess.RunAsync("node", ["--version"], ProbeTimeout, cancellationToken, root);
            int[]? current = result is { ExitCode: 0, TimedOut: false } ? ParseVersion(result.Output) : null;
            foreach (VersionPin pin in nodePins)
            {
                if (current is null || !Matches(pin, current))
                {
                    problems.Add(
                        $"This project pins Node.js {pin.Text} in {pin.Source}, but the Fleet backend " +
                        $"{(current is null ? "cannot read the installed Node.js version" : $"has Node.js {FormatVersion(current)}")}. " +
                        "Install a compatible Node.js version on the hub or update the pin, then restart Fleet. " +
                        "Fleet does not install runtimes from project files.");
                }
            }
        }

        if (usesPython && ReadPin(Path.Combine(root, ".python-version")) is { } pythonPin)
        {
            string[] programs = steps.Where(step => !string.IsNullOrWhiteSpace(step.Verify))
                .SelectMany(step => PlanReview.ProgramsUsed(step.Verify!, PythonCommands))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (programs.Length == 0) programs = [OperatingSystem.IsWindows() ? "py" : "python3"];

            foreach (string usedProgram in programs)
            {
                (string? program, string[] arguments) = PythonProbe(usedProgram, pythonPin);
                QuickProcess.Result? result = program is null
                    ? null
                    : await QuickProcess.RunAsync(program, arguments, ProbeTimeout, cancellationToken, root);
                int[]? current = result is { ExitCode: 0, TimedOut: false } ? ParseVersion(result.Output) : null;
                if (current is null || !Matches(pythonPin, current))
                {
                    string observed = current is null ? "no usable Python was found" : $"the check's {usedProgram} command selects Python {FormatVersion(current)}";
                    string hint = OperatingSystem.IsWindows() && pythonPin.Parts.Length >= 2
                        ? $" On Windows, checks can select that minor version with py -{pythonPin.Parts[0]}.{pythonPin.Parts[1]}."
                        : string.Empty;
                    problems.Add(
                        $"This project pins Python {pythonPin.Text} in .python-version, but {observed}. " +
                        $"Install a compatible Python version on the hub or update the pin, then restart Fleet.{hint} " +
                        "Fleet does not install runtimes from project files.");
                }
            }
        }

        return problems;
    }

    private static bool Uses(IReadOnlyList<PlanStepInput> steps, IReadOnlySet<string> commands) =>
        steps.Any(step => !string.IsNullOrWhiteSpace(step.Verify) && PlanReview.ProgramsUsed(step.Verify, commands).Count > 0);

    private static string? ReadDotnetPin(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("sdk", out JsonElement sdk) && sdk.ValueKind == JsonValueKind.Object &&
                   sdk.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<VersionPin> ReadNodePins(string root)
    {
        var pins = new List<VersionPin>();
        foreach (string name in new[] { ".nvmrc", ".node-version" })
        {
            if (ReadPin(Path.Combine(root, name)) is { } pin)
            {
                pins.Add(pin);
            }
        }

        try
        {
            string packageJson = Path.Combine(root, "package.json");
            if (!File.Exists(packageJson))
            {
                return pins;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("engines", out JsonElement engines) && engines.ValueKind == JsonValueKind.Object &&
                engines.TryGetProperty("node", out JsonElement node) && node.ValueKind == JsonValueKind.String &&
                ParsePin(node.GetString(), "package.json engines.node") is { } pin)
            {
                pins.Add(pin);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // package.json is optional for this check; explicit version files still apply.
        }

        return pins;
    }

    private static VersionPin? ReadPin(string path)
    {
        try
        {
            return File.Exists(path) ? ParsePin(File.ReadAllText(path), Path.GetFileName(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindInParentDirectories(string root, string name)
    {
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static VersionPin? ParsePin(string? value, string source)
    {
        if (value is null)
        {
            return null;
        }

        Match match = Regex.Match(value.Trim(), @"^[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?$");
        if (!match.Success)
        {
            return null; // Ranges and aliases need their native version manager; don't guess at them.
        }

        int[] parts = match.Groups.Cast<Group>().Skip(1).Where(group => group.Success)
            .Select(group => int.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return new VersionPin(value.Trim(), source, parts);
    }

    private static int[]? ParseVersion(string output)
    {
        Match match = Regex.Match(output, @"(?<!\d)[vV]?(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
        {
            return null;
        }

        return match.Groups.Cast<Group>().Skip(1).Where(group => group.Success)
            .Select(group => int.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static bool Matches(VersionPin pin, int[] current) => pin.Parts.Length <= current.Length &&
        pin.Parts.Select((part, index) => part == current[index]).All(matches => matches);

    private static string FormatVersion(int[] version) => string.Join('.', version);

    private static (string? Program, string[] Arguments) PythonProbe(string usedProgram, VersionPin pin)
    {
        if (string.Equals(usedProgram, "py", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
        {
            string version = pin.Parts.Length >= 2 ? $"-{pin.Parts[0]}.{pin.Parts[1]}" : $"-{pin.Parts[0]}";
            return ("py", [version, "--version"]);
        }

        if (usedProgram is "python" or "python3") return (usedProgram, ["--version"]);
        return (null, []);
    }

    private sealed record VersionPin(string Text, string Source, int[] Parts);
}
