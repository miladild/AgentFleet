using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentFleet;

/// <param name="Name">Matches a {name} placeholder in the command or working folder.</param>
/// <param name="Description">What the model should pass, for example "path to the test project".</param>
/// <param name="Required">A required value the model leaves out is an error; an optional one drops its whole argument.</param>
internal sealed record FleetCustomToolParameter(string Name, string? Description = null, bool Required = true);

/// <summary>
/// A tool made from a command, without writing code: "dotnet test {project}" becomes a tool the model calls with a
/// project. The command runs as a program with separate arguments, not through a shell, so a value the model passes
/// is always one argument and can never add a second command.
/// </summary>
/// <param name="Command">The command line, with {placeholders} for what the model fills in. The program itself is fixed.</param>
/// <param name="WorkingDirectory">Where it runs; may contain placeholders. Missing means the backend's working folder.</param>
/// <param name="ReadOnly">It only reads, so it stays available while a plan waits for approval.</param>
internal sealed record FleetCustomToolConfig(
    string Description,
    string Command,
    IReadOnlyList<FleetCustomToolParameter>? Parameters = null,
    string? WorkingDirectory = null,
    int? TimeoutSeconds = null,
    bool ReadOnly = false,
    bool Enabled = true);

internal sealed record CustomToolResult(bool Ok, string Output, long Milliseconds);

