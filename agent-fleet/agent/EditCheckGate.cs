using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>What one attempt's edit check did: checks run, edits undone, restores, the best state and whether it was switched off.</summary>
internal sealed record EditCheckSummary(
    int Checks,
    int Refused,
    int Restored,
    bool FinalRestored,
    RoundScore? Best,
    bool Stopped,
    bool Disabled,
    string? DisabledReason,
    int FailedWrites)
{
    public string Describe()
    {
        string best = Best is { } score ? EditCheckGate.Now(score) : "no baseline";
        string state = Disabled ? $"switched off ({DisabledReason})" : "not switched off";
        string writes = FailedWrites > 0 ? $", {FailedWrites} write{(FailedWrites == 1 ? string.Empty : "s")} failed" : string.Empty;
        return $"{Checks} checks, {Refused} refused, {Restored} restored, best {best}, {state}{writes}.";
    }
}

/// <summary>
/// Runs the step's approved check after each file write or edit a worker model makes, so a model hears at once when an
/// edit stops a loading project from loading, and a run that stops improving goes back to its best state. Scoring is the
/// runner's own (<c>scoreOf</c>), so this gate and the runner agree on what better means. One gate serves one attempt.
/// </summary>
internal sealed class EditCheckGate
{
    private const string PassedLine = "All checks pass. The fleet runs the final check now.";
    private const string BestAction = "This is the best state so far.";
    private const string NotBetterAction = "It did not improve on the best state.";
    private const string UndoneAction = "The edit was undone.";
    private const string RestoredAction = "The files were put back to the best state.";
    private static readonly Regex ProblemLine = new("error|✖|✗|×|fail|unexpected|expected|exception", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExpectedLine = new(@"\bexpected\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IEditCheckFiles _files;
    private readonly Func<CancellationToken, Task<string>> _runCheck;
    private readonly Func<string, RoundScore?> _scoreOf;
    private readonly EditCheckSettings _settings;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Touched> _touched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _refusals = new(StringComparer.Ordinal);
    private PendingEdit? _pending;
    private BestState? _best;
    private bool _baselineLoads;
    private bool _baselineTaken;
    private RoundScore? _lastScore;
    private bool _lastChecked;
    private int _checks;
    private int _refused;
    private int _restores;
    private int _notImproving;
    private int _failedWrites;
    private bool _stopRequested;
    private bool _disabled;
    private string? _disabledReason;

    public EditCheckGate(IEditCheckFiles files, Func<CancellationToken, Task<string>> runCheck, Func<string, RoundScore?> scoreOf,
        EditCheckSettings settings, Action<string> log)
    {
        _files = files;
        _runCheck = runCheck;
        _scoreOf = scoreOf;
        _settings = settings;
        _log = log;
        if (!settings.Enabled) Disable("switched off in the settings");
    }

    /// <summary>True once the model's round should end: every check passes, or the run stalled twice.</summary>
    public bool StopRequested => _stopRequested;

    /// <summary>Gives up the check for the rest of the attempt. The files are left as they are.</summary>
    public void Disable(string reason)
    {
        if (_disabled) return;
        _disabled = true;
        _disabledReason = reason;
        _lastChecked = false;
        _pending = null;
        _log($"edit check switched off: {reason}");
    }

    /// <summary>Called before a write or edit. The first call runs the baseline check.</summary>
    public async Task BeforeMutationAsync(string path, CancellationToken cancellationToken)
    {
        if (_disabled) return;
        _pending = null;
        if (!_baselineTaken)
        {
            _baselineTaken = true;
            await TakeBaselineAsync(cancellationToken);
            if (_disabled) return;
        }

        try
        {
            byte[]? previous = await _files.ReadBytesAsync(path, cancellationToken);
            _pending = new PendingEdit(_files.Canonical(path), path, previous);
        }
        catch (WorkerPathRefusedException exception)
        {
            // The file tools refuse this path themselves (a frozen test file, a file the project does not sync), so no edit
            // happens here to check. The gate stays on for the other edits.
            _log($"edit {path}: not tracked, the tools refuse this path: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Disable($"could not read a file before an edit: {exception.Message}");
        }
    }

    /// <summary>
    /// Called after a write or edit that the tool reported as done. Returns the text the model sees: the tool's reply, or
    /// the refusal, plus one line with the result of the check.
    /// </summary>
    public async Task<string> AfterMutationAsync(string path, string toolReply, CancellationToken cancellationToken)
    {
        PendingEdit? pending = _pending;
        _pending = null;
        if (_disabled || pending is null || _best is null) return toolReply;

        byte[]? current;
        try
        {
            current = await _files.ReadBytesAsync(path, cancellationToken);
        }
        catch (WorkerPathRefusedException exception)
        {
            _log($"edit {path}: not tracked, the tools refuse this path: {exception.Message}");
            return toolReply;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Disable($"could not read {path} after an edit: {exception.Message}");
            return toolReply;
        }

        if (!_touched.TryGetValue(pending.Key, out Touched? touched))
        {
            touched = new Touched(path, pending.Previous);
            _touched[pending.Key] = touched;
        }

        touched.Current = current;
        // The state before this edit: an undone edit puts it back, so FinishAsync judges the files as they now are.
        RoundScore? scoreBeforeEdit = _lastScore;
        bool checkedBeforeEdit = _lastChecked;
        _lastChecked = false;
        var clock = Stopwatch.StartNew();
        string? output = await RunCheckAsync(cancellationToken);
        long milliseconds = clock.ElapsedMilliseconds;
        if (output is null) return toolReply;

        _checks++;
        if (!output.StartsWith("Exit code:", StringComparison.Ordinal))
        {
            Disable("the check did not run: its output does not start with the exit code");
            return toolReply;
        }

        if (Score(output) is not { } score)
        {
            _log($"edit {path}: {milliseconds} ms, no failing test named, kept, not counted");
            return $"{toolReply}\n{Suffix($"no failing test named", _best.Score, "The edit is kept and not counted.")}";
        }

        _lastScore = score;
        _lastChecked = true;
        if (output.StartsWith("Exit code: 0", StringComparison.Ordinal))
        {
            _best = new BestState(score, Snapshot());
            _notImproving = 0;
            _stopRequested = true;
            _log($"edit {path}: {milliseconds} ms, loads yes, {score.Failures} failing, best {Best(score)}, stop: all checks pass");
            return $"{toolReply}\n{PassedLine}\n{SuffixLine(score, score, BestAction)}";
        }

        string? warning = null;
        if (_baselineLoads && !score.Compiles)
        {
            int refusals = _refusals.GetValueOrDefault(pending.Key);
            if (refusals < _settings.RefusalsBeforeKeeping)
            {
                _refusals[pending.Key] = refusals + 1;
                _refused++;
                return await UndoAsync(path, pending, touched, output, score, milliseconds, scoreBeforeEdit, checkedBeforeEdit, cancellationToken);
            }

            warning = $"Warning: the project no longer loads after this edit to {path}. The edit is kept, so fix the cause in your next edit:\n{ProblemLines(output)}";
        }

        if (_best.Score.IsWorseThan(score))
        {
            _best = new BestState(score, Snapshot());
            _notImproving = 0;
            _log($"edit {path}: {milliseconds} ms, loads {Yes(score)}, {score.Failures} failing, best {Best(score)}, kept as the best state");
            return $"{warning ?? toolReply}\n{SuffixLine(score, score, BestAction)}";
        }

        _notImproving++;
        if (_notImproving > _settings.Patience)
        {
            return await RestoreAsync(path, toolReply, output, score, milliseconds, cancellationToken);
        }

        _log($"edit {path}: {milliseconds} ms, loads {Yes(score)}, {score.Failures} failing, best {Best(_best.Score)}, kept, not an improvement");
        return $"{warning ?? toolReply}\n{SuffixLine(score, _best.Score, NotBetterAction)}";
    }

    /// <summary>
    /// Called once the attempt's model is done. If the last checked state is worse than the best one, the best one is put back,
    /// so the final check and the sync to the hub see it. Never throws.
    /// </summary>
    public async Task<EditCheckSummary> FinishAsync(CancellationToken cancellationToken)
    {
        _pending = null;
        bool finalRestored = false;
        if (_best is { } best && _lastChecked && _lastScore is { } last && last.IsWorseThan(best.Score))
        {
            await WriteBestAsync(cancellationToken);
            _lastScore = best.Score;
            finalRestored = true;
            _log($"attempt end: best state ({Now(best.Score)}) put back");
        }

        return new EditCheckSummary(_checks, _refused, _restores, finalRestored, _best?.Score, _stopRequested, _disabled, _disabledReason, _failedWrites);
    }

    /// <summary>The line that ends every reply the gate changes, in fixed words.</summary>
    internal static string SuffixLine(RoundScore now, RoundScore best, string action) =>
        $"[Check after this edit: {Now(now)}, best so far {Best(best)}. {action}]";

    /// <summary>How a state reads in the words the model is given: "6 failing", or "does not load".</summary>
    internal static string Now(RoundScore score) => score.Compiles ? $"{score.Failures} failing" : "does not load";

    /// <summary>How the best state reads after "best so far": the failing count, or "does not load".</summary>
    internal static string Best(RoundScore score) => score.Compiles ? score.Failures.ToString() : "does not load";

    /// <summary>The problem lines of a check output: the first error-like line and the lines after it, at most eight and 900 characters.</summary>
    internal static string ProblemLines(string output)
    {
        string[] lines = output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int first = Array.FindIndex(lines, line => ProblemLine.IsMatch(line));
        var picked = new List<string>();
        int characters = 0;
        for (int index = Math.Max(first, 0); index < lines.Length && picked.Count < 8; index++)
        {
            string line = lines[index].TrimEnd();
            if (line.Length == 0) continue;
            int room = 900 - characters;
            if (room <= 0) break;
            if (line.Length > room)
            {
                picked.Add(line[..room]);
                break;
            }

            picked.Add(line);
            characters += line.Length + 1;
        }

        return string.Join('\n', picked);
    }

    /// <summary>The expected and actual lines of the first failing assertion, when the output has them.</summary>
    internal static string? FirstFailureDetail(string output)
    {
        string[] lines = output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int expected = Array.FindIndex(lines, line => ExpectedLine.IsMatch(line));
        if (expected < 0) return null;
        int start = Math.Max(expected - 1, 0);
        int end = Math.Min(expected + 2, lines.Length - 1);
        string detail = string.Join('\n', lines[start..(end + 1)].Select(line => line.TrimEnd()));
        return detail.Length > 400 ? detail[..400] : detail;
    }

    private static string Suffix(string now, RoundScore best, string action) =>
        $"[Check after this edit: {now}, best so far {Best(best)}. {action}]";

    private static string Yes(RoundScore score) => score.Compiles ? "yes" : "no";

    private RoundScore? Score(string output) =>
        output.StartsWith("Exit code: 0", StringComparison.Ordinal) ? new RoundScore(true, 0) : _scoreOf(output);

    /// <summary>Runs the check under three times the maximum time. A check that cannot finish or throws disables the gate.</summary>
    private async Task<string?> RunCheckAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_settings.MaximumCheckTime * 3);
        try
        {
            return await _runCheck(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Disable("the check did not finish within three times the maximum check time");
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Disable($"the check threw: {exception.Message}");
            return null;
        }
    }

    private async Task TakeBaselineAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        string? output = await RunCheckAsync(cancellationToken);
        long milliseconds = clock.ElapsedMilliseconds;
        if (output is null) return;

        _checks++;
        if (clock.Elapsed > _settings.MaximumCheckTime)
        {
            Disable("the baseline check took longer than the maximum check time");
            return;
        }

        if (!output.StartsWith("Exit code:", StringComparison.Ordinal))
        {
            Disable("the check did not run: its output does not start with the exit code");
            return;
        }

        if (Score(output) is not { } score)
        {
            Disable("the baseline check output could not be scored");
            return;
        }

        _baselineLoads = score.Compiles;
        _best = new BestState(score, new Dictionary<string, byte[]?>(StringComparer.Ordinal));
        _lastScore = score;
        _lastChecked = true;
        _log($"baseline check: {milliseconds} ms, loads {Yes(score)}, {score.Failures} failing");
    }

    /// <summary>The edit that does not load is undone, and the file is exactly as it was before it.</summary>
    private async Task<string> UndoAsync(string path, PendingEdit pending, Touched touched, string output, RoundScore score,
        long milliseconds, RoundScore? scoreBeforeEdit, bool checkedBeforeEdit, CancellationToken cancellationToken)
    {
        try
        {
            await _files.WriteBytesAsync(path, pending.Previous, cancellationToken);
            touched.Current = pending.Previous;
            _lastScore = scoreBeforeEdit;
            _lastChecked = checkedBeforeEdit;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _failedWrites++;
            Disable($"could not undo an edit to {path}: {exception.Message}");
            return $"Warning: the project no longer loads after this edit to {path}, and the edit could not be undone:\n{ProblemLines(output)}";
        }

        _log($"edit {path}: {milliseconds} ms, loads no, best {Best(_best!.Score)}, refused and undone");
        return $"Error: nothing was changed in {path}. After this edit the project no longer loads:\n{ProblemLines(output)}\n" +
               $"The file is exactly as it was before this edit. Fix the cause in your next edit.\n{SuffixLine(score, _best.Score, UndoneAction)}";
    }

    /// <summary>After too many edits that did not help, the files go back to the best state and the model is told which.</summary>
    private async Task<string> RestoreAsync(string path, string toolReply, string output, RoundScore score, long milliseconds, CancellationToken cancellationToken)
    {
        int notHelped = _notImproving;
        _notImproving = 0;
        _restores++;
        List<string> putBack = await WriteBestAsync(cancellationToken);
        _lastScore = _best!.Score;
        _lastChecked = true;
        if (_restores >= 2) _stopRequested = true;

        var text = new StringBuilder(toolReply);
        string files = putBack.Count == 0 ? "no file differed" : string.Join(", ", putBack);
        text.Append($"\nRestored: the last {notHelped} edits did not help. The files are back at the best state so far ({Now(_best.Score)}): {files}. ");
        text.Append("Read them again before you edit them.");
        if (FirstFailureDetail(output) is { } detail)
        {
            text.Append($"\nThe first failing test:\n{detail}");
        }

        text.Append($"\n{SuffixLine(score, _best.Score, RestoredAction)}");
        _log(
            $"edit {path}: {milliseconds} ms, loads {Yes(score)}, {score.Failures} failing, best {Best(_best.Score)}, restored {putBack.Count} file(s)" +
            (_stopRequested ? ", stop: stalled" : string.Empty));
        return text.ToString();
    }

    /// <summary>Writes the best snapshot back: every touched file whose bytes differ. A file touched after the best state goes back to its original bytes.</summary>
    private async Task<List<string>> WriteBestAsync(CancellationToken cancellationToken)
    {
        var written = new List<string>();
        Dictionary<string, byte[]?> snapshot = _best?.Snapshot ?? new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach ((string key, Touched touched) in _touched)
        {
            byte[]? target = snapshot.TryGetValue(key, out byte[]? saved) ? saved : touched.Original;
            if (SameBytes(touched.Current, target)) continue;
            try
            {
                await _files.WriteBytesAsync(touched.Path, target, cancellationToken);
                touched.Current = target;
                written.Add(touched.Path);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _failedWrites++;
                _log($"could not put {touched.Path} back: {exception.Message}");
            }
        }

        return written;
    }

    private Dictionary<string, byte[]?> Snapshot() =>
        _touched.ToDictionary(pair => pair.Key, pair => pair.Value.Current, StringComparer.Ordinal);

    private static bool SameBytes(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private sealed class Touched(string path, byte[]? original)
    {
        public string Path { get; } = path;

        public byte[]? Original { get; } = original;

        public byte[]? Current { get; set; } = original;
    }

    private sealed record PendingEdit(string Key, string Path, byte[]? Previous);

    private sealed record BestState(RoundScore Score, Dictionary<string, byte[]?> Snapshot);
}
