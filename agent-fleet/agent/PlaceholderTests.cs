using System.Text.RegularExpressions;

namespace AgentFleet;

// Finds the test files that cannot check anything. Their tests assert a fixed failure, so they fail before any code exists and the
// fail-first check would accept them while proving nothing about the step.
internal static class PlaceholderTests
{
    // Statements that fail whatever the code does. assert.fail, Assert.Fail, self.fail and pytest.fail are left out on purpose: they
    // are used inside try or catch blocks to say that a line must not be reached.
    private static readonly Regex Statement = new(
        @"(?:assert\.ok\(false|assert\(false|assert\.equal\(true, false|assert\.strictEqual\(true, false|expect\(true\)\.toBe\(false|" +
        @"expect\(false\)\.toBe\(true|throw new Error\([""'`](?i:not implemented|todo)|\bassert False\b|self\.assertTrue\(False|" +
        @"self\.assertFalse\(True|Assert\.True\(false|Assert\.False\(true)\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex TestOpener = new(
        @"^(?:test|it|specify)(?:\.only|\.skip)?\(|^def test_|^\[(?:Fact|Theory|Test|TestCase)(?:\(|\])",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> Find(string relativePath, string text)
    {
        var placeholders = new List<string>();
        int tests = 0;
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            // A commented-out statement or test is not part of what the file checks.
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#')) continue;
            if (TestOpener.IsMatch(line)) tests++;
            if (Statement.IsMatch(line))
            {
                string shown = line.Length <= 120 ? line : line[..120];
                placeholders.Add($"{relativePath} line {index + 1}: {shown}");
            }
        }

        // A file is a placeholder file only when most of its tests are placeholders. One stub in a branch among real tests is not a
        // file that checks nothing, and a file with no tests at all is not judged here.
        if (tests > 0 && placeholders.Count * 2 >= tests) return placeholders;
        return [];
    }
}
