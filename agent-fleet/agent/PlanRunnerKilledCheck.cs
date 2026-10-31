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

    // The first "--test" flag of a node command, as a whole word (not --test-name-pattern and the like).
    [GeneratedRegex(@"^(?<head>\s*node\s(?:[^&|;]*?\s)?)--test(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NodeTestFlag();

    /// <summary>
    /// The command with the runner's force-exit flags added, or null when it is not a plain node test command or already carries
    /// them. A command chained with &amp;&amp; is left alone: only the first command is a plain one, and a rerun of part of a chain would
    /// not be the check.
    /// </summary>
    internal static string? ForcedExitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains("--test-force-exit", StringComparison.Ordinal) ||
            command.Contains("&&", StringComparison.Ordinal) || command.Contains('|') || command.Contains(';'))
        {
            return null;
        }

        Match flag = NodeTestFlag().Match(command);
        return flag.Success ? string.Concat(flag.Groups["head"].Value, "--test ", ForcedExitFlags, command.AsSpan(flag.Length)) : null;
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
