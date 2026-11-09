using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// node's test runner prints a failing test's error only when the whole run ends. A test that opens a server and fails before it
/// closes it keeps the process alive, so the check is killed at the timeout and the worker sees test names with no reason. When
/// a killed check printed failing tests, the same command is run once more with the runner told to exit when the tests are done,
/// so the errors print and the next round can fix them.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>The flags added to a node test command for the rerun: exit when the tests are done, and stop a test that never ends.</summary>
    private const string ForcedExitFlags = "--test-force-exit --test-timeout=20000";

    /// <summary>
    /// The flag a step's own check is tried with first: exit when the tests are done. No per-test limit is added, so a test that
    /// legitimately takes long is not failed by it.
    /// </summary>
    internal const string FirstRunForcedExitFlags = "--test-force-exit";

    /// <summary>The per-test limit a worker's own node test command gets: a test that runs longer than this fails by itself.</summary>
    internal const string WorkerTestTimeoutFlag = "--test-timeout=30000";

    /// <summary>
    /// The flags added to a worker's own plain node test command when it has neither: exit when the tests are done, and fail one test
    /// that runs past the per-test limit, so one hanging test does not hold the whole command until its own limit.
    /// </summary>
    internal const string WorkerTestFlags = "--test-force-exit " + WorkerTestTimeoutFlag;

    // The first "--test" flag of a node command, as a whole word (not --test-name-pattern and the like).
    [GeneratedRegex(@"^(?<head>\s*node\s(?:[^&|;]*?\s)?)--test(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NodeTestFlag();

    // The characters that chain or pipe commands in a shell, besides "&&".
    private static readonly char[] ChainCharacters = ['|', ';'];

    /// <summary>
    /// The command a worker's plain node test call runs: the same command with the per-test limit added after "--test", and the
    /// force-exit flag too when the worker did not add it. A piped or chained command gets the flags in its first command only, when
    /// that is a node test command; the rest of the text is kept as the worker wrote it. Null when the command already sets a per-test
    /// limit, or when its first command is not a node test command. A step's own check is not changed by this (see FirstRunForcedExitFlags).
    /// </summary>
    internal static string? WorkerTestCommand(string? command)
    {
        if (command is null || command.Contains("--test-timeout", StringComparison.Ordinal))
        {
            return null;
        }

        string flags = command.Contains("--test-force-exit", StringComparison.Ordinal) ? WorkerTestTimeoutFlag : WorkerTestFlags;
        int chain = FirstChainOperator(command);
        if (chain < 0)
        {
            return WithFlagsAfterTest(command, flags);
        }

        // The first command is the test run; the operator and everything after it are kept as the worker wrote them.
        return WithFlagsAfterTest(command[..chain], flags) is { } first ? first + command[chain..] : null;
    }

    /// <summary>
    /// The command with the runner's force-exit flags added, or null when it is not a plain node test command or already carries
    /// them. A command that pipes or chains others is left alone: a rerun of part of it would not be the check.
    /// </summary>
    internal static string? ForcedExitCommand(string? command, string flags = ForcedExitFlags)
    {
        if (command?.Contains("--test-force-exit", StringComparison.Ordinal) == true)
        {
            return null;
        }

        return WithFlagsAfterTest(command, flags);
    }

    // The position of the first "&&", "|" or ";" in a command, or -1 when it has none. A single "&" (a redirect such as 2>&1) is not one.
    private static int FirstChainOperator(string command)
    {
        int and = command.IndexOf("&&", StringComparison.Ordinal);
        int other = command.IndexOfAny(ChainCharacters);
        return and < 0 ? other : other < 0 ? and : Math.Min(and, other);
    }

    // The command with the flags put right after its first "--test" flag, or null when the command is empty, chains or pipes
    // commands, or is not a node test command.
    private static string? WithFlagsAfterTest(string? command, string flags)
    {
        if (string.IsNullOrWhiteSpace(command) || FirstChainOperator(command) >= 0)
        {
            return null;
        }

        Match flag = NodeTestFlag().Match(command);
        return flag.Success ? string.Concat(flag.Groups["head"].Value, "--test ", flags, command.AsSpan(flag.Length)) : null;
    }

    /// <summary>
    /// What the check's command prints when run again with the runner forced to exit, for a check that was killed after it printed
    /// failing tests. Null when the rerun does not apply or cannot run: it only adds evidence and never fails a step.
    /// </summary>
    private async Task<string?> RerunKilledTestRunAsync(PlanRecord plan, PlanStep step, IWorkerWorkspaceSession workspace,
        string? partialOutput, CancellationToken cancellationToken)
    {
        if (ForcedExitCommand(step.Verify) is not { } command || !PlanFailure.IsTestRunOutput(partialOutput) ||
            PlanFailure.FailureSignature(partialOutput).Items.Count == 0)
        {
            return null;
        }

        try
        {
            using IDisposable workerScope = workspace.Enter();
            (_, string output) = await _tools.RunRepairCommandAsync(plan, command, cancellationToken);
            return output;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Plan {PlanId} step {StepId}: the killed check could not be run again with the test runner forced to exit.", plan.Id, step.Id);
            return null;
        }
    }
}
