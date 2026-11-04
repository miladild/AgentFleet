using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// A passing check does not accept a step whose changed files still print debugging output: a print or log call that names the
/// uppercase word DEBUG, or a debugger statement, left behind after it was used to find a bug. A console.log or print call the step
/// added to a file that existed before it counts too, unless the step's own text asks for logging or printing. The worker gets one extra round in
/// the same conversation to remove those lines, once per attempt. After that round the step is accepted even if lines remain, and
/// the step-done note names them.
/// </summary>
internal sealed partial class PlanRunner
{
    /// <summary>The run-log kind of the one extra round a passing step is given for the debugging output it left.</summary>
    private const string DebugOutputRoundKind = "debug-output";

    /// <summary>How many lines the extra round and the step-done note name before they only count the rest.</summary>
    private const int DebugLinesShown = 10;

    /// <summary>How many changed files are read for debugging output, and how many bytes of each (the start of a larger file).</summary>
    private const int DebugScanFiles = 40;
    private const long DebugScanBytesPerFile = 1024 * 1024;

    /// <summary>How much of a line the message shows.</summary>
    private const int DebugTextShown = 120;

    /// <summary>
    /// The print and log calls a line may use for DEBUG to count. The call name must be a whole word, and the uppercase DEBUG must
    /// come after its opening parenthesis on the same line. A lowercase "debug" in a message, or DEBUG inside a longer word, does not count.
    /// </summary>
    private const string DebugCallPattern =
        @"\b(?:console\.(?:log|debug|info|warn|error|trace)|print|println|Console\.Write(?:Line)?|Debug\.Write(?:Line)?|log\.(?:Print|Printf|Println)|logger\.\w+)\s*\(.*\bDEBUG\b";

    private static readonly Regex DebugCallLine = new(DebugCallPattern, RegexOptions.CultureInvariant);

