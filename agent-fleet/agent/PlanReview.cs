using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// Checks a proposed plan for the mistakes that make it fail in the night rather than in front of the user: a check
/// that is a sentence instead of a command, a check that needs a file only a later step creates, a project folder
/// that is not there. Measured with the fleet's own planner: one plan had "File src/slug.js should exist" as a check,
/// another checked step 1 with a test file step 3 writes. Either would have spent every attempt and blocked the plan.
/// The model gets the problems back and proposes again, as it does for a broken diagram.
/// </summary>
internal static partial class PlanReview
{
    // Shell built-ins a check may start with: they are not programs on PATH.
    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "echo", "test", "[", "type", "dir", "exit", "true", "false", "set", "if", "call", "pushd", "popd", "where", "findstr"
    };

    [GeneratedRegex(@"&&|\|\||;|\|")]
    private static partial Regex CommandSeparators();

    [GeneratedRegex("\"[^\"]*\"|'[^']*'|\\S+")]
    private static partial Regex Tokens();

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,6}$")]
    private static partial Regex FileExtension();

    public static IReadOnlyList<string> Problems(string? workingDirectory, IReadOnlyList<PlanStepInput> steps, Func<string, bool>? programExists = null)
    {
        programExists ??= program => QuickProcess.FindOnPath(program) is not null;
        var problems = new List<string>();

        string? root = null;
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            problems.Add("workingDirectory is missing: give the project's full folder path on the hub.");
        }
        else if (!Path.IsPathFullyQualified(workingDirectory.Trim()))
        {
            problems.Add($"workingDirectory \"{workingDirectory}\" is not a full path: give the project's full folder path on the hub.");
        }
        else
        {
            root = workingDirectory.Trim();
            // A plan may create its project folder, but only inside one that exists.
            if (!Directory.Exists(root) && !Directory.Exists(Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? string.Empty))
            {
                problems.Add($"workingDirectory \"{root}\" does not exist on the hub, and neither does its parent folder.");
            }
        }

        // Without checks the runner can only take the model's word for it, all night. The last step is where the
        // whole result is proven (the tests, the build), so it always needs one.
        if (steps.All(step => string.IsNullOrWhiteSpace(step.Verify)))
        {
            problems.Add("No step has a check, so nothing would prove the work is right. Give each step a command that fails when it " +
                         "did not work (run its tests, build the project, or call the new code with node -e or python -c), and end " +
                         "with a step that runs the whole test suite.");
        }
        else if (string.IsNullOrWhiteSpace(steps[^1].Verify))
        {
            problems.Add($"The last step ({steps[^1].Title}) has no check: it should prove the whole result, for example by running all the tests or the build.");
        }

        for (int index = 0; index < steps.Count; index++)
        {
            PlanStepInput step = steps[index];
            int number = index + 1;
            if (string.IsNullOrWhiteSpace(step.Verify))
            {
                continue;
            }

            string verify = step.Verify.Trim();
            string? firstProgram = FirstProgram(verify);
            if (firstProgram is null || !IsRunnable(firstProgram, root, programExists))
            {
                problems.Add(
                    $"Step {number} ({step.Title}): its check \"{Clip(verify)}\" is not a command the hub can run" +
                    (firstProgram is not null && LooksLikeProgram(firstProgram) ? $" ({firstProgram} is not installed there)" : string.Empty) +
                    ". The fleet runs the check exactly as written: give a command that fails when the step did not work, such as node --test test/x.test.js, npm test or dotnet test.");
                continue;
            }

            if (NeverFinishes(verify, root) is { } endless)
            {
                problems.Add(
                    $"Step {number} ({step.Title}): its check \"{Clip(verify)}\" runs {endless}, which does not exit on its own, so the fleet " +
                    "would stop it at its time limit and the step would fail every attempt. Check the step with something that finishes: its " +
                    "tests, the build or the typecheck (a test can start the server, call it and stop it). A plan cannot leave a server " +
                    "running; tell the user the command that starts it instead.");
                continue;
            }

            if (root is null)
            {
                continue;
            }

            foreach (string file in FilesIn(verify))
            {
                string full = Resolve(root, file);
                if (File.Exists(full) || Directory.Exists(full))
                {
                    continue;
                }

                int creator = IndexOfStepNaming(steps, root, full);
                if (creator > index)
                {
                    problems.Add(
                        $"Step {number} ({step.Title}): its check uses {file}, which step {creator + 1} creates, so it cannot pass when step {number} is done. " +
                        "Write the code and its test in the same step, or check this step with something that exists by then.");
                }
                else if (creator < 0)
                {
                    problems.Add(
                        $"Step {number} ({step.Title}): its check uses {file}, which does not exist and no step names in its files. " +
                        "List it in the files of the step that creates it.");
                }
            }
        }

        return problems;
    }

    // npm scripts that by convention start something that keeps running.
    private static readonly HashSet<string> ServerScripts = new(StringComparer.OrdinalIgnoreCase) { "dev", "start", "serve", "watch", "preview" };

    /// <summary>
    /// What in a check keeps running instead of finishing (a dev server, a watcher), or null. An npm, yarn, pnpm or bun
    /// script is looked up in the project's package.json. Measured: a planner checked "Start the development server" with
    /// `npm run dev` (tsx watch), which can never pass.
    /// </summary>
    public static string? NeverFinishes(string command, string? root) => NeverFinishes(command, root, depth: 0);

    private static string? NeverFinishes(string command, string? root, int depth)
    {
        foreach (string segment in CommandSeparators().Split(command))
        {
            List<string> words = Tokens().Matches(segment).Select(match => match.Value.Trim('"', '\'')).Where(word => word.Length > 0).ToList();
            if (words.Count > 0 && words[0].ToLowerInvariant() is "npx" or "bunx")
            {
                words.RemoveAt(0);
            }

            if (words.Count == 0)
            {
                continue;
            }

            if (words.Any(word => word.Equals("--watch", StringComparison.OrdinalIgnoreCase) || word.Equals("--watchAll", StringComparison.OrdinalIgnoreCase)))
            {
                return "a watcher (--watch)";
            }

            string program = Path.GetFileNameWithoutExtension(words[0]).ToLowerInvariant();
            string? second = words.Count > 1 ? words[1].ToLowerInvariant() : null;
            switch (program)
            {
                case "npm" or "yarn" or "pnpm" or "bun":
                    string? script = second is "run" or "run-script" ? words.ElementAtOrDefault(2) : second;
                    if (script is null)
                    {
                        break;
                    }

                    if (ServerScripts.Contains(script))
                    {
                        return $"the \"{script}\" script, which by convention starts a server or a watcher";
                    }

                    if (depth == 0 && ScriptBody(root, script) is { } body && NeverFinishes(body, root, depth + 1) is { } inside)
                    {
                        return $"the \"{script}\" script ({inside})";
                    }

                    break;
                case "nodemon" or "live-server" or "http-server" or "webpack-dev-server" or "ts-node-dev" or "serve" or "uvicorn" or "gunicorn":
                    return $"{program}, a server or a watcher";
                case "next" when second is "dev" or "start":
                case "vite" when second is null or "dev" or "serve" or "preview":
                case "webpack" or "ng" when second == "serve":
                case "tsx" when second == "watch":
                case "dotnet" when second == "watch":
                case "flask" when second == "run":
                case "rails" when second is "s" or "server":
                    return $"{program}{(second is null ? string.Empty : " " + second)}, a server or a watcher";
                case "tsc" when words.Contains("-w"):
                    return "a watcher (tsc -w)";
                case "php" when words.Contains("serve", StringComparer.OrdinalIgnoreCase):
                case "python" or "python3" or "py" when words.Contains("http.server", StringComparer.OrdinalIgnoreCase):
                    return "a web server";
            }
        }

        return null;
    }

    private static string? ScriptBody(string? root, string script)
    {
        try
        {
            string path = Path.Combine(root ?? string.Empty, "package.json");
            if (root is null || !File.Exists(path))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("scripts", out var scripts) &&
                   scripts.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   scripts.TryGetProperty(script, out var body) &&
                   body.ValueKind == System.Text.Json.JsonValueKind.String
                ? body.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // The program the check starts with, after any leading "cd folder &&".
    private static string? FirstProgram(string command)
    {
        foreach (string segment in CommandSeparators().Split(command))
        {
            string? first = Tokens().Matches(segment).Select(match => match.Value.Trim('"', '\'')).FirstOrDefault();
            if (first is null)
            {
                continue;
            }

            if (first.Equals("cd", StringComparison.OrdinalIgnoreCase) || first.Equals("pushd", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return first;
        }

        return null;
    }

    private static bool IsRunnable(string program, string? root, Func<string, bool> programExists)
    {
        if (BuiltIns.Contains(program) || programExists(program))
        {
            return true;
        }

        // A script in the project: ./gradlew, .\build.ps1, scripts/check.sh.
        if (root is not null && (program.Contains('/') || program.Contains('\\')))
        {
            string full = Resolve(root, program);
            return File.Exists(full);
        }

        return false;
    }

    // Words that are clearly meant as programs, as opposed to the first word of a sentence.
    private static bool LooksLikeProgram(string word) =>
        word.Length > 0 && (char.IsLower(word[0]) || word.Contains('.') || word.Contains('-'));

    // Arguments that look like files: a path with a slash, or a name with an extension. Flags, globs and URLs are not.
    private static IEnumerable<string> FilesIn(string command)
    {
        foreach (Match match in Tokens().Matches(command).Skip(1))
        {
            string token = match.Value.Trim('"', '\'');
            // Inline code ("require('./src/x')"), flags (-v, /c on Windows), globs, URLs and assignments are not files.
            if (token.StartsWith('-') || token.IndexOfAny(['*', '?', '(', ')', '\'', '"', '{', '}', '[', ']', '<', '>', '$', '`', ',', ';', ' ', '=']) >= 0 ||
                token.Contains("://", StringComparison.Ordinal) || (token.StartsWith('/') && token.Length <= 4 && token.IndexOf('/', 1) < 0) ||
                token is "&&" or "||" or "|")
            {
                continue;
            }

            if (token.Contains('/') || token.Contains('\\') || FileExtension().IsMatch(token))
            {
                yield return token;
            }
        }
    }

    private static int IndexOfStepNaming(IReadOnlyList<PlanStepInput> steps, string root, string full)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            if ((steps[index].Files ?? []).Any(file => !string.IsNullOrWhiteSpace(file) &&
                    string.Equals(Resolve(root, file.Trim()), full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Resolve(string root, string path)
    {
        try
        {
            return Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string Clip(string text) => text.Length <= 120 ? text : text[..120] + "...";
}
