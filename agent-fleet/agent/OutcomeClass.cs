using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// The kind of result a check or a test command gave, in the run log. The first two are about the work; the rest say the
/// environment or the runner got in the way, so the work is not blamed for them.
/// </summary>
internal enum OutcomeClass
{
    Pass,
    TestFailure,
    CompileError,
    OtherError,
    TimeLimit,
    Crash,
    CleanupRejected,
    OpenHandle
}

/// <summary>
/// Sorts one command run into an <see cref="OutcomeClass"/> from its exit code, whether the runner's time limit ended it, and
/// its printed output, and names the command's family, so the run log can tell a timed-out test run from a failing test.
/// </summary>
internal static partial class OutcomeClassifier
{
    // The runner's marker for a connection or handle the tests left open after they finished.
    [GeneratedRegex(@"^\[open-after-tests\]", RegexOptions.Multiline)]
    private static partial Regex OpenHandleLine();

    // The errors that PlanFailure.BuildBroken does not name: a name that is not defined, a module that cannot be loaded.
    [GeneratedRegex(@"ReferenceError|Cannot find module|ERR_MODULE_NOT_FOUND|\bTS\d{4}:|is not defined")]
    private static partial Regex ReferenceOrModuleError();

    // The markers of failing tests: TAP's "not ok", the check mark, a runner's "failing tests:" list, a summary with a non-zero
    // count of failures, and an assertion's error name.
    [GeneratedRegex(@"not ok|✖|failing tests:|\bfail\s+0*[1-9]\d*\b|AssertionError", RegexOptions.IgnoreCase)]
    private static partial Regex FailingTestMarker();

    // The build tools, as a whole word where the name is short (tsconfig.json is not tsc).
    [GeneratedRegex(@"npm run build|\btsc\b|dotnet build", RegexOptions.IgnoreCase)]
    private static partial Regex BuildTool();

    // The test runners, whatever flags, file lists, pipes or prefixes a command gives them.
    private static readonly string[] TestRunners =
        ["node --test", "npm test", "npm run test", "tsx --test", "dotnet test", "pytest", "vitest", "jest"];

    /// <summary>
    /// The class of one run. Checked in order: the runner's time limit, a crash (the forced-exit crash text, or a native crash
    /// exit code), an open handle, a zero exit, a compile error, a test failure (only with a non-zero exit), and otherwise an
    /// other error.
    /// </summary>
    internal static OutcomeClass Of(int? exitCode, bool timedOut, string output)
    {
        if (timedOut)
        {
            return OutcomeClass.TimeLimit;
        }

        // Negative exit codes are the Windows crash statuses, read as signed numbers; 134 and 139 are the Unix abort and segfault.
        if (PlanRunner.ForcedExitCrashed(output) || exitCode is < 0 or 134 or 139)
        {
            return OutcomeClass.Crash;
        }

        if (OpenHandleLine().IsMatch(output))
        {
            return OutcomeClass.OpenHandle;
        }

        if (exitCode == 0)
        {
            return OutcomeClass.Pass;
        }

        if (PlanFailure.BuildBroken(output) || ReferenceOrModuleError().IsMatch(output))
        {
            return OutcomeClass.CompileError;
        }

        if (exitCode is not null && FailingTestMarker().IsMatch(output))
        {
            return OutcomeClass.TestFailure;
        }

        return OutcomeClass.OtherError;
    }

    /// <summary>Whether the class is an infrastructure outcome (a time limit, a crash, a rejected cleanup or an open handle), not a result of the work.</summary>
    internal static bool IsInfra(OutcomeClass outcome) =>
        outcome is OutcomeClass.TimeLimit or OutcomeClass.Crash or OutcomeClass.CleanupRejected or OutcomeClass.OpenHandle;

    /// <summary>The class's name as the run log writes it, such as "time-limit".</summary>
    internal static string Name(OutcomeClass outcome) => outcome switch
    {
        OutcomeClass.Pass => "pass",
        OutcomeClass.TestFailure => "test-failure",
        OutcomeClass.CompileError => "compile-error",
        OutcomeClass.OtherError => "other-error",
        OutcomeClass.TimeLimit => "time-limit",
        OutcomeClass.Crash => "crash",
        OutcomeClass.CleanupRejected => "cleanup-rejected",
        OutcomeClass.OpenHandle => "open-handle",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a known outcome class.")
    };

    /// <summary>The family of a shell command: "test-run" for a test runner, "build" for a build tool, otherwise "other". Case-insensitive.</summary>
    internal static string CommandFamily(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "other";
        }

        if (TestRunners.Any(runner => command.Contains(runner, StringComparison.OrdinalIgnoreCase)))
        {
            return "test-run";
        }

        return BuildTool().IsMatch(command) ? "build" : "other";
    }
}