    // Documentation is not code: a markdown example or a changelog line that shows a print call is not left-over output.
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".mdx", ".markdown", ".rst", ".adoc", ".txt"
    };

    /// <summary>A line of a changed file that prints debugging output: its file (relative, forward slashes), its number and its text.</summary>
    internal sealed record DebugLine(string File, int Number, string Text);

    /// <summary>
    /// The lines in the files a step changed that print debugging output. Test files, documentation, lock and generated files and
    /// dependency folders are not read; a file that cannot be read is skipped. Empty when the rule does not apply: the tests step of
    /// test-first mode and a plan without a project folder. The final check of a longer plan is not exempt, because that is where
    /// such lines were found.
    /// </summary>
    private async Task<IReadOnlyList<DebugLine>> DebugOutputLinesAsync(PlanRecord plan, PlanStep step, int attempt,
        IReadOnlyList<string> changedFiles, CancellationToken cancellationToken)
    {
        if (IsTestsStep(step) || string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return [];
        }

        // A print call the step added to a file that existed before it counts too, unless the step asks for logging or printing.
        bool newPrints = _harness.EnableNewPrintCheck && !StepAsksForLogging(step);
        var found = new List<DebugLine>();
        var withoutBaseline = new List<string>();
        foreach (string file in changedFiles
                     .Where(file => !IsTestPath(file) && !DocumentExtensions.Contains(Path.GetExtension(file)) &&
                         !RepairLadder.IsIncidentalFile(file) && !IsFleetOrDependencyPath(file))
                     .Take(DebugScanFiles))
        {
            string text;
            try
            {
                text = ReadStart(Path.Combine(plan.WorkingDirectory, file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            DebugBaseline? baseline = null;
            string[] lines = text.Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string trimmed = lines[index].Trim();
                if (DebugCallLine.IsMatch(trimmed) || trimmed is "debugger;" or "debugger")
                {
                    found.Add(new DebugLine(file, index + 1, trimmed));
                }
                else if (newPrints && NewPrintCall.IsMatch(trimmed))
                {
                    baseline ??= await DebugBaselineAsync(plan, step, file, cancellationToken);
                    if (baseline.Lines is null)
                    {
                        // A file the step created has no earlier version to judge its calls against; a file whose earlier version
                        // cannot be found is left to the DEBUG rule above, and the run log says so.
                        if (!baseline.CreatedByStep && !withoutBaseline.Contains(file)) withoutBaseline.Add(file);
                    }
                    else if (!baseline.Lines.Contains(trimmed))
                    {
                        found.Add(new DebugLine(file, index + 1, trimmed));
                    }
                }
            }
        }

        if (withoutBaseline.Count > 0)
        {
            _store.AddEvent(plan.Id, step.Id, attempt, DebugBaselineMissingKind,
                detail: $"The step's earlier version of {string.Join(", ", withoutBaseline.Take(DebugLinesShown))} could not be found, so print calls added there were not checked.");
        }

        return found;
    }

    /// <summary>The run-log kind written when a changed file's earlier version could not be found for the print-call check.</summary>
    private const string DebugBaselineMissingKind = "debug-output-baseline-missing";

    // A print or log call a step adds is output the step's text did not ask for, unless the text speaks of logging or printing.
    private static readonly Regex NewPrintCall = new(@"(?<![\w.])(?:console\.log|print)\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex LoggingWords = new(@"\b(?:log|logs|logging|logger|print|prints|printing|console)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static bool StepAsksForLogging(PlanStep step) => LoggingWords.IsMatch($"{step.Title} {step.Detail}");

    /// <param name="Lines">The trimmed lines of the file as the step found it; null when that is not known.</param>
    /// <param name="CreatedByStep">The file did not exist when the step began.</param>
    private sealed record DebugBaseline(HashSet<string>? Lines, bool CreatedByStep);

    // What the file looked like when the step began: the text held at the start for a file the step lists, the work snapshot's copy for
    // a file that was already changed then, and the commit's copy for any other (git says a file the snapshot lacks was unchanged).
    private async Task<DebugBaseline> DebugBaselineAsync(PlanRecord plan, PlanStep step, string file, CancellationToken cancellationToken)
    {
        string key = $"{plan.Id}:{step.Id}";
        string full = Path.GetFullPath(Path.Combine(plan.WorkingDirectory!, file));
        if (_filesAtStart.TryGetValue(key, out HashSet<string>? before) && !before.Contains(full))
        {
            return new DebugBaseline(null, CreatedByStep: true);
        }

        string? text = null;
        if (_contentAtStart.TryGetValue(key, out IReadOnlyDictionary<string, string>? atStart) && atStart.TryGetValue(file, out string? held))
        {
            text = held;
        }
        else if (_failFirstSnapshots.TryGetValue(key, out PlanTools.WorkSnapshot? snapshot))
        {
            text = snapshot.Files.TryGetValue(full, out byte[]? bytes)
                ? Encoding.UTF8.GetString(bytes)
                : snapshot.Uncaptured.Contains(full) ? null : await _tools.ReadAtCommitAsync(plan, snapshot.Head, file.Replace('\\', '/'), cancellationToken);
        }

        return new DebugBaseline(text?.Split('\n').Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal), CreatedByStep: false);
    }

    private static string ReadStart(string path)
    {
        using FileStream stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(DebugScanBytesPerFile, stream.Length)];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>Whether the extra round for debugging output already ran in this attempt.</summary>
    private bool DebugRoundGiven(PlanRecord plan, PlanStep step, int attempt) =>
        (_store.Get(plan.Id)?.Events ?? []).Any(runEvent =>
            runEvent.Kind == DebugOutputRoundKind && runEvent.StepId == step.Id && runEvent.Attempt == attempt);

    /// <summary>The file and line references, as the run log and the step-done note show them: the first few, then how many more.</summary>
    private static string DebugLocations(IReadOnlyList<DebugLine> lines)
    {
        string shown = string.Join(", ", lines.Take(DebugLinesShown).Select(line => $"{line.File}:{line.Number}"));
        int more = lines.Count - DebugLinesShown;
        return more > 0 ? $"{shown}, and {more} more" : shown;
    }

    /// <summary>What the worker is told when a passing check is not taken as the step done yet, because of debugging output.</summary>
    private static string DebugOutputMessage(IReadOnlyList<DebugLine> lines)
    {
        string shown = string.Join("; ", lines.Take(DebugLinesShown).Select(line => $"{line.File}:{line.Number} {ShortenedText(line.Text)}"));
        int more = lines.Count - DebugLinesShown;
        string list = more > 0 ? $"{shown}; and {more} more" : shown;
        return $"The check passed, but these lines print debugging output: {list}. " +
            "Remove them with edit_file; keep logging that the step itself asks for.";
    }

    private static string ShortenedText(string text) =>
        text.Length <= DebugTextShown ? text : text[..(DebugTextShown - 3)] + "...";
}
