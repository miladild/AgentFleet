using System.Collections.Concurrent;
using System.Text;

namespace AgentFleet;

/// <summary>
/// Shared by the hub and worker edit tools. When the exact text is not in the file, the lenient rule may apply the edit if
/// exactly one place matches: whole lines are compared with trailing spaces and tabs ignored, and look-alike characters in
/// oldText (no-break space, curly quotes, dashes, ellipsis) are read as their plain forms. Indentation and line breaks are
/// always exact, and nothing is scored: a near miss is never applied. Otherwise the reply says where the closest text is, so
/// the model can copy it instead of guessing again.
/// </summary>
internal static class EditMatching
{
    public const string LenientNote = "matched after ignoring trailing whitespace";
    public const int ClosestLines = 10;
    public const int GuardRegionLines = 40;
    private const int MaxShownLength = 120;

    // The closest region of the latest miss per scope and path, so the loop guard can show more of it on a repeated miss.
    private static readonly ConcurrentDictionary<string, MissRegion> LastMisses = new(StringComparer.Ordinal);

    /// <summary>The closest region of a miss: its first line number and up to <see cref="GuardRegionLines"/> numbered lines.</summary>
    internal sealed record MissRegion(int FirstLine, IReadOnlyList<string> Lines);

    /// <summary>How many places match oldText under the lenient rule, and the character span of the first one.</summary>
    public static (int Count, int Start, int End) FindLenient(string content, string oldText)
    {
        bool endsWithBreak = oldText.EndsWith('\n');
        string[] wanted = LineKeysOf(endsWithBreak ? oldText[..^1] : oldText);
        string[] lines = content.Split('\n');
        string[] keys = lines.Select(LineKey).ToArray();
        var starts = new int[lines.Length];
        for (int i = 1; i < lines.Length; i++)
        {
            starts[i] = starts[i - 1] + lines[i - 1].Length + 1;
        }

        int count = 0, start = 0, end = 0;
        for (int i = 0; i + wanted.Length <= lines.Length; i++)
        {
            // A trailing line break in oldText needs a line break in the file after the last matched line.
            if (endsWithBreak && i + wanted.Length >= lines.Length)
            {
                break;
            }

            if (!MatchesAt(keys, i, wanted))
            {
                continue;
            }

            count++;
            if (count == 1)
            {
                int last = i + wanted.Length - 1;
                start = starts[i];
                end = endsWithBreak
                    ? starts[last] + lines[last].Length + 1
                    : starts[last] + lines[last].TrimEnd('\r').Length;
            }
        }

        return (count, start, end);
    }

    /// <summary>The reply for oldText that matches nothing, exact or lenient. It also remembers the closest region for the guard.</summary>
    public static string NotFoundReply(string shownPath, string recordPath, string content, string oldText)
    {
        string[] lines = content.Split('\n');
        string[] keys = lines.Select(LineKey).ToArray();
        string[] wanted = LineKeysOf(oldText.EndsWith('\n') ? oldText[..^1] : oldText);
        (int start, int score) = Closest(keys, wanted);

        int regionEnd = Math.Min(lines.Length, start + GuardRegionLines);
        var region = new List<string>();
        for (int i = start; i < regionEnd; i++)
        {
            region.Add(Numbered(lines, keys, wanted, start, i));
        }

        Remember(recordPath, new MissRegion(start + 1, region));

        if (score == 0 || score * 2 < wanted.Length)
        {
            return $"Error: oldText was not found in {shownPath}. No part of the file looks like it. " +
                "Use read_file with startLine and endLine to find the text, then copy it exactly.";
        }

        int shown = Math.Min(ClosestLines, region.Count);
        var reply = new StringBuilder($"Error: oldText was not found in {shownPath}. The closest text starts at line {start + 1}:");
        for (int i = 0; i < shown; i++)
        {
            reply.Append('\n').Append(region[i]);
        }

        reply.Append("\nCopy the exact lines from here, or read_file the region.");

        // A literal backslash sequence in oldText that the closest text does not have: the model wrote an escape where the file has a line break.
        string shownText = string.Join("\n", lines[start..(start + shown)]);
        if (LacksSequence(oldText, shownText, @"\n") || LacksSequence(oldText, shownText, @"\/"))
        {
            reply.Append(" Your text contains a literal backslash sequence; send real line breaks and characters instead of escape sequences.");
        }

        return reply.ToString();
    }

    /// <summary>The reply when the lenient rule finds more than one place: the edit is not applied.</summary>
    public static string AmbiguousReply(string path, int count) =>
        $"Error: oldText matches {count} places in {path} once trailing whitespace is ignored. Include more surrounding lines so it is unique.";

    /// <summary>The closest region of the latest not-found reply for this path in this scope, or null when there is none.</summary>
    internal static MissRegion? LastMissRegion(string path) =>
        LastMisses.TryGetValue(MissKey(path), out MissRegion? region) ? region : null;

    // Worker edits and hub edits run in different scopes, so a miss in one never lends its region to the other.
    private static string MissKey(string path) =>
        (WorkerWorkspaceContext.Current is { } worker ? "worker " + worker.Machine : "hub") + "\n" + path;

    private static void Remember(string path, MissRegion region)
    {
        if (LastMisses.Count >= 256)
        {
            LastMisses.Clear();
        }

        LastMisses[MissKey(path)] = region;
    }

    private static bool LacksSequence(string oldText, string shownText, string sequence) =>
        oldText.Contains(sequence, StringComparison.Ordinal) && !shownText.Contains(sequence, StringComparison.Ordinal);

    // Trailing spaces and tabs do not count; leading indentation and the line itself do.
    private static string LineKey(string line) => line.TrimEnd('\r').TrimEnd(' ', '\t');

    private static string[] LineKeysOf(string text) => Lookalikes(text).Split('\n').Select(LineKey).ToArray();

    // Only oldText is normalised: the file's own characters are compared as they are.
    private static string Lookalikes(string text) => text
        .Replace('\u00A0', ' ')
        .Replace('\u201C', '"')
        .Replace('\u201D', '"')
        .Replace('\u2018', '\'')
        .Replace('\u2019', '\'')
        .Replace('\u2013', '-')
        .Replace('\u2014', '-')
        .Replace("\u2026", "...");

    private static bool MatchesAt(string[] keys, int start, string[] wanted)
    {
        for (int j = 0; j < wanted.Length; j++)
        {
            if (keys[start + j] != wanted[j])
            {
                return false;
            }
        }

        return true;
    }

    // The window that starts at the line with the most matching lines; the first one wins a tie.
    private static (int Start, int Score) Closest(string[] keys, string[] wanted)
    {
        int bestStart = 0, bestScore = -1;
        for (int i = 0; i < keys.Length; i++)
        {
            int score = 0;
            for (int j = 0; j < wanted.Length && i + j < keys.Length; j++)
            {
                if (keys[i + j] == wanted[j])
                {
                    score++;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestStart = i;
            }
        }

        return (bestStart, Math.Max(bestScore, 0));
    }

    private static string Numbered(string[] lines, string[] keys, string[] wanted, int start, int index)
    {
        string text = lines[index].TrimEnd('\r');
        if (text.Length > MaxShownLength)
        {
            text = text[..MaxShownLength] + "...";
        }

        int position = index - start;
        string mark = position < wanted.Length && keys[index] != wanted[position]
            ? $" (differs from your line {position + 1})"
            : string.Empty;
        return $"{index + 1}: {text}{mark}";
    }
}
