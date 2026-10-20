using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <summary>A test file as the runner read it: its path inside the project and its text.</summary>
internal sealed record TestFileText(string Path, string Text);

/// <summary>A test declaration the reader found, and whether the file marks it as skipped (a skipped test runs nothing, so it covers nothing).</summary>
internal sealed record TestDeclaration(string Name, bool Skipped);

/// <summary>
/// Decides which requirements a tests step covers, from the names of its tests. A test covers a requirement when its
/// name carries the requirement's id as a whole token (R3 in "R3_R4_rejects_zero", not in "R30"). Names are read from
/// the code, not from comments, so a commented-out test does not count. A skipped test is read but does not count. A file
/// with no test declaration at all is searched as plain text instead, because a check that cannot tell names apart should
/// not refuse a step that is right.
/// </summary>
internal static partial class RequirementCoverage
{
    private const string TextMatchNote = " (text match)";

    // The longest requirement text the uncovered feedback shows. The retry prompt lists every requirement in full.
    private const int FeedbackTextCharacters = 60;

    private static readonly HashSet<string> SlashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".mts", ".cts", ".cs", ".go"
    };

    // The names of the tests that run in the file, or null when the file declares no test at all (then its text is searched).
    internal static IReadOnlyList<string>? TestNames(string path, string text)
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        List<TestDeclaration>? declared = extension switch
        {
            ".cs" => CSharpDeclarations(StripComments(text, extension)),
            ".py" => PythonDeclarations(StripComments(text, extension)),
            ".go" => GoDeclarations(StripComments(text, extension)),
            _ when SlashCommentExtensions.Contains(extension) => JavaScriptDeclarations(StripComments(text, extension)),
            _ => null
        };
        if (declared is null || declared.Count == 0)
        {
            return null;
        }

        return declared.Where(declaration => !declaration.Skipped).Select(declaration => declaration.Name).Distinct(StringComparer.Ordinal).ToArray();
    }

    // Comments are removed (line breaks kept), and string literals are copied whole, so a comment marker inside a
    // string such as a URL does not hide the rest of its line. In Python a triple-quoted string (a docstring) is dropped
    // whole: a docstring that mentions a test is not a test.
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
            if (hash && (current is '"' or '\'') && index + 2 < text.Length && text[index + 1] == current && text[index + 2] == current)
            {
                string delimiter = new(current, 3);
                int close = text.IndexOf(delimiter, index + 3, StringComparison.Ordinal);
                int stop = close < 0 ? text.Length : close + 3;
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

            // A slash that starts a regex literal can hold a quote or a comment marker, so the literal is copied whole, as a string is.
            if (!hash && current == '/' && !(index + 1 < text.Length && text[index + 1] is '/' or '*') && StartsRegexLiteral(result))
            {
                int end = RegexLiteralEnd(text, index);
                result.Append(text, index, end - index);
                index = end;
                continue;
            }

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

    // A slash starts a regex literal where an operand may begin: at the start, after an operator or an opening bracket, or after
    // the keyword return. After a name, a number or a closing bracket it is a division.
    private static bool StartsRegexLiteral(StringBuilder result)
    {
        int last = result.Length - 1;
        while (last >= 0 && char.IsWhiteSpace(result[last]))
        {
            last--;
        }

        if (last < 0 || "(,=:[!&|?{};+-*%<>~^".Contains(result[last]))
        {
            return true;
        }

        return last >= 5 && result.ToString(last - 5, 6) == "return";
    }

    // The end of a regex literal, flags included. A slash inside a character class does not end it, and a line break ends it too.
    private static int RegexLiteralEnd(string text, int start)
    {
        bool inClass = false;
        int index = start + 1;
        while (index < text.Length && text[index] != '\n')
        {
            char current = text[index];
            if (current == '\\')
            {
                index += 2;
                continue;
            }

            if (current == '[')
            {
                inClass = true;
            }
            else if (current == ']')
            {
                inClass = false;
            }
            else if (current == '/' && !inClass)
            {
                index++;
                while (index < text.Length && char.IsLetter(text[index]))
                {
                    index++;
                }

                return index;
            }

            index++;
        }

        return Math.Min(index, text.Length);
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

                foreach (string name in names.Where(name => ContainsId(IdSearchText(path, name), requirement.Id)))
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

    // A Go test is named TestR1_x, and the "Test" prefix is a letter in front of the id, so the id is searched after it.
    private static string IdSearchText(string path, string name) =>
        System.IO.Path.GetExtension(path).Equals(".go", StringComparison.OrdinalIgnoreCase) && name.StartsWith("Test", StringComparison.Ordinal)
            ? name["Test".Length..]
            : name;

    internal static string UncoveredFeedback(IReadOnlyList<PlanRequirement> uncovered)
    {
        if (uncovered.Count == 0)
        {
            return string.Empty;
        }

        string items = string.Join("; ", uncovered.Select(requirement => $"{requirement.Id} ({Shorten(requirement.Text)})"));
        return $"These requirements have no test named after them: {items}. Write a test for each one whose name starts with its id, " +
               "for example \"R2: rejects negatives\" in JavaScript, or a C# method named R2_rejects_negatives.";
    }

    private static string Shorten(string text) => text.Length <= FeedbackTextCharacters ? text : text[..(FeedbackTextCharacters - 3)] + "...";

    // Jest, Vitest, Mocha and node:test: test, it and describe, with .only, .each and the rest. A .skip or .todo chain is read and
    // marked skipped. The table of an .each call is skipped over, so the name after it is read.
    private static List<TestDeclaration> JavaScriptDeclarations(string code) =>
        JavaScriptTestName().Matches(code)
            .Where(match => match.Groups["name"].Length > 0)
            .Select(match => new TestDeclaration(match.Groups["name"].Value,
                match.Groups["chain"].Value.Split('.').Any(part => part is "skip" or "todo")))
            .ToList();

    // An attribute that marks a test (xUnit, NUnit, MSTest) puts the method's name in the names; a skip argument or an Ignore
    // attribute marks it skipped. A display name counts as a name of its test. A file with no such attribute at all falls back
    // to every void or Task method, as before.
    private static List<TestDeclaration> CSharpDeclarations(string code)
    {
        var declared = new List<TestDeclaration>();
        if (!CSharpTestAttribute().IsMatch(code))
        {
            declared.AddRange(CSharpMethodName().Matches(code).Select(match => new TestDeclaration(match.Groups["name"].Value, false)));
            declared.AddRange(DisplayNameName().Matches(code).Select(match => new TestDeclaration(match.Groups["name"].Value, false)));
            return declared.Where(declaration => declaration.Name.Length > 0).ToList();
        }

        foreach (Match match in CSharpTestMethod().Matches(code))
        {
            string args = match.Groups["args"].Value;
            bool skipped = match.Groups["ignore"].Length > 0 || match.Groups["between"].Value.Contains("[Ignore", StringComparison.Ordinal) ||
                           SkipArgument().IsMatch(args);
            declared.Add(new TestDeclaration(match.Groups["name"].Value, skipped));
            foreach (Match display in DisplayNameName().Matches(args))
            {
                declared.Add(new TestDeclaration(display.Groups["name"].Value, skipped));
            }
        }

        return declared.Where(declaration => declaration.Name.Length > 0).ToList();
    }

    // A Python test is a def test_ function, not one inside a docstring. A test with a skip decorator is read but marked skipped;
    // a skipif decorator does not skip it outright, so it still counts.
    private static List<TestDeclaration> PythonDeclarations(string code) =>
        PythonTestName().Matches(code)
            .Select(match => new TestDeclaration(match.Groups["name"].Value, PythonSkipDecorator().IsMatch(match.Groups["decorators"].Value)))
            .ToList();

    private static List<TestDeclaration> GoDeclarations(string code) =>
        GoTestName().Matches(code).Select(match => new TestDeclaration(match.Groups["name"].Value, false)).ToList();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:test|it|describe)(?<chain>(?:\.(?:only|skip|todo|each|concurrent|failing|fixme|serial|parallel)\b)*)\s*(?:\((?:[^()]|\([^()]*\))*\)\s*)?\(\s*(?<quote>[""'`])(?<name>(?:(?!\k<quote>)[^\r\n\\]|\\.)*)\k<quote>")]
    private static partial Regex JavaScriptTestName();

    [GeneratedRegex(@"DisplayName\s*=\s*@?""(?<name>[^""\r\n]*)""")]
    private static partial Regex DisplayNameName();

    [GeneratedRegex(@"\[(?:Fact|Theory|Test|TestMethod|TestCase|DataTestMethod)(?:Attribute)?\b")]
    private static partial Regex CSharpTestAttribute();

    // The attributes in front of a test method, then the method's name. The text between the test attribute and the method
    // may hold other attributes and modifiers, but not a statement, so one test's attribute does not reach the next method.
    [GeneratedRegex(@"(?<ignore>(?:\[Ignore\b[^\]\r\n]*\]\s*)*)\[(?:Fact|Theory|Test|TestMethod|TestCase|DataTestMethod)(?:Attribute)?\b(?<args>[^\]\r\n]*)\](?<between>[^;]*?)\b(?:void|Task)\s+(?<name>\w+)\s*\(")]
    private static partial Regex CSharpTestMethod();

    [GeneratedRegex(@"\bSkip\s*=")]
    private static partial Regex SkipArgument();

    [GeneratedRegex(@"\b(?:void|Task)\s+(?<name>\w+)\s*\(")]
    private static partial Regex CSharpMethodName();

    [GeneratedRegex(@"(?<decorators>(?:^[ \t]*@[^\r\n]*\r?\n)*)^[ \t]*(?:async[ \t]+)?def[ \t]+(?<name>test_\w*)[ \t]*\(", RegexOptions.Multiline)]
    private static partial Regex PythonTestName();

    [GeneratedRegex(@"^[ \t]*@(?:pytest\.mark\.skip|unittest\.skip|skip)\b", RegexOptions.Multiline)]
    private static partial Regex PythonSkipDecorator();

    [GeneratedRegex(@"\bfunc\s+(?<name>Test\w*)\s*\(")]
    private static partial Regex GoTestName();
}
