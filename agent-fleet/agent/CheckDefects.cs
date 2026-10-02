using System.Text.RegularExpressions;

namespace AgentFleet;

internal enum CheckDefectKind
{
    NeverFinishes,
    ShellSyntax,
    ProgramMissing,
    FileMissing,
    TimedOut,
    ReportedByModel
}

/// <summary>A sign that the check, not the work, is what is wrong. A sign is only a reason to ask the auditor.</summary>
internal sealed record CheckDefect(CheckDefectKind Kind, string Evidence)
{
    public string Name => Kind switch
    {
        CheckDefectKind.NeverFinishes => "the check never finishes",
        CheckDefectKind.ShellSyntax => "the shell cannot parse the check",
        CheckDefectKind.ProgramMissing => "the check's program is not there",
        CheckDefectKind.FileMissing => "a file the check names is not there",
        CheckDefectKind.TimedOut => "the check timed out twice with the same output",
        _ => "the model reported that the check cannot pass"
    };
}

/// <summary>
/// Tells a broken check from a failing one. A check that cannot start, parse or finish is a defect of the plan and is
/// healed automatically; a check that ran and failed on its merits (a failing test, a compiler error) is the model's to
/// fix and never goes through here. Every pattern below can only come from the shell or the launcher, never from the
/// project's own code: a JavaScript SyntaxError, a TS1005 or an assertion is not one of them.
/// </summary>
internal static partial class CheckDefects
{
    // PowerShell, cmd and sh each report a command they cannot parse in their own words.
    [GeneratedRegex(@"(?ix)
        \bParserError\b
        |is\snot\sa\svalid\sstatement\sseparator
        |Unexpected\stoken\s'[^']*'\sin\sexpression\sor\sstatement
        |Missing\sclosing\s'\)'\sin\sexpression
        |The\ssyntax\sof\sthe\scommand\sis\sincorrect
        |was\sunexpected\sat\sthis\stime
        |The\sfilename,\sdirectory\sname,\sor\svolume\slabel\ssyntax\sis\sincorrect
        |(?:sh|bash|dash):\s(?:-c:\s)?(?:line\s\d+:\s)?(?:\d+:\s)?syntax\serror
        |syntax\serror\snear\sunexpected\stoken")]
    private static partial Regex ShellSyntax();

    [GeneratedRegex(@"(?ix)
        The\sterm\s'(?<p>[^']+)'\sis\snot\srecognized\sas\sthe\sname
        |'(?<p>[^']+)'\sis\snot\srecognized\sas\san\sinternal\sor\sexternal\scommand
        |(?:^|[\r\n])(?:(?:/bin/)?(?:sh|bash|dash):\s(?:line\s\d+:\s)?(?:\d+:\s)?)?(?<p>[\w.\-/\\]+):\s(?:command\s)?not\sfound
        |failed\sto\sstart\s'(?<p>[^']+)'")]
    private static partial Regex MissingProgram();

    [GeneratedRegex(@"(?ix)
        Could\snot\sfind\s'(?<f>[^']+)'
        |Cannot\sfind\smodule\s'(?<f>[^']+)'
        |can't\sopen\sfile\s'(?<f>[^']+)'
        |Cannot\sfind\spath\s'(?<f>[^']+)'\sbecause\sit\sdoes\snot\sexist
        |ENOENT:\sno\ssuch\sfile\sor\sdirectory,\s\w+\s'(?<f>[^']+)'
        |File\snot\sfound:\s(?<f>\S+)
        |(?<f>[^\s:'""]+):\sNo\ssuch\sfile\sor\sdirectory")]
    private static partial Regex MissingFile();

    [GeneratedRegex(@"(?i)command exceeded the \d+(?:\.\d+)?s timeout and was killed|Operation has timed out")]
    private static partial Regex TimedOutOutput();

    /// <summary>What the command alone gives away, before any round: a check that can never exit.</summary>
    public static CheckDefect? FromCommand(string verify, string? root) =>
        PlanReview.NeverFinishes(verify, root) is { } what
            ? new CheckDefect(CheckDefectKind.NeverFinishes, $"`{verify}` runs {what}, which does not exit on its own.")
            : null;

    /// <summary>What the output of a check that failed gives away about the check itself, or null when it reads as a verdict.</summary>
    public static CheckDefect? FromOutput(string verify, string output, string? root, IReadOnlyList<PlanStep> steps)
    {
        if (ShellSyntax().Match(output) is { Success: true } syntax)
        {
            return new CheckDefect(CheckDefectKind.ShellSyntax, LineAround(output, syntax.Index));
        }

        IReadOnlySet<string> programs = CheckGuard.Segments(verify).Select(words => words[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MissingProgram().Matches(output))
        {
            string name = Path.GetFileNameWithoutExtension(match.Groups["p"].Value.Trim().Replace('\\', '/'));
            // A missing runtime (node, dotnet, python) is a machine problem with its own handling: another worker.
            if (name.Length > 0 && programs.Contains(name) && !PlanRunner.IsRuntimeProgram(name))
            {
                return new CheckDefect(CheckDefectKind.ProgramMissing, LineAround(output, match.Index));
            }
        }

        string[] arguments = PlanReview.FilesIn(verify).ToArray();
        if (root is not null && arguments.Length > 0)
        {
            foreach (Match match in MissingFile().Matches(output))
            {
                string reported = Path.GetFileName(match.Groups["f"].Value.Trim().Replace('\\', '/'));
                string? argument = arguments.FirstOrDefault(candidate =>
                    string.Equals(Path.GetFileName(candidate.Replace('\\', '/')), reported, StringComparison.OrdinalIgnoreCase));
                if (reported.Length == 0 || argument is null)
                {
                    continue;
                }

                string full = PlanReview.Resolve(root, argument);
                if (!File.Exists(full) && !Directory.Exists(full) && !NamedBySomeStep(steps, root, full))
                {
                    return new CheckDefect(CheckDefectKind.FileMissing, $"{argument} does not exist and no step creates it. {LineAround(output, match.Index)}");
                }
            }
        }

        return null;
    }

    public static bool IsTimeout(string output) => TimedOutOutput().IsMatch(output);

    /// <summary>The same output, but for the numbers in it (a duration, a pid, a port) and spacing.</summary>
    public static bool SameOutput(string first, string second) => Fingerprint(first) == Fingerprint(second);

    /// <summary>A check that was stopped at its time limit twice, and said the same both times, is not getting anywhere.</summary>
    public static CheckDefect? TimedOutTwice(string output, IEnumerable<string> earlierOutputs)
    {
        if (!IsTimeout(output))
        {
            return null;
        }

        return earlierOutputs.Any(earlier => IsTimeout(earlier) && SameOutput(earlier, output))
            ? new CheckDefect(CheckDefectKind.TimedOut, Clip(output, 300))
            : null;
    }

    /// <summary>
    /// Whether a model's evidence is really in the check's output: at least one line of it, as written, at least 12
    /// characters long. A model's report that the check cannot pass counts for nothing unless the output says so too.
    /// </summary>
    public static bool EvidenceMatches(string? evidence, string output)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        string haystack = Collapse(output);
        return evidence.Split('\n').Select(Collapse).Any(line => line.Length >= 12 && haystack.Contains(line, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The shell a check runs in, in words for the auditor: where the machine is Windows, a check with && or || goes through cmd and the rest through PowerShell.</summary>
    public static string ShellDescription(string? workerPlatform, string verify)
    {
        bool chained = verify.Contains("&&", StringComparison.Ordinal) || verify.Contains("||", StringComparison.Ordinal);
        return workerPlatform switch
        {
            "windows" when chained => "Windows worker over SSH, run through cmd.exe /d /s /c (because the check has && or ||)",
            "windows" => "Windows worker over SSH, run in Windows PowerShell 5.1 (no && or ||; use ; and $LASTEXITCODE)",
            "linux" => "Linux worker over SSH, run in a POSIX shell",
            _ when OperatingSystem.IsWindows() => "the hub, a Windows PC, run through cmd.exe /c",
            _ => "the hub, run through /bin/sh -c"
        };
    }

    private static bool NamedBySomeStep(IReadOnlyList<PlanStep> steps, string root, string full) =>
        steps.Any(step => step.Files.Any(file => !string.IsNullOrWhiteSpace(file) &&
            string.Equals(PlanReview.Resolve(root, file.Trim()), full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));

    private static string Fingerprint(string text)
    {
        string head = text.Length <= 400 ? text : text[..400];
        return Regex.Replace(Regex.Replace(head, @"\d+(?:\.\d+)?", "#"), @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static string Collapse(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private static string LineAround(string output, int index)
    {
        int start = output.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        int end = output.IndexOf('\n', index);
        return Clip((end < 0 ? output[start..] : output[start..end]).Trim(), 300);
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
