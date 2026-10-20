namespace AgentFleet;

/// <summary>The lines a correction removed from and added to an old file, in order.</summary>
/// <param name="OldLines">How many lines the old file has.</param>
/// <param name="TooLarge">True when a file has more lines than the comparison takes; then Removed and Added are empty.</param>
internal sealed record LineChange(IReadOnlyList<string> Removed, IReadOnlyList<string> Added, int OldLines, bool TooLarge);

/// <summary>
/// A plain longest-common-subsequence line diff, used to show a ruling's correction and to refuse one that rewrites too much
/// of a test file. Line endings are ignored. Test files are small, so the comparison is kept simple and bounded in size.
/// </summary>
internal static class LineDiff
{
    public const int MaxLines = 4000;

    public static LineChange Compute(string oldText, string newText)
    {
        string[] a = Lines(oldText);
        string[] b = Lines(newText);
        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            return new LineChange([], [], a.Length, TooLarge: true);
        }

        // The common start and end need no comparison table; only the middle that differs does.
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length;
        int endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }

        int n = endA - start;
        int m = endB - start;
        // lcs[i, j] is the longest common subsequence of the middle parts from i on.
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[start + i] == b[start + j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var removed = new List<string>();
        var added = new List<string>();
        int x = 0;
        int y = 0;
        while (x < n && y < m)
        {
            if (a[start + x] == b[start + y])
            {
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                removed.Add(a[start + x++]);
            }
            else
            {
                added.Add(b[start + y++]);
            }
        }

        while (x < n) removed.Add(a[start + x++]);
        while (y < m) added.Add(b[start + y++]);
        return new LineChange(removed, added, a.Length, TooLarge: false);
    }

    private static string[] Lines(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Length == 0) return [];
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return normalized.Split('\n');
    }
}
