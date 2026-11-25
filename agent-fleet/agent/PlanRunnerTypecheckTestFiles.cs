using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// A project's type check can leave a step's own test files out (a tsconfig.json whose "include" lists only the source folder), and the
/// test run strips types, so a test that hands a constructor the wrong kind of value does not fail there. Measured on a real run: the
/// service under test caught the call error inside its own try block, the test died later on a missing property, and the worker spent
/// fifteen rounds looking at the wrong code. When a step's check fails with a TypeError or a ReferenceError, the step's own TypeScript
/// test files are type-checked with the compiler options of the project's tsconfig.json (see TypecheckFlags), and the errors that name
/// those files go back with the failure, as the typecheck script's errors do (see PlanRunnerTypecheck).
/// </summary>
internal sealed partial class PlanRunner
{
    // The compiler is the project's own copy, run directly: no package manager, no script, and nothing written (a check never emits).
    private const string TypecheckCompilerCommand = "node node_modules/typescript/bin/tsc --noEmit --incremental false";

    /// <summary>How many of the step's test files one compiler run names.</summary>
    private const int TypecheckTestFilesMax = 5;

    private static readonly string[] TypeScriptFileEndings = [".ts", ".tsx", ".mts", ".cts"];

    // A runtime error a type check could explain: a value of the wrong kind, or a name that is not there. Whole words, as printed.
    [GeneratedRegex(@"\b(?:TypeError|ReferenceError)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TypecheckRuntimeError();

    // A path that is safe to put on a command line: letters, digits and "_ . / @ -", and not starting with "-" or "@", which the compiler
    // would read as an option or as a file of arguments. \z and not $, because $ also matches before a final newline.
    [GeneratedRegex(@"\A[A-Za-z0-9_.][A-Za-z0-9_./@-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex TypecheckPlainPath();

    /// <summary>
    /// The type errors in the step's own TypeScript test files as the compiler reports them with the project's own options, or null when
    /// the check does not apply: the failure is not a TypeError or a ReferenceError, the project has no tsconfig.json whose options can be
    /// reproduced, the step lists no existing TypeScript test file, or the compiler passes, cannot run or prints nothing about them. Only
    /// errors in those files are kept: the errors in the step's other files are the typecheck script's to report. It only adds evidence:
    /// a command that cannot run is logged and never fails or holds a step. The caller has already left out a step whose own check is a
    /// type check (see TypecheckHintAsync). A test file whose errors the typecheck script's hint already names is not checked again: the
    /// script reads it, so its errors are not sent twice.
    /// </summary>
    private async Task<string?> TypecheckTestFilesHintAsync(PlanRecord plan, PlanStep step, int attempt, string tier, ModelAttemptResult model,
        int rung, int round, string? checkOutput, string? scriptHint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(checkOutput) || !TypecheckRuntimeError().IsMatch(checkOutput))
        {
            return null;
        }

        try
        {
            string tsconfig = Path.Combine(plan.WorkingDirectory!, "tsconfig.json");
            if (!File.Exists(tsconfig) || TypecheckFlags.FromTsconfig(File.ReadAllText(tsconfig)) is not { } flags)
            {
                return null;
            }

            (string Full, string Relative)[] tests = TypecheckTestFiles(plan, step)
                .Where(file => scriptHint is null || !scriptHint.Contains(file.Relative, StringComparison.Ordinal))
                .ToArray();
            if (tests.Length == 0)
            {
                return null;
            }

            string options = flags.Length == 0 ? string.Empty : flags + " ";
            string command = $"{TypecheckCompilerCommand} {options}{string.Join(' ', tests.Select(file => file.Relative))}";
            (bool passed, string output) = await _tools.RunRepairCommandAsync(plan, command, cancellationToken);
            if (passed)
            {
                return null;
            }

            // Only the test files: the step's other files are covered by the typecheck script's errors.
            string[] named = tests.Select(file => file.Full).ToArray();
            string[] lines = TypecheckErrorLines(output, path => NamesDeclared(plan, named, path)).Take(TypecheckLinesShown).ToArray();
            if (lines.Length == 0)
            {
                return null;
            }

            string errors = string.Join('\n', lines);
            _store.AddEvent(plan.Id, step.Id, attempt, TypecheckHintKind, tier, node: model.ModelNode ?? model.Machine,
                detail: $"The step's test files, type-checked with the project's own compiler options, report errors:\n{errors}",
                modelNode: model.ModelNode, workspaceNode: model.Machine, rung: rung, round: round);
            return "The test run does not type check. The compiler, run on this step's test files with the project's own options, " +
                   $"reports these errors, which may be the real reason the tests fail:\n{errors}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the type check of the test files could not run, so the failed check goes back without it.", plan.Id, step.Id);
            return null;
        }
    }

    // The test files the step lists that exist and can be named on a command line, with their paths relative to the project and written
    // with forward slashes. Existence is read on the hub's copy; for a step that runs on a worker the files of the attempt were synced to
    // the hub before the check ran, so a test file written in this very round is there.
    private static (string Full, string Relative)[] TypecheckTestFiles(PlanRecord plan, PlanStep step)
    {
        string root = Path.GetFullPath(plan.WorkingDirectory!);
        return FleetPlanContext.DeclaredPaths(plan, step)
            .Where(File.Exists)
            .Select(full => (Full: full, Relative: Path.GetRelativePath(root, full).Replace('\\', '/')))
            .Where(file => TypeScriptFileEndings.Any(ending => file.Relative.EndsWith(ending, StringComparison.Ordinal)) &&
                           IsTestPath(file.Relative) && TypecheckPlainPath().IsMatch(file.Relative))
            .DistinctBy(file => file.Relative, StringComparer.OrdinalIgnoreCase)
            .Take(TypecheckTestFilesMax)
            .ToArray();
    }
}
