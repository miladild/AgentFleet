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
    /// The type errors in the files the step lists, or null when the project has no typecheck script, the step's own check is a
    /// type check, the type check passes, or it prints nothing about those files. It only adds evidence: a command that cannot run
    /// is logged and never fails or holds a step.
    /// </summary>
    private async Task<string?> TypecheckHintAsync(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model,
        int rung, int round, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkingDirectory) || (step.Verify ?? string.Empty).Contains("typecheck", StringComparison.OrdinalIgnoreCase) ||
            (step.Verify ?? string.Empty).Contains("tsc", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            string package = Path.Combine(plan.WorkingDirectory, "package.json");
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
            _store.AddEvent(plan.Id, step.Id, attempt, TypecheckHintKind, tier, node: model.ModelNode ?? model.Machine,
                detail: $"`{TypecheckScriptCommand}` reports errors in this step's files, sent back with the failed check:\n{errors}",
                modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
            return $"The project's type check (`{TypecheckScriptCommand}`) rejects this step's files. The test run does not type check, " +
                   $"so these errors may be the real reason the tests fail:\n{errors}";
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
