using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>What the stuck-step guard asks for after a result.</summary>
internal enum StuckAction
{
    None,
    Note,
    Stop
}

/// <summary>The guard's answer to one result: what to do, how many times in a row the same outcome has come back, and what to tell the model.</summary>
internal readonly record struct StuckDecision(StuckAction Action, int Repeats, OutcomeClass Outcome, string? Note);

/// <summary>The run of identical results a step attempt has so far.</summary>
internal readonly record struct StuckStreak(string Family, OutcomeClass Outcome, int Repeats);

/// <summary>
/// Counts, for one step attempt, how many times in a row a kind of command came back with the same infrastructure outcome: the
/// runner's time limit, a crash at exit, an open handle, rejected cleanup. The count is by the kind of outcome, not the command's
/// text, because a worker whose tests keep hanging writes the same command a different way each time, and a guard that compares
/// text never sees a repeat. The second repeat gets a note, the third a stop. Ordinary failures (a failing test, a compile error)
/// are not this guard's business and end a streak, as does a pass. A streak belongs to the attempt, so it survives the
/// conversation being reset inside it. The invoker and the round loop both call it, so it takes a lock.
/// </summary>
internal sealed partial class StuckStepGuard
{
    private const int MaxTracked = 512;

    // The runner's marker for a connection or handle the tests left open, followed by the resource's name.
    [GeneratedRegex(@"^\[open-after-tests\][ \t]*(?<name>\S[^\r\n]*)?$", RegexOptions.Multiline)]
    private static partial Regex OpenHandleLine();

    private readonly object _gate = new();
    private readonly Dictionary<string, StuckStreak> _streaks = new(StringComparer.Ordinal);
    private readonly int _noteAfter;
    private readonly int _stopAfter;

    /// <param name="noteAfter">The repeat that gets a note.</param>
    /// <param name="stopAfter">The repeat that gets a stop; never below <paramref name="noteAfter"/>.</param>
    public StuckStepGuard(int noteAfter = 2, int stopAfter = 3)
    {
        _noteAfter = Math.Max(1, noteAfter);
        _stopAfter = Math.Max(_noteAfter, stopAfter);
    }

    public StuckStepGuard(HarnessOptions options) : this(options.StuckNoteAfter, options.StuckStopAfter)
    {
    }

    /// <summary>
    /// Counts one result of a command family for the attempt and says what to do about it.
    /// </summary>
    /// <param name="output">What the command printed; read only for the name of an open resource.</param>
    public StuckDecision Observe(string planId, int stepId, int attempt, string family, OutcomeClass outcome, string? output = null)
    {
        string key = KeyOf(planId, stepId, attempt);
        int repeats;
        lock (_gate)
        {
            if (!OutcomeClassifier.IsInfra(outcome))
            {
                _streaks.Remove(key);
                return new StuckDecision(StuckAction.None, 0, outcome, null);
            }

            repeats = _streaks.TryGetValue(key, out StuckStreak current) && current.Family == family && current.Outcome == outcome
                ? current.Repeats + 1
                : 1;
            if (_streaks.Count >= MaxTracked && !_streaks.ContainsKey(key))
            {
                _streaks.Clear();
            }

            _streaks[key] = new StuckStreak(family, outcome, repeats);
        }

        if (repeats < _noteAfter)
        {
            return new StuckDecision(StuckAction.None, repeats, outcome, null);
        }

        string note = Diagnosis(family, outcome, repeats, output);
        return repeats >= _stopAfter
            ? new StuckDecision(StuckAction.Stop, repeats, outcome, note + " " + StopSentence)
            : new StuckDecision(StuckAction.Note, repeats, outcome, note);
    }

    /// <summary>The attempt's streak so far, or null when it has none.</summary>
    public StuckStreak? Streak(string planId, int stepId, int attempt)
    {
        lock (_gate)
        {
            return _streaks.TryGetValue(KeyOf(planId, stepId, attempt), out StuckStreak streak) ? streak : null;
        }
    }

    /// <summary>Forgets the attempt's streak.</summary>
    public void Reset(string planId, int stepId, int attempt)
    {
        lock (_gate)
        {
            _streaks.Remove(KeyOf(planId, stepId, attempt));
        }
    }

    internal const string StopSentence = "This round ends here, and the next round starts fresh with this diagnosis.";

    private static string KeyOf(string planId, int stepId, int attempt) => $"{planId}:{stepId}:{attempt}";

    private static string Diagnosis(string family, OutcomeClass outcome, int repeats, string? output)
    {
        string command = family == "test-run" ? "test command" : "command";
        return outcome switch
        {
            OutcomeClass.TimeLimit =>
                $"The {command} has now hit the time limit {repeats} times in a row, however it was written. Running it again will not help. " +
                "Find what keeps the process alive or what never finishes (a server or timer left open, a test waiting for input) and fix that in the code or the test.",
            OutcomeClass.Crash =>
                $"The {command} has now crashed at exit {repeats} times in a row, however it was written. Running it again will not help. " +
                "The tests may have finished: find what the process still holds when it exits (a connection or handle left open) and close it in the code or the test.",
            OutcomeClass.OpenHandle =>
                $"The tests finish, but something stays open after them{OpenResource(output)}, so the {command} does not exit on its own. " +
                $"This has now happened {repeats} times in a row, however the command was written. Running it again will not help: close it in the code or the test.",
            OutcomeClass.CleanupRejected =>
                $"The {command} has now ended with files left behind that the runner rejected {repeats} times in a row, however it was written. Running it again will not help. " +
                "Find which files it leaves (reports, caches, temporary output) and make it remove them or write somewhere else.",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not an infrastructure outcome.")
        };
    }

    // " (name)" when the output names the resource that stays open, otherwise nothing.
    private static string OpenResource(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return string.Empty;
        }

        string name = OpenHandleLine().Match(output).Groups["name"].Value.Trim();
        return name.Length == 0 ? string.Empty : $" ({name})";
    }
}
