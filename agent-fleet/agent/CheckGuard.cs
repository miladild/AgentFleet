using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>What the guard decided about a proposed check. Kind is "syntax-only" or "validation" when it was accepted.</summary>
internal sealed record CheckVerdict(bool Accepted, string Reason, string Kind = "");

/// <summary>
/// The deterministic gate between a model's proposal for a broken check and the check a step actually has. A step's
/// check is what keeps the model's word from being taken for the work, so a proposal is applied only when it cannot be
/// a way to pass without the work: it must be a command the plan could have been saved with (PlanReview), it must not
/// be one that passes whatever happens, and it must not be weaker than the one it replaces. A rewrite is either
/// syntax only (the same programs and arguments in a form the shell accepts) or it still runs the project's tests or
/// build and keeps everything the original validated.
/// </summary>
internal static partial class CheckGuard
{
    public const int MaxLength = 600;

    // Programs that say nothing about the project: they print, wait, or look at a folder.
    private static readonly HashSet<string> TrivialPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "echo", "true", ":", "exit", "type", "dir", "ls", "cat", "rem", "pwd", "where", "which", "write-host", "write-output",
        "set", "cd", "chdir", "pushd", "popd", "sleep", "timeout", "start-sleep", "test", "[", "ver", "date", "hostname",
        "whoami", "get-date", "get-location", "get-childitem", "gci", "get-content", "sl", "set-location"
    };

    // Programs a healed check may name although the hub does not have them: the worker may, and these are what plans use.
    private static readonly HashSet<string> KnownPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "npm", "npx", "pnpm", "yarn", "bun", "bunx", "node", "tsc", "dotnet", "pytest", "python", "python3", "py", "pip", "go", "cargo",
        "mvn", "mvnw", "gradle", "gradlew", "make", "jest", "vitest", "mocha", "php", "phpunit", "deno", "ruby", "bundle", "rake", "swift", "flutter"
    };

    private static readonly HashSet<string> Glue = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "then", "fi", "{", "}", "(", ")", "($?)", "$?", "($?", "$?)", "$lastexitcode", "($lastexitcode", "$lastexitcode)",
        "-eq", "-ne", "&", "call", "@"
    };

    private static readonly HashSet<string> VersionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "-v", "--version", "-version", "version", "-h", "--help", "help", "/?"
    };

    // A failure swallowed on purpose: the check would pass whatever the command did.
    [GeneratedRegex(@"(?ix)
        (?:\|\||;)\s*\(?\s*(?:exit(?:\s+/b)?\s+0|true|\$true|echo|cmd\s+/c\s+exit\s+0)\b
        |\bexit(?:\s+/b)?\s+0\b|silentlycontinue|-erroraction\s+ignore|erroractionpreference|\$lastexitcode\s*=\s*0")]
    private static partial Regex SwallowsFailure();

    [GeneratedRegex(@"(?ix)^\s*(?:cmd(?:\.exe)?\s+(?:/[a-z]\s+)*/c|(?:powershell|pwsh)(?:\.exe)?\s+(?:-\w+\s+)*-(?:command|c))\s+(?<inner>.+)$", RegexOptions.Singleline)]
    private static partial Regex ShellWrapper();

    [GeneratedRegex(@"&&|\|\||;|\||\r?\n")]
    private static partial Regex Separators();

    public static CheckVerdict Validate(PlanRecord plan, PlanStep step, string original, string? proposal, Func<string, bool>? programExists = null)
    {
        string? candidate = proposal?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Reject("The proposal is empty.");
        }

        if (candidate.Length > MaxLength)
        {
            return Reject($"The proposal is {candidate.Length} characters; a check is one short command (at most {MaxLength}).");
        }

        if (candidate.Contains('\n') || candidate.Contains('\r'))
        {
            return Reject("A check is one command on one line.");
        }

        if (string.Equals(Collapse(candidate), Collapse(original), StringComparison.OrdinalIgnoreCase))
        {
            return Reject("It is the check the step already has.");
        }

        if (PlanRunnerToolPolicy.CommandRefusal(candidate) is { } refusal)
        {
            return Reject(refusal);
        }

        string? root = string.IsNullOrWhiteSpace(plan.WorkingDirectory) ? null : plan.WorkingDirectory.Trim();
        IReadOnlySet<string> originalPrograms = ProgramsOf(original);
        Func<string, bool> exists = programExists ?? (program => QuickProcess.FindOnPath(program) is not null);
        bool known(string program) =>
            exists(program) || originalPrograms.Contains(Path.GetFileNameWithoutExtension(program)) || KnownPrograms.Contains(Path.GetFileNameWithoutExtension(program));

        // A shell wrapper (cmd /c "...", powershell -Command "...") is how a command gets past a shell's syntax, not what
        // runs: the rules read the command inside it.
        string inner = Unwrap(candidate);
        List<PlanStep> ordered = [.. plan.Steps];
        int index = ordered.FindIndex(candidateStep => candidateStep.Id == step.Id);
        if (index < 0)
        {
            return Reject("The step is not in the plan.");
        }

        List<PlanStepInput> inputs = ordered.Select(planStep => new PlanStepInput(
            planStep.Title, planStep.Detail, [.. planStep.Files], planStep.Id == step.Id ? inner : planStep.Verify,
            planStep.Tier, planStep.ParallelGroup)).ToList();
        IReadOnlyList<string> problems = PlanReview.CheckProblems(root, inputs, index, known);
        if (problems.Count > 0)
        {
            return Reject(problems[0]);
        }

        if (SwallowsFailure().IsMatch(candidate))
        {
            return Reject("It would pass whatever the command does (the failure is swallowed).");
        }

        List<string[]> proposed = Segments(candidate);

        // The plan's rules read a command as && between programs; `if ($?) { npm test }` hides npm test from them.
        string plain = string.Join(" && ", proposed.Select(words => string.Join(' ', words)));
        if (PlanReview.NeverFinishes(plain, root) is { } endless)
        {
            return Reject($"It runs {endless}, which does not exit on its own.");
        }

        List<string[]> before = Segments(original);
        if (!proposed.Any(Substantive))
        {
            return Reject("It only prints, waits or looks at a folder, so it would pass without the work.");
        }

        if (step.Id == ordered.Max(planStep => planStep.Id) && !PlanReview.HasFinalValidation(plain, root))
        {
            return Reject("It is the plan's last step: its check has to be a whole-project build or test-suite command that covers every gate the project defines.");
        }

        if (Flatten(before).SequenceEqual(Flatten(proposed), StringComparer.Ordinal))
        {
            return new CheckVerdict(true, "Same programs and arguments, written so the shell accepts them.", "syntax-only");
        }

        if (!proposed.Any(words => IsValidation(words)))
        {
            return Reject("It changes what the check runs, so it has to run the project's tests, typecheck or build.");
        }

        // The only new things a rewrite may add are the project's own tests and builds, and a change of folder: a model
        // reading output it did not write is not given a way to make the machine run anything else. That includes a
        // harmless-looking last command (`npm test; ls`, `npm test || dir`, `npm test | cat`): the exit code of a chain is the
        // last command's, so a trivial one at the end would make the check pass whatever the tests did.
        foreach (string[] added in proposed.Where(words => words[0] != "cd" && !IsValidation(words) &&
                     !before.Any(original => original.SequenceEqual(words, StringComparer.Ordinal))))
        {
            return Reject($"It adds `{string.Join(' ', added)}`, which the original check did not run and which is not a test or a build.");
        }

        foreach (string[] segment in before.Where(words => IsValidation(words) && !IsEndless(words, root)))
        {
            if (!proposed.Any(words => words.SequenceEqual(segment, StringComparer.Ordinal) || Covers(words, segment, root)))
            {
                return Reject($"It drops `{string.Join(' ', segment)}`, which the original check ran.");
            }
        }

        return new CheckVerdict(true, "Still runs the project's tests or build and keeps what the original validated.", "validation");
    }

    /// <summary>A command in the check's own words, shell glue removed: what the shell is asked to run, not how it is spelled.</summary>
    internal static List<string[]> Segments(string command)
    {
        string text = Unwrap(command);
        var segments = new List<string[]>();
        foreach (string part in Separators().Split(text))
        {
            List<string> words = [];
            foreach (Match match in PlanReview.Tokens().Matches(part))
            {
                string word = Clean(match.Value);
                if (word.Length == 0)
                {
                    continue;
                }

                string lower = word.ToLowerInvariant();
                if (words.Count > 0 && words[^1] is "-eq" or "-ne" && lower is "0" or "0)")
                {
                    continue;
                }

                if (Glue.Contains(word))
                {
                    continue;
                }

                words.Add(words.Count == 0 ? ProgramName(lower) : word);
            }

            // `exit $LASTEXITCODE` (its variable is glue) only hands the exit code on.
            if (words.Count == 0 || words is ["exit"])
            {
                continue;
            }

            // cd /d folder is cd folder.
            if (words[0] == "cd" && words.Count > 1 && words[1].Equals("/d", StringComparison.OrdinalIgnoreCase))
            {
                words.RemoveAt(1);
            }

            segments.Add([.. words]);
        }

        return segments;
    }

    private static string Unwrap(string command)
    {
        string text = command.Trim();
        for (int depth = 0; depth < 2; depth++)
        {
            Match wrapper = ShellWrapper().Match(text);
            if (!wrapper.Success)
            {
                break;
            }

            text = wrapper.Groups["inner"].Value.Trim();
            if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
            {
                text = text[1..^1];
            }
        }

        return text;
    }

    // Quotes and escapes are the shells' business, and slashes the same on both; everything else is kept as written.
    private static string Clean(string token)
    {
        string word = token.Trim().Replace("\"", string.Empty).Replace("'", string.Empty).Replace("`", string.Empty).Replace('\\', '/');
        return word.StartsWith("./", StringComparison.Ordinal) ? word[2..] : word;
    }

    private static string ProgramName(string lower) => lower switch
    {
        "chdir" or "set-location" or "sl" or "pushd" => "cd",
        _ => Path.GetFileNameWithoutExtension(lower).Length > 0 ? Path.GetFileNameWithoutExtension(lower) : lower
    };

    private static IEnumerable<string> Flatten(IEnumerable<string[]> segments) => segments.SelectMany(words => words);

    private static string Collapse(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private static IReadOnlySet<string> ProgramsOf(string command) =>
        Segments(command).Select(words => words[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Substantive(string[] words)
    {
        if (TrivialPrograms.Contains(words[0]))
        {
            return false;
        }

        return !(words.Length >= 2 && words.Skip(1).All(word => VersionWords.Contains(word)));
    }

    /// <summary>A command that runs the project's own tests, typecheck or build (not a linter, not a script of unknown purpose).</summary>
    internal static bool IsValidation(string[] words)
    {
        if (words.Length == 0)
        {
            return false;
        }

        string program = words[0];
        string? second = words.ElementAtOrDefault(1)?.ToLowerInvariant();
        switch (program)
        {
            case "npm" or "pnpm" or "yarn" or "bun":
                string? script = second is "run" or "run-script" ? words.ElementAtOrDefault(2)?.ToLowerInvariant() : second;
                return script is not null && ValidationScript(script);
            case "npx" or "bunx" or "pnpx":
                string? tool = words.Skip(1).FirstOrDefault(word => !word.StartsWith('-'))?.ToLowerInvariant();
                return tool is "jest" or "vitest" or "mocha" or "ava" or "tsc" or "tap" or "playwright" or "cypress";
            case "node":
                return words.Skip(1).Any(word => word is "--test" or "--check");
            case "tsc":
                return !words.Contains("-w") && !words.Contains("--watch");
            case "dotnet":
                return second is "test" or "build";
            case "pytest" or "phpunit" or "jest" or "vitest" or "mocha" or "ava":
                return true;
            case "python" or "python3" or "py":
                int module = Array.IndexOf(words, "-m");
                return module >= 0 && words.ElementAtOrDefault(module + 1) is "pytest" or "unittest" or "mypy" or "compileall";
            case "go":
                return second is "test" or "build" or "vet";
            case "cargo":
                return second is "test" or "build" or "check";
            case "mvn" or "mvnw":
                return words.Skip(1).Any(word => word is "test" or "verify" or "package" or "compile");
            case "gradle" or "gradlew":
                return words.Skip(1).Any(word => word is "test" or "build" or "check");
            case "make" or "rake" or "deno" or "swift" or "flutter":
                return second is "test" or "build" or "check";
            default:
                return false;
        }
    }

    private static bool ValidationScript(string script) =>
        script.StartsWith("test", StringComparison.Ordinal) || script.StartsWith("build", StringComparison.Ordinal) ||
        script.StartsWith("typecheck", StringComparison.Ordinal) || script.StartsWith("type-check", StringComparison.Ordinal) ||
        script.StartsWith("check", StringComparison.Ordinal) || script.StartsWith("verify", StringComparison.Ordinal) ||
        script.StartsWith("compile", StringComparison.Ordinal) || script == "ci";

    private static bool IsEndless(string[] words, string? root) =>
        PlanReview.NeverFinishes(string.Join(' ', words), root) is not null;

    // A whole-project run of the same tool contains a narrower run of it (`node --test` runs what `node --test one.test.js`
    // ran), and so does the same run with more arguments, as long as it is still a whole-project run (`dotnet test
    // --blame-hang-timeout 60s` is, `dotnet test --filter X` is not).
    private static bool Covers(string[] wider, string[] narrower, string? root)
    {
        if (!PlanReview.IsWholeProjectValidation(string.Join(' ', wider), root))
        {
            return false;
        }

        bool sameTool = wider[0] == narrower[0] &&
            string.Equals(wider.ElementAtOrDefault(1), narrower.ElementAtOrDefault(1), StringComparison.OrdinalIgnoreCase);
        bool extendsIt = wider.Length >= narrower.Length && wider.Take(narrower.Length).SequenceEqual(narrower, StringComparer.Ordinal);
        return sameTool && (extendsIt || !PlanReview.IsWholeProjectValidation(string.Join(' ', narrower), root));
    }

    private static CheckVerdict Reject(string reason) => new(false, reason);
}