/// <summary>Parses, checks and runs command tools.</summary>
internal static partial class CustomToolRunner
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MaxTimeoutSeconds = 600;
    private const int MaxValueLength = 4000;
    private const int MaxOutputCharacters = 30_000;

    [GeneratedRegex("^[a-z][a-z0-9_]{1,47}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]{0,40})\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("\"[^\"]*\"|\\S+")]
    private static partial Regex TokenPattern();

    /// <summary>Splits a command line the way the web UI does: spaces separate, "double quotes" keep text together.</summary>
    public static IReadOnlyList<string> Tokenize(string commandLine) =>
        TokenPattern().Matches(commandLine ?? string.Empty)
            .Select(match => match.Value.Length >= 2 && match.Value.StartsWith('"') && match.Value.EndsWith('"') ? match.Value[1..^1] : match.Value)
            .ToList();

    public static IReadOnlyList<string> Placeholders(string? text) =>
        text is null ? [] : PlaceholderPattern().Matches(text).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Throws InvalidOperationException saying what is wrong, in words for the Config panel.</summary>
    public static void Validate(string name, FleetCustomToolConfig tool)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new InvalidOperationException(
                $"Tool name '{name}' is invalid: use 2 to 48 lowercase letters, digits or underscores, starting with a letter, for example run_tests.");
        }

        if (string.IsNullOrWhiteSpace(tool.Description))
        {
            throw new InvalidOperationException($"Tool '{name}' needs a description: it is how the model knows when to use it.");
        }

        IReadOnlyList<string> tokens = Tokenize(tool.Command);
        if (tokens.Count == 0)
        {
            throw new InvalidOperationException($"Tool '{name}' needs a command, for example: dotnet test {{project}}");
        }

        if (Placeholders(tokens[0]).Count > 0)
        {
            throw new InvalidOperationException($"Tool '{name}': the program ({tokens[0]}) must be fixed. Placeholders can only fill in its arguments.");
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (FleetCustomToolParameter parameter in tool.Parameters ?? [])
        {
            if (!declared.Add(parameter.Name))
            {
                throw new InvalidOperationException($"Tool '{name}' lists the parameter '{parameter.Name}' twice.");
            }
        }

        var used = Placeholders(tool.Command).Concat(Placeholders(tool.WorkingDirectory)).ToHashSet(StringComparer.Ordinal);
        if (used.FirstOrDefault(placeholder => !declared.Contains(placeholder)) is { } missing)
        {
            throw new InvalidOperationException($"Tool '{name}' uses {{{missing}}} but has no parameter called {missing}.");
        }

        if (declared.FirstOrDefault(parameter => !used.Contains(parameter)) is { } unused)
        {
            throw new InvalidOperationException($"Tool '{name}' has a parameter '{unused}' that the command does not use. Put {{{unused}}} where it goes.");
        }

        if (tool.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            throw new InvalidOperationException($"Tool '{name}': the time limit must be between 1 and {MaxTimeoutSeconds} seconds.");
        }
    }

    /// <summary>The JSON schema the model sees: one string property per parameter.</summary>
    public static JsonElement Schema(FleetCustomToolConfig tool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (FleetCustomToolParameter parameter in tool.Parameters ?? [])
        {
            properties[parameter.Name] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = string.IsNullOrWhiteSpace(parameter.Description) ? parameter.Name : parameter.Description
            };
            if (parameter.Required)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return JsonSerializer.SerializeToElement(schema);
    }

    public static string? ValueOf(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement element => element.GetRawText(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>The program and its arguments with the values filled in, or the reason it cannot run.</summary>
    public static (string Program, IReadOnlyList<string> Arguments, string? WorkingDirectory, string? Error) Prepare(
        FleetCustomToolConfig tool,
        IReadOnlyDictionary<string, string?> values)
    {
        foreach (FleetCustomToolParameter parameter in tool.Parameters ?? [])
        {
            values.TryGetValue(parameter.Name, out string? value);
            if (parameter.Required && string.IsNullOrWhiteSpace(value))
            {
                return (string.Empty, [], null, $"Error: {parameter.Name} is required ({parameter.Description ?? "no description"}).");
            }

            if (value is not null && (value.Length > MaxValueLength || value.Contains('\0')))
            {
                return (string.Empty, [], null, $"Error: the value for {parameter.Name} is too long or contains a NUL character.");
            }
        }

        IReadOnlyList<string> tokens = Tokenize(tool.Command);
        var arguments = new List<string>();
        foreach (string token in tokens.Skip(1))
        {
            string? filled = Fill(token, values);
            if (filled is not null)
            {
                arguments.Add(filled);
            }
        }

        string? workingDirectory = string.IsNullOrWhiteSpace(tool.WorkingDirectory) ? null : Fill(tool.WorkingDirectory.Trim(), values);
        return (tokens[0], arguments, workingDirectory, null);
    }

    // Fills a token's placeholders. A token whose placeholder has no value is left out altogether, so an optional
    // "--filter={filter}" disappears instead of becoming "--filter=".
    private static string? Fill(string token, IReadOnlyDictionary<string, string?> values)
    {
        bool dropped = false;
        string filled = PlaceholderPattern().Replace(token, match =>
        {
            values.TryGetValue(match.Groups[1].Value, out string? value);
            if (string.IsNullOrEmpty(value))
            {
                dropped = true;
                return string.Empty;
            }

            return value;
        });
        return dropped ? null : filled;
    }

    public static async Task<CustomToolResult> RunAsync(
        FleetCustomToolConfig tool,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        (string program, IReadOnlyList<string> arguments, string? workingDirectory, string? error) = Prepare(tool, values);
        if (error is not null)
        {
            return new CustomToolResult(false, error, stopwatch.ElapsedMilliseconds);
        }

        if (workingDirectory is not null && !Directory.Exists(workingDirectory))
        {
            return new CustomToolResult(false, $"Error: the working folder does not exist: {workingDirectory}", stopwatch.ElapsedMilliseconds);
        }

        string? resolved = program.Contains('/') || program.Contains('\\') ? program : QuickProcess.FindOnPath(program);
        if (resolved is null || !File.Exists(resolved))
        {
            return new CustomToolResult(false, $"Error: {program} was not found on this computer (the backend's PATH).", stopwatch.ElapsedMilliseconds);
        }

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        // A Windows batch file (npm, npx, yarn are .cmd files) is always run by cmd.exe, which would read & | < > ^ %
        // in an argument as its own syntax. Those characters are refused for batch files, and every argument is quoted.
        if (OperatingSystem.IsWindows() && Path.GetExtension(resolved).ToLowerInvariant() is ".cmd" or ".bat")
        {
            if (arguments.FirstOrDefault(argument => argument.IndexOfAny(['"', '%', '!', '&', '|', '<', '>', '^', '\r', '\n']) >= 0) is { } unsafeValue)
            {
                return new CustomToolResult(false,
                    $"Error: {Path.GetFileName(resolved)} is a batch file, so a value may not contain \" % ! & | < > ^ or line breaks ({Clip(unsafeValue, 80)}).",
                    stopwatch.ElapsedMilliseconds);
            }

            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/d /s /c \"" + string.Join(' ', new[] { resolved }.Concat(arguments).Select(part => $"\"{part}\"")) + "\"";
        }
        else
        {
            startInfo.FileName = resolved;
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CustomToolResult(false, $"Error: could not start {program}: {exception.Message}", stopwatch.ElapsedMilliseconds);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        TimeSpan timeout = TimeSpan.FromSeconds(tool.TimeoutSeconds ?? DefaultTimeoutSeconds);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }

            return new CustomToolResult(false,
                $"Error: it did not finish within {timeout.TotalSeconds:0} seconds and was stopped.\n{Format(null, stdout, stderr)}", stopwatch.ElapsedMilliseconds);
        }

        process.WaitForExit(); // Flushes the last output lines.
        return new CustomToolResult(process.ExitCode == 0, Format(process.ExitCode, stdout, stderr), stopwatch.ElapsedMilliseconds);
    }

    private static string Format(int? exitCode, StringBuilder stdout, StringBuilder stderr)
    {
        string output;
        lock (stdout)
        {
            output = stdout.ToString();
        }

        string errors;
        lock (stderr)
        {
            errors = stderr.ToString();
        }

        string text = (exitCode is null ? string.Empty : $"Exit code: {exitCode}\n") + $"--- stdout ---\n{output}" +
                      (errors.Length > 0 ? $"\n--- stderr ---\n{errors}" : string.Empty);
        // The end of a build or test run is where the summary is, so keep the start and the end.
        return text.Length <= MaxOutputCharacters
            ? text
            : text[..(MaxOutputCharacters / 3)] + $"\n\n[... {text.Length - MaxOutputCharacters} characters left out ...]\n\n" + text[^(MaxOutputCharacters * 2 / 3)..];
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}

/// <summary>A command tool as the model sees it: its own name, description and parameters.</summary>
internal sealed class CustomCommandTool(string name, FleetCustomToolConfig config) : AIFunction
{
    private readonly JsonElement _schema = CustomToolRunner.Schema(config);

    public override string Name => name;

    public override string Description => config.Description.Trim();

    public override JsonElement JsonSchema => _schema;

    public FleetCustomToolConfig Config => config;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        Dictionary<string, string?> values = arguments.ToDictionary(pair => pair.Key, pair => CustomToolRunner.ValueOf(pair.Value), StringComparer.Ordinal);
        CustomToolResult result = await CustomToolRunner.RunAsync(config, values, cancellationToken);
        return result.Output;
    }
}
