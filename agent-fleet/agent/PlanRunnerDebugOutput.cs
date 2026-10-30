using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// A passing check does not accept a step whose changed files still print debugging output: a print or log call that names the
/// uppercase word DEBUG, or a debugger statement, left behind after it was used to find a bug. The worker gets one extra round in
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
    private IReadOnlyList<DebugLine> DebugOutputLines(PlanRecord plan, PlanStep step, IReadOnlyList<string> changedFiles)
    {
        if (IsTestsStep(step) || string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            return [];
        }

        var found = new List<DebugLine>();
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

            string[] lines = text.Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string trimmed = lines[index].Trim();
                if (DebugCallLine.IsMatch(trimmed) || trimmed is "debugger;" or "debugger")
                {
                    found.Add(new DebugLine(file, index + 1, trimmed));
                }
            }
        }

        return found;
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
