using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// A step's check can run code without type checking it (a test runner that strips types), so a worker can write a class the
/// compiler rejects and then chase the test failures that causes without seeing the real reason. When a step's check fails and
/// the project has a typecheck script, the script runs in the same workspace and the errors it prints for the files the step
/// lists go back to the worker with the failure. Errors in other files are left out: they usually belong to a later step.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>The run-log kind of the type errors sent back with a failed check.</summary>
    private const string TypecheckHintKind = "typecheck-hint";

    private const string TypecheckScriptCommand = "npm run typecheck";

    /// <summary>How many type error lines a failed check carries.</summary>
    private const int TypecheckLinesShown = 20;

    // A compiler error line as tsc prints it without colour: "src/a.ts(3,11): error TS2300: Duplicate identifier 'x'."
    [GeneratedRegex(@"^(?<path>[^\s(][^(]*)\(\d+,\d+\): error TS\d+:", RegexOptions.CultureInvariant)]
    private static partial Regex TypecheckErrorStart();

    /// <summary>Whether the package file declares a non-empty typecheck script.</summary>
    internal static bool HasTypecheckScript(string? packageJson)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(packageJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("scripts", out JsonElement scripts) && scripts.ValueKind == JsonValueKind.Object &&
                scripts.TryGetProperty("typecheck", out JsonElement script) && script.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(script.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The error lines whose file the predicate accepts, each with the indented lines that continue it.</summary>
    internal static IEnumerable<string> TypecheckErrorLines(string output, Func<string, bool> listed)
    {
        bool keeping = false;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            Match start = TypecheckErrorStart().Match(line);
            if (start.Success)
            {
                keeping = listed(start.Groups["path"].Value.Replace('\\', '/'));
            }
            else if (!keeping || line.Length == 0 || !char.IsWhiteSpace(line[0]))
            {
                keeping = false;
                continue;
            }

            if (keeping)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// What a type check says about a failed check, or null when the step's own check is a type check or there is nothing to say: the
    /// errors the project's typecheck script prints for the files the step lists, then the errors the compiler prints for the step's own
    /// test files, which the script may not read (see PlanRunnerTypecheckTestFiles). Each part only adds evidence: a command that cannot
    /// run is logged and never fails or holds a step.
    /// </summary>
    private async Task<string?> TypecheckHintAsync(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model,
        int rung, int round, string? checkOutput, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || (step.Verify ?? string.Empty).Contains("typecheck", StringComparison.OrdinalIgnoreCase) ||
            (step.Verify ?? string.Empty).Contains("tsc", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? scriptHint = await TypecheckScriptHintAsync(plan, step, attempt, tier, model, rung, round, cancellationToken);
        string? testFilesHint = await TypecheckTestFilesHintAsync(plan, step, attempt, tier, model, rung, round, checkOutput, scriptHint, cancellationToken);
        string? hint = scriptHint is null ? testFilesHint : testFilesHint is null ? scriptHint : $"{scriptHint}\n\n{testFilesHint}";
        return LimitRepeatedHint(plan, step, attempt, tier, model, rung, round, hint);
    }

    // "src/a.ts(28,11): error TS2300: Duplicate identifier 'history'."
    [GeneratedRegex(@"^(?<path>[^\s(][^(]*)\((?<line>\d+),\d+\): error TS2300: Duplicate identifier '(?<name>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateIdentifier();

    /// <summary>
    /// What to do about a name used twice in one file: the error says where, and a small model that is only told where keeps
    /// editing around it. Measured in three runs: a class had a private field and a method with the same name, so a call of the
    /// method failed with "is not a function", the hint named both lines in nine rounds and the worker never renamed either.
    /// Empty when no name appears twice in a file.
    /// </summary>
    internal static string DuplicateMemberAdvice(IEnumerable<string> errorLines)
    {
        var advice = new List<string>();
        foreach (IGrouping<(string Path, string Name), int> group in errorLines
            .Select(line => DuplicateIdentifier().Match(line))
            .Where(match => match.Success)
            .GroupBy(match => (match.Groups["path"].Value, match.Groups["name"].Value), match => int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture)))
        {
            int[] lines = group.Distinct().Order().ToArray();
            if (lines.Length < 2)
            {
                continue;
            }

            advice.Add($"`{group.Key.Name}`{DeclaredTwice}{group.Key.Path} (lines {string.Join(" and ", lines)}). A class cannot have a field and a " +
                       $"method with the same name, and the field hides the method, so a call such as `store.{group.Key.Name}()` fails with \"is not a function\". " +
                       $"Keep the name that the step text and the tests use for the public method, and rename the other one (for example the private field to `_{group.Key.Name}`) " +
                       "in every place it is used, including each `this." + group.Key.Name + "`. Finish the whole rename before you run a test: change the declaration " +
                       $"first, then use edit_file with replaceAll to change every `this.{group.Key.Name}.` to `this._{group.Key.Name}.` and every " +
                       $"`this.{group.Key.Name} =` to `this._{group.Key.Name} =`. The failing count can go up while the rename is half done; that is expected.");
        }

        return advice.Count == 0 ? string.Empty : "\n" + string.Join('\n', advice);
    }

    // The words of the advice about a name declared twice; the next round's edit check looks for them (see RenameDue).
    private const string DeclaredTwice = " is declared twice in ";

    // Whether the last type-check hint of the step told the worker to rename a name that is declared twice.
    private bool RenameDue(PlanRecord plan, PlanStep step) =>
        _lastTypecheckHints.TryGetValue($"{plan.Id}/{step.Id}", out (string Text, int Sent) last) && last.Text.Contains(DeclaredTwice, StringComparison.Ordinal);

    /// <summary>How many times in a row a step is sent the same type errors.</summary> Measured: one hint went out in 14 of 16 rounds and was never acted on.</summary>
    internal const int MaxIdenticalHints = 3;

    private readonly ConcurrentDictionary<string, (string Text, int Sent)> _lastTypecheckHints = new(StringComparer.Ordinal);

    // The same type errors, word for word, are sent at most MaxIdenticalHints times in a row for a step. A hint that changed is sent
    // as before, and a round with no hint starts the count again. The run log says when a hint was left out.
    private string? LimitRepeatedHint(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model, int rung, int round, string? hint)
    {
        string key = $"{plan.Id}/{step.Id}";
        if (hint is null)
        {
            _lastTypecheckHints.TryRemove(key, out _);
            return null;
        }

        int sent = _lastTypecheckHints.TryGetValue(key, out (string Text, int Sent) last) && string.Equals(last.Text, hint, StringComparison.Ordinal) ? last.Sent : 0;
        if (sent >= MaxIdenticalHints)
        {
            _store.AddEvent(plan.Id, step.Id, attempt, TypecheckHintKind, tier, node: model.ModelNode ?? model.Machine,
                detail: $"The same type errors were already sent {sent} times in a row and did not change, so they are repeated, not sent again.",
                modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
            return null;
        }

        _lastTypecheckHints[key] = (hint, sent + 1);
        return hint;
    }

    /// <summary>
    /// The type errors in the files the step lists, or null when the project has no typecheck script, the type check passes, or it
    /// prints nothing about those files.
    /// </summary>
    private async Task<string?> TypecheckScriptHintAsync(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model,
        int rung, int round, CancellationToken cancellationToken)
    {
        try
        {
            string package = Path.Combine(plan.WorkingDirectory!, "package.json");
            if (!File.Exists(package) || !HasTypecheckScript(File.ReadAllText(package)))
            {
                return null;
            }

            string[] declared = FleetPlanContext.DeclaredPaths(plan, step).ToArray();
            (bool passed, string output) = await _tools.RunRepairCommandAsync(plan, TypecheckScriptCommand, cancellationToken);
            if (passed)
            {
                return null;
            }

            string[] lines = TypecheckErrorLines(output, path => NamesDeclared(plan, declared, path)).Take(TypecheckLinesShown).ToArray();
            if (lines.Length == 0)
            {
                return null;
            }

            string errors = string.Join('\n', lines);
            string advice = DuplicateMemberAdvice(lines);
            _store.AddEvent(plan.Id, step.Id, attempt, TypecheckHintKind, tier, node: model.ModelNode ?? model.Machine,
                detail: $"`{TypecheckScriptCommand}` reports errors in this step's files:\n{errors}{advice}",
                modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
            return $"The project's type check (`{TypecheckScriptCommand}`) rejects this step's files. The test run does not type check, " +
                   $"so these errors may be the real reason the tests fail:\n{errors}{advice}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the type check could not run, so the failed check goes back without it.", plan.Id, step.Id);
            return null;
        }
    }

    private static bool NamesDeclared(PlanRecord plan, string[] declared, string printedPath)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(plan.WorkingDirectory!, printedPath));
            return declared.Any(path => FleetPlanContext.Names(path, full));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
