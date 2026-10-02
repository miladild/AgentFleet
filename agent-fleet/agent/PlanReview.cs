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
    internal static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "echo", "test", "[", "type", "dir", "exit", "true", "false", "set", "if", "call", "pushd", "popd", "where", "findstr", "chdir", "set-location", "sl"
    };

    [GeneratedRegex(@"&&|\|\||;|\|")]
    internal static partial Regex CommandSeparators();

    [GeneratedRegex("\"[^\"]*\"|'[^']*'|\\S+")]
    internal static partial Regex Tokens();

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
        else if (steps[^1].ParallelGroup is not null)
        {
            problems.Add($"The last step ({steps[^1].Title}) cannot be in a parallel group: final validation must run after all project edits finish.");
        }
        else if (!HasFinalValidation(steps[^1].Verify!, root))
        {
            problems.Add($"The last step ({steps[^1].Title}) needs a whole-project build or test-suite command as its check. The runner executes this command after the step's edits and uses its output for bounded repair retries; a syntax check of one file is not enough.");
        }

        for (int index = 0; index < steps.Count; index++)
        {
            problems.AddRange(CheckProblems(root, steps, index, programExists));
        }

        return problems;
    }

    /// <summary>
    /// What is wrong with one step's check: not a command the machine can run, one that never finishes, a file it uses
    /// that nothing creates in time. Empty when the check is sound. The same rules decide a plan at proposal time and a
    /// healed check at run time.
    /// </summary>
    internal static IReadOnlyList<string> CheckProblems(string? root, IReadOnlyList<PlanStepInput> steps, int index, Func<string, bool>? programExists = null)
    {
        programExists ??= program => QuickProcess.FindOnPath(program) is not null;
        var found = new List<string>();
        PlanStepInput step = steps[index];
        int number = index + 1;
        if (string.IsNullOrWhiteSpace(step.Verify))
        {
            found.Add($"Step {number} ({step.Title}) has no bounded check. Every unattended step needs a command that proves it worked.");
            return found;
        }

        string verify = step.Verify.Trim();
        if (PlanRunnerToolPolicy.CommandRefusal(verify) is { } refusal)
        {
            found.Add($"Step {number} ({step.Title}): its check is not safe for unattended retries. {refusal}");
            return found;
        }

        string? firstProgram = FirstProgram(verify);
        if (firstProgram is null || !IsRunnable(firstProgram, root, programExists))
        {
            string launcherHint = OperatingSystem.IsWindows() &&
                (string.Equals(firstProgram, "python", StringComparison.OrdinalIgnoreCase) || string.Equals(firstProgram, "python3", StringComparison.OrdinalIgnoreCase)) &&
                QuickProcess.FindOnPath("py") is not null
                ? " The Windows Python launcher is available as py; use a check such as py -3 --version if that matches the project."
                : string.Empty;
            found.Add(
                $"Step {number} ({step.Title}): its check \"{Clip(verify)}\" is not a command the selected machine can run" +
                (firstProgram is not null && LooksLikeProgram(firstProgram) ? $" ({firstProgram} is not installed on the selected machine)" : string.Empty) +
                ". The fleet runs the check exactly as written: give a command that fails when the step did not work, such as node --test test/x.test.js, npm test or dotnet test." + launcherHint);
            return found;
        }

        if (NeverFinishes(verify, root) is { } endless)
        {
            found.Add(
                $"Step {number} ({step.Title}): its check \"{Clip(verify)}\" runs {endless}, which does not exit on its own, so the fleet " +
                "would stop it at its time limit and the step would fail every attempt. Check the step with something that finishes: its " +
                "tests, the build or the typecheck (a test can start the server, call it and stop it). A plan cannot leave a server " +
                "running; tell the user the command that starts it instead.");
            return found;
        }

        if (root is null)
        {
            return found;
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
                found.Add(
                    $"Step {number} ({step.Title}): its check uses {file}, which step {creator + 1} creates, so it cannot pass when step {number} is done. " +
                    "Write the code and its test in the same step, or check this step with something that exists by then.");
            }
            else if (creator < 0)
            {
                found.Add(
                    $"Step {number} ({step.Title}): its check uses {file}, which does not exist and no step names in its files. " +
                    "List it in the files of the step that creates it.");
            }
        }

        return found;
    }

    internal static bool IsWholeProjectValidation(string command, string? root, int depth = 0)
    {
        if (depth > 2) return false;
        foreach (string segment in CommandSeparators().Split(command))
        {
            string[] words = Tokens().Matches(segment).Select(match => match.Value.Trim('"', '\''))
                .Where(word => word.Length > 0).ToArray();
            if (words.Length == 0) continue;
            string program = Path.GetFileNameWithoutExtension(words[0]).ToLowerInvariant();
            string? subcommand = words.ElementAtOrDefault(1)?.ToLowerInvariant();
            string? script = subcommand is "run" or "run-script" ? words.ElementAtOrDefault(2)?.ToLowerInvariant() : subcommand;

            if (program is "npm" or "pnpm" or "yarn" or "bun")
            {
                bool scriptCommand = subcommand is "run" or "run-script";
                if ((script is "test" or "build" or "typecheck") && words.Length <= (scriptCommand ? 3 : 2)) return true;
                if (scriptCommand && script is not null && words.Length == 3 &&
                    ScriptBody(root, script) is { } body && IsWholeProjectValidation(body, root, depth + 1)) return true;
            }

            if (program == "dotnet" && (subcommand is "build" or "test") && !words.Contains("--filter", StringComparer.OrdinalIgnoreCase)) return true;
            if (program == "cargo" && (subcommand is "build" or "test") &&
                !words.Any(word => word is "-p" or "--package" or "--test" or "--bench")) return true;
            if (program == "go" && (subcommand is "build" or "test") && words.Contains("./...", StringComparer.Ordinal) && !words.Contains("-run", StringComparer.OrdinalIgnoreCase)) return true;
            if (program == "mvn" && (subcommand is "test" or "verify" or "package")) return true;
            if ((program is "gradle" or "gradlew") && (subcommand is "test" or "build" or "check")) return true;
            if (program == "make" && (subcommand is "test" or "build" or "check") && words.Length == 2) return true;
            if ((program is "pytest" or "phpunit") && words.Length == 1) return true;
            if ((program is "python" or "python3" or "py") && subcommand == "-m" &&
                (words.ElementAtOrDefault(2) is "pytest" or "unittest") && words.Length == 3) return true;
            if (program == "node" && subcommand == "--test" &&
                !words.Skip(2).Any(word => (word is "--test-name-pattern" or "--test-only") ||
                    !word.StartsWith("-", StringComparison.Ordinal) &&
                    (word.Contains(".test.", StringComparison.OrdinalIgnoreCase) || word.Contains(".spec.", StringComparison.OrdinalIgnoreCase) ||
                     word.Contains('*') || word.Contains('/') || word.Contains('\\') || Path.HasExtension(word)))) return true;
            if (program == "tsc" && words.Contains("--noEmit", StringComparer.OrdinalIgnoreCase) &&
                !words.Skip(1).Any(word => Path.HasExtension(word))) return true;
        }

        return false;
    }

    internal static bool HasFinalValidation(string command, string? root)
    {
        if (!IsWholeProjectValidation(command, root)) return false;
        if (root is null) return true;

        // If package.json defines a build or test script, require every defined gate in the final command.
        // A unit test alone must not silently replace a production build in a web app.
        try
        {
            string packagePath = Path.Combine(root, "package.json");
            if (!File.Exists(packagePath)) return true;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packagePath));
            if (!document.RootElement.TryGetProperty("scripts", out System.Text.Json.JsonElement scripts) ||
                scripts.ValueKind != System.Text.Json.JsonValueKind.Object) return true;
            bool hasBuild = scripts.TryGetProperty("build", out _);
            bool hasTests = scripts.TryGetProperty("test", out _);
            bool hasTypecheck = scripts.TryGetProperty("typecheck", out _);
            return (!hasBuild || HasPackageScript(command, "build", root)) &&
                   (!hasTests || HasPackageScript(command, "test", root)) &&
                   (!hasTypecheck || HasPackageScript(command, "typecheck", root));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool HasPackageScript(string command, string requestedScript, string root, int depth = 0)
    {
        if (depth > 2) return false;
        foreach (string segment in CommandSeparators().Split(command))
        {
            string[] words = Tokens().Matches(segment).Select(match => match.Value.Trim('"', '\''))
                .Where(word => word.Length > 0).ToArray();
            if (words.Length == 0) continue;
            string program = Path.GetFileNameWithoutExtension(words[0]).ToLowerInvariant();
            if (program is not ("npm" or "pnpm" or "yarn" or "bun")) continue;
            string? subcommand = words.ElementAtOrDefault(1)?.ToLowerInvariant();
            string? script = subcommand is "run" or "run-script" ? words.ElementAtOrDefault(2)?.ToLowerInvariant() : subcommand;
            if (string.Equals(script, requestedScript, StringComparison.OrdinalIgnoreCase)) return true;
            if ((subcommand is "run" or "run-script") && script is not null && words.Length == 3 &&
                ScriptBody(root, script) is { } body && HasPackageScript(body, requestedScript, root, depth + 1)) return true;
        }

        return false;
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

    internal static string? ScriptBody(string? root, string script)
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

    private static bool IsDirectoryChange(string word) =>
        word.ToLowerInvariant() is "cd" or "pushd" or "chdir" or "set-location" or "sl";

    // The program the check starts with, after any leading "cd folder &&".
    internal static string? FirstProgram(string command)
    {
        foreach (string segment in CommandSeparators().Split(command))
        {
            string? first = Tokens().Matches(segment).Select(match => match.Value.Trim('"', '\'')).FirstOrDefault();
            if (first is null)
            {
                continue;
            }

            if (IsDirectoryChange(first))
            {
                continue;
            }

            return first;
        }

        return null;
    }

    internal static IReadOnlySet<string> ProgramsUsed(string command, IReadOnlySet<string> programs)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string segment in CommandSeparators().Split(command))
        {
            string? first = Tokens().Matches(segment).Select(match => match.Value.Trim('"', '\'')).FirstOrDefault();
            if (first is null || IsDirectoryChange(first))
            {
                continue;
            }

            if (programs.Contains(Path.GetFileNameWithoutExtension(first)))
            {
                used.Add(Path.GetFileNameWithoutExtension(first).ToLowerInvariant());
            }
        }

        return used;
    }

    internal static bool IsRunnable(string program, string? root, Func<string, bool> programExists)
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
    internal static bool LooksLikeProgram(string word) =>
        word.Length > 0 && (char.IsLower(word[0]) || word.Contains('.') || word.Contains('-'));

    // Arguments that look like files: a path with a slash, or a name with an extension. Flags, globs and URLs are not.
    internal static IEnumerable<string> FilesIn(string command)
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

    internal static string Resolve(string root, string path)
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
