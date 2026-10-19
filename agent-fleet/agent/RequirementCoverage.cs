using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>A test file as the runner read it: its path inside the project and its text.</summary>
internal sealed record TestFileText(string Path, string Text);

/// <summary>
/// Decides which requirements a tests step covers, from the names of its tests. A test covers a requirement when its
/// name carries the requirement's id as a whole token (R3 in "R3_R4_rejects_zero", not in "R30"). Names are read from
/// the code, not from comments, so a commented-out test does not count. A file whose names cannot be read is searched as
/// plain text instead, because a check that cannot tell names apart should not refuse a step that is right.
/// </summary>
internal static partial class RequirementCoverage
{
    private const string TextMatchNote = " (text match)";

    private static readonly HashSet<string> SlashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".mts", ".cts", ".cs", ".go"
    };

    internal static IReadOnlyList<string>? TestNames(string path, string text)
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        Regex[]? patterns = extension switch
        {
            ".cs" => [DisplayNameName(), CSharpMethodName()],
            ".py" => [PythonTestName()],
            ".go" => [GoTestName()],
            _ when SlashCommentExtensions.Contains(extension) => [JavaScriptTestName()],
            _ => null
        };
        if (patterns is null)
        {
            return null;
        }

        string code = StripComments(text, extension);
        var names = new List<string>();
        foreach (Regex pattern in patterns)
        {
            foreach (Match match in pattern.Matches(code))
            {
                string name = match.Groups["name"].Value;
                if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names.Count == 0 ? null : names;
    }

    // Comments are removed (line breaks kept), and string literals are copied whole, so a comment marker inside a
    // string such as a URL does not hide the rest of its line.
    internal static string StripComments(string text, string extension)
    {
        string lowered = extension.ToLowerInvariant();
        bool hash = lowered == ".py";
        if (!hash && !SlashCommentExtensions.Contains(lowered))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            char current = text[index];
            if (current is '"' or '\'' or '`')
            {
                int end = index + 1;
                while (end < text.Length && text[end] != current && text[end] != '\n')
                {
                    end += text[end] == '\\' && current != '`' ? 2 : 1;
                }

                end = Math.Min(end, text.Length);
                if (end < text.Length && text[end] == current)
                {
                    end++;
                }

                result.Append(text, index, end - index);
                index = end;
                continue;
            }

            if (hash ? current == '#' : current == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (!hash && current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                int close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                int stop = close < 0 ? text.Length : close + 2;
                foreach (char skipped in text.AsSpan(index, stop - index))
                {
                    if (skipped == '\n')
                    {
                        result.Append('\n');
                    }
                }

                index = stop;
                continue;
            }

            result.Append(current);
            index++;
        }

        return result.ToString();
    }

    // The id must not run into a letter or digit on either side: R3 is not in R30 or xR3, but is in R3_R4_rejects. Case counts.
    internal static bool ContainsId(string text, string id) =>
        Regex.IsMatch(text, "(?<![A-Za-z0-9])" + Regex.Escape(id) + "(?![A-Za-z0-9])");

    /// <summary>The requirements with the names that cover each one (empty when none does), in the order given.</summary>
    internal static IReadOnlyList<PlanRequirement> Apply(IReadOnlyList<PlanRequirement> requirements, IReadOnlyList<TestFileText> files)
    {
        var parsed = files.Select(file => (file.Path, file.Text, Names: TestNames(file.Path, file.Text))).ToArray();
        return requirements.Select(requirement =>
        {
            var covered = new List<string>();
            foreach (var (path, text, names) in parsed)
            {
                if (names is null)
                {
                    if (ContainsId(text, requirement.Id) && !covered.Contains(path + TextMatchNote, StringComparer.Ordinal))
                    {
                        covered.Add(path + TextMatchNote);
                    }

                    continue;
                }

                foreach (string name in names.Where(name => ContainsId(name, requirement.Id)))
                {
                    if (!covered.Contains(name, StringComparer.Ordinal))
                    {
                        covered.Add(name);
                    }
                }
            }

            return requirement with { CoveredBy = covered };
        }).ToArray();
    }

    internal static string UncoveredFeedback(IReadOnlyList<PlanRequirement> uncovered)
    {
        if (uncovered.Count == 0)
        {
            return string.Empty;
        }

        string items = string.Join("; ", uncovered.Select(requirement => $"{requirement.Id} ({requirement.Text})"));
        return $"These requirements have no test named after them: {items}. Write a test for each one whose name starts with its id, " +
               "for example \"R2: rejects negatives\" in JavaScript, or a C# method named R2_rejects_negatives.";
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:test|it|describe)(?:\.(?:only|skip|todo))?\s*\(\s*(?<quote>[""'`])(?<name>(?:(?!\k<quote>)[^\r\n\\]|\\.)*)\k<quote>")]
    private static partial Regex JavaScriptTestName();

    [GeneratedRegex(@"DisplayName\s*=\s*""(?<name>[^""\r\n]*)""")]
    private static partial Regex DisplayNameName();

    [GeneratedRegex(@"\b(?:void|Task)\s+(?<name>\w+)\s*\(")]
    private static partial Regex CSharpMethodName();

    [GeneratedRegex(@"\bdef\s+(?<name>test_\w*)\s*\(")]
    private static partial Regex PythonTestName();

    [GeneratedRegex(@"\bfunc\s+(?<name>Test\w*)\s*\(")]
    private static partial Regex GoTestName();
}
