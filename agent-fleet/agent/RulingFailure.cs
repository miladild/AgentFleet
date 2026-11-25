using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>
/// The failure a ruling was made for, as one short line: the first error a failing check printed (its class and message) with
/// the numbers blanked, so a changed count or timing is the same failure and a different error is not. A ruling note describes
/// that failure. Measured: a note that said the code must change went on leading every message for 13 rounds after the check had
/// moved from a failed comparison to a TypeError at an earlier line of the test, so it described something no longer there.
/// </summary>
internal static partial class RulingFailure
{
    /// <summary>The most characters of a failure line.</summary>
    internal const int MaxCharacters = 160;

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    // "AssertionError [ERR_ASSERTION]: message", "TypeError: message", "Error: message", "System.InvalidOperationException: message".
    // A test's own name ("✖ rejects TypeError: bad input") does not start the line, so it is not read as an error.
    [GeneratedRegex(@"^\s*(?<name>(?:[A-Za-z_$][\w$.]*)?(?:Error|Exception))(?<code>\s*\[[\w-]+\])?:\s*(?<message>.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorStart();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// The first error line of a failing check, with its numbers blanked and its length capped, or null when the output prints none
    /// (a timeout, a build message, another runner's wording). A comparison header that ends in a colon takes the line after it
    /// (the values compared), because the header alone is the same for every failed comparison.
    /// </summary>
    internal static string? Line(string? checkOutput)
    {
        if (string.IsNullOrWhiteSpace(checkOutput))
        {
            return null;
        }

        string[] lines = AnsiEscape().Replace(checkOutput, string.Empty).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            Match start = ErrorStart().Match(lines[index].TrimEnd('\r'));
            if (!start.Success)
            {
                continue;
            }

            string message = start.Groups["message"].Value;
            if (message.Length == 0 || message.EndsWith(':'))
            {
                for (int next = index + 1; next < lines.Length; next++)
                {
                    string candidate = lines[next].Trim();
                    if (candidate.Length == 0)
                    {
                        continue;
                    }

                    message = $"{message} {candidate}".Trim();
                    break;
                }
            }

            string line = Whitespace().Replace($"{start.Groups["name"].Value}{start.Groups["code"].Value}: {message}", " ");
            line = Number().Replace(line, "#");
            return line.Length > MaxCharacters ? line[..MaxCharacters] : line;
        }

        return null;
    }

    /// <summary>
    /// Whether a ruling made for the stored failure line still describes the check's failure. It does when no line was stored (a
    /// ruling written before lines were kept), when the check prints no readable error line, and when the line is the same.
    /// </summary>
    internal static bool StillApplies(string? storedLine, string? checkOutput)
    {
        if (string.IsNullOrEmpty(storedLine))
        {
            return true;
        }

        return Line(checkOutput) is not { } now || string.Equals(now, storedLine, StringComparison.Ordinal);
    }
}
