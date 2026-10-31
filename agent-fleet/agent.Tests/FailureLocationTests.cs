namespace AgentFleet.Tests;

public sealed class FailureLocationTests : IDisposable
{
    private const char Backslash = (char)92;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-location-" + Guid.NewGuid().ToString("N"));

    public FailureLocationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "test"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "dep"));
        File.WriteAllLines(Path.Combine(_root, "src", "semver.js"),
            ["'use strict';", "function validate(v) {", "  const prerelease = v.split('-')[1];", "  const ids = prerelease.split('.');", "}"]);
        File.WriteAllLines(Path.Combine(_root, "test", "lru.test.js"),
            ["const test = require('node:test');", "", "test('a', () => {", "  assert.throws(() => new LruCache({ maxSize: 1 }), RangeError);", "});"]);
        File.WriteAllLines(Path.Combine(_root, "node_modules", "dep", "index.js"), ["module.exports = 1;"]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_node_stack_frame_from_another_machine_is_found_by_the_tail_of_its_path()
    {
        string output = "TypeError: Cannot read properties of undefined (reading 'split')\n" +
                        "    at validate (C:\\FleetData\\Workspaces\\agentfleet\\plans\\abc\\worker-a\\project\\src\\semver.js:4:28)\n" +
                        "    at Test.runInAsyncScope (node:async_hooks:227:14)";

        string? where = FailureLocation.Describe(output, _root);

        Assert.NotNull(where);
        Assert.Contains("- src/semver.js line 4: const ids = prerelease.split('.');", where);
        Assert.DoesNotContain("async_hooks", where);
    }

    [Fact]
    public void The_assertion_a_test_failed_on_is_shown_with_its_test_and_the_frames_after_it()
    {
        string output = "AssertionError: Missing expected exception (RangeError).\n" +
                        "      at TestContext.<anonymous> (C:\\x\\project\\test\\lru.test.js:4:14)\n" +
                        "      at validate (/home/runner/work/project/src/semver.js:3:20)\n" +
                        "      at validate (/home/runner/work/project/src/semver.js:4:28)";

        string? where = FailureLocation.Describe(output, _root);

        Assert.Equal(
            "Where it failed (each failing test as written, or the source line a stack frame names):\n" +
            "- test/lru.test.js lines 3-4 (the failing test as written):\n" +
            "  3: test('a', () => {\n" +
            "  4:   assert.throws(() => new LruCache({ maxSize: 1 }), RangeError);\n" +
            "- src/semver.js line 3: const prerelease = v.split('-')[1];\n" +
            "- src/semver.js line 4: const ids = prerelease.split('.');\n" +
            "The inputs and comments in these tests are the rules the code has to meet.",
            where?.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("  File \"/work/project/src/semver.js\", line 4, in validate")]
    [InlineData("   at Semver.Validate(String v) in C:\\build\\project\\src\\semver.js:line 4")]
    [InlineData("src/semver.js:4: TypeError")]
    public void Python_dotnet_and_short_forms_are_read_too(string frame)
    {
        Assert.Contains("src/semver.js line 4", FailureLocation.Describe(frame, _root));
    }

    [Theory]
    [InlineData("at x (C:\\w\\project\\node_modules\\dep\\index.js:1:1)")] // a dependency is not the project
    [InlineData("at x (node:internal/test_runner/test:1306:25)")]
    [InlineData("at x (C:\\w\\project\\src\\missing.js:3:1)")] // not a file of the project
    [InlineData("at x (C:\\w\\project\\src\\semver.js:99:1)")] // past the end of the file
    [InlineData("at x (..\\..\\outside.js:3:1)")] // never outside the project
    [InlineData("ok in 12:34:56 on localhost:3000")]
    [InlineData("")]
    public void Nothing_is_said_when_the_output_names_no_line_of_the_project(string output)
    {
        Assert.Null(FailureLocation.Describe(output, _root));
    }

    [Fact]
    public void No_project_folder_means_nothing_is_said()
    {
        Assert.Null(FailureLocation.Describe("at x (src/semver.js:4:1)", null));
        Assert.Null(FailureLocation.Describe("at x (src/semver.js:4:1)", Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void A_long_source_line_is_cut()
    {
        File.WriteAllText(Path.Combine(_root, "src", "wide.js"), new string('x', 500));

        string? where = FailureLocation.Describe("at f (src/wide.js:1:1)", _root);

        Assert.EndsWith(" ...", where);
        Assert.Contains(new string('x', 200) + " ...", where);
        Assert.DoesNotContain(new string('x', 201), where);
    }

    [Fact]
    public void The_follow_up_and_the_retry_prompt_both_say_where_the_output_points()
    {
        var step = new PlanStep(1, "fix it", "detail", ["src/semver.js"], "node --test", "standard", StepStatus.Running, null, 1, null, null);
        var plan = new PlanRecord("0123456789abcdef0123456789abcdef", "t", "Fix the widget", PlanStatus.Running, null, [], [], [], null, null,
            [step], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) with { WorkingDirectory = _root };
        string failure = "TypeError: Cannot read properties of undefined\n    at validate (C:\\x\\project\\src\\semver.js:4:28)";
        const string line = "- src/semver.js line 4: const ids = prerelease.split('.');";

        string followUp = RepairMessages.Build(plan, step, failure, 2, [], [], sameOutput: false, changedFiles: null);
        string retry = PlanPromptComposition.BuildPrompt(plan, step, 2, failure);

        foreach (string message in new[] { followUp, retry })
        {
            Assert.Contains(line, message);
            Assert.True(message.IndexOf(line, StringComparison.Ordinal) > message.IndexOf("semver.js:4:28", StringComparison.Ordinal), "after the failure it is about");
        }

        string without = RepairMessages.Build(plan with { WorkingDirectory = null }, step, failure, 2, [], [], sameOutput: false, changedFiles: null);
        Assert.DoesNotContain("Where it failed", without);
    }

    [Theory]
    [InlineData(@"at validate (C:\FleetData\Workspaces\agentfleet\plans\0123456789abcdef0123456789abcdef\worker-a\project\src\semver.js:35:28)", @"at validate (src\semver.js:35:28)")]
    [InlineData("at f (/home/runner/Workspaces/plans/0123456789abcdef0123456789abcdef/worker-b/project/test/a.test.js:4:1)", "at f (test/a.test.js:4:1)")]
    [InlineData("nothing to cut: src/a.js:1:1 and plans/short/project/x", "nothing to cut: src/a.js:1:1 and plans/short/project/x")]
    public void A_workers_project_folder_is_taken_off_the_paths_of_its_output(string output, string expected)
    {
        Assert.Equal(expected, FailureLocation.ProjectRelative(output, null));
    }

    [Fact]
    public void The_hubs_own_project_folder_is_taken_off_in_either_slash_style_and_any_case()
    {
        string root = Path.Combine(_root, "proj");
        string back = root.Replace('/', Backslash);
        string forward = root.Replace(Backslash, '/');

        string cut = FailureLocation.ProjectRelative($"at a ({back}{Backslash}src{Backslash}a.js:1:1)\nat b ({forward.ToUpperInvariant()}/src/b.js:2:2)\nat c ({forward}/src/c.js:3:3)", root);

        Assert.Equal($"at a (src{Backslash}a.js:1:1)\nat b (src/b.js:2:2)\nat c (src/c.js:3:3)", cut);
        Assert.Equal("same", FailureLocation.ProjectRelative("same", root));
        Assert.Equal(string.Empty, FailureLocation.ProjectRelative(string.Empty, root));
    }

    private const string Heading = "Where it failed (each failing test as written, or the source line a stack frame names):";

    private static int LineOf(string[] lines, string fragment) =>
        Array.FindIndex(lines, line => line.Contains(fragment, StringComparison.Ordinal)) + 1;

    private static int Occurrences(string text, string fragment) => text.Split(fragment).Length - 1;

    private void WriteProjectFile(string relative, string[] lines)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    [Fact]
    public void Every_failing_test_is_shown_as_written_with_its_input_and_its_comment()
    {
        string[] file =
        [
            "const { test } = require('node:test');",
            "const assert = require('node:assert');",
            "",
            "test('adds a tax-free day', () => {",
            "  // January 1 is tax-free whatever the weekday.",
            "  const janFirst = new Date(Date.UTC(2026, 0, 1));",
            "  assert.equal(taxFree(janFirst), true);",
            "});",
            "",
            "test('a weekend is tax-free', () => {",
            "  // Saturdays are tax-free in this made-up rule.",
            "  const saturday = new Date(Date.UTC(2026, 2, 7));",
            "  assert.equal(taxFree(saturday), true);",
            "});",
            "",
            "test('a weekday is not', () => {",
            "  // Wednesdays are never tax-free.",
            "  const wednesday = new Date(Date.UTC(2026, 2, 4));",
            "  assert.equal(taxFree(wednesday), false);",
            "});",
        ];
        WriteProjectFile(Path.Combine("test", "tax.test.js"), file);
        string Failure(string name, string assertion) =>
            $"✖ {name} (1.1ms)\n  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:\n" +
            $"      at TestContext.<anonymous> (test\\tax.test.js:{LineOf(file, assertion)}:3)\n";
        string output = "Exit code: 1\n" + Failure("adds a tax-free day", "assert.equal(taxFree(janFirst)") +
                        Failure("a weekend is tax-free", "assert.equal(taxFree(saturday)") +
                        Failure("a weekday is not", "assert.equal(taxFree(wednesday)") + "ℹ fail 3\n";

        string? where = FailureLocation.Describe(output, _root);

        Assert.Equal(
            Heading + "\n" +
            "- test/tax.test.js lines 4-7 (the failing test as written):\n" +
            "  4: test('adds a tax-free day', () => {\n" +
            "  5:   // January 1 is tax-free whatever the weekday.\n" +
            "  6:   const janFirst = new Date(Date.UTC(2026, 0, 1));\n" +
            "  7:   assert.equal(taxFree(janFirst), true);\n" +
            "- test/tax.test.js lines 10-13 (the failing test as written):\n" +
            "  10: test('a weekend is tax-free', () => {\n" +
            "  11:   // Saturdays are tax-free in this made-up rule.\n" +
            "  12:   const saturday = new Date(Date.UTC(2026, 2, 7));\n" +
            "  13:   assert.equal(taxFree(saturday), true);\n" +
            "- test/tax.test.js lines 16-19 (the failing test as written):\n" +
            "  16: test('a weekday is not', () => {\n" +
            "  17:   // Wednesdays are never tax-free.\n" +
            "  18:   const wednesday = new Date(Date.UTC(2026, 2, 4));\n" +
            "  19:   assert.equal(taxFree(wednesday), false);\n" +
            "The inputs and comments in these tests are the rules the code has to meet.",
            where?.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_frame_in_ordinary_source_keeps_the_single_line_form()
    {
        string? where = FailureLocation.Describe("at validate (C:\\x\\project\\src\\semver.js:4:28)", _root);

        Assert.Equal(
            Heading + "\n- src/semver.js line 4: const ids = prerelease.split('.');",
            where?.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Two_frames_in_one_test_are_shown_as_one_block()
    {
        WriteProjectFile(Path.Combine("test", "sum.test.js"),
            ["test('sums two lists', () => {", "  const left = [1, 2];", "  assert.equal(sum(left), 3);", "  assert.equal(sum(left), 4);", "});"]);
        string output = "AssertionError: wrong sum\n" +
                        "    at TestContext.<anonymous> (test\\sum.test.js:3:3)\n" +
                        "    at TestContext.<anonymous> (test\\sum.test.js:4:3)";

        string? where = FailureLocation.Describe(output, _root)?.Replace("\r\n", "\n");

        Assert.NotNull(where);
        Assert.Equal(1, Occurrences(where, "(the failing test as written)"));
        Assert.Contains("- test/sum.test.js lines 1-4 (the failing test as written):\n  1: test('sums two lists', () => {", where);
    }

    [Fact]
    public void A_python_test_function_and_a_dotnet_fact_are_shown_as_whole_blocks()
    {
        WriteProjectFile(Path.Combine("tests", "test_hours.py"),
            ["import hours", "", "def test_closed_on_holiday():", "    day = date(2026, 6, 19)", "    assert hours.session(day) == 'closed'"]);
        WriteProjectFile(Path.Combine("tests", "HoursTests.cs"),
            ["using Xunit;", "", "public class HoursTests", "{", "    [Fact]", "    public void Closed_on_a_holiday()", "    {",
             "        var when = new DateTime(2026, 6, 19);", "        Assert.Equal(\"closed\", Hours.Session(when));", "    }", "}"]);
        string output = "  File \"tests/test_hours.py\", line 5, in test_closed_on_holiday\n" +
                        "   at HoursTests.Closed_on_a_holiday() in C:\\build\\project\\tests\\HoursTests.cs:line 9";

        string? where = FailureLocation.Describe(output, _root)?.Replace("\r\n", "\n");

        Assert.Contains("- tests/test_hours.py lines 3-5 (the failing test as written):\n  3: def test_closed_on_holiday():\n  4:     day = date(2026, 6, 19)", where);
        Assert.Contains("- tests/HoursTests.cs lines 5-9 (the failing test as written):\n  5:     [Fact]", where);
        Assert.Contains("  9:         Assert.Equal(\"closed\", Hours.Session(when));", where);
    }

    [Fact]
    public void A_failing_line_with_no_test_opener_in_the_sixty_lines_above_it_is_a_single_line()
    {
        string[] file = [.. new[] { "test('far away', () => {" }, .. Enumerable.Range(0, 61).Select(step => $"  step{step}();"), "  const value = compute();"];
        WriteProjectFile(Path.Combine("test", "far.test.js"), file);

        string? where = FailureLocation.Describe($"at x (test\\far.test.js:{file.Length}:3)", _root)?.Replace("\r\n", "\n");

        Assert.Equal(Heading + "\n- test/far.test.js line 63: const value = compute();", where);
    }

    [Fact]
    public void A_failing_line_twenty_lines_below_its_test_opener_shows_the_whole_test_with_its_setup()
    {
        string[] file =
        [
            "test('fires once', async () => {",
            "  const provider = { getQuotes: async () => [quote(201)] };",
            .. Enumerable.Range(0, 17).Select(step => $"  // step {step}"),
            "  await service.pollOnce();",
            "  assert.equal(service.getState().fired.length, 0);",
            "});"
        ];
        WriteProjectFile(Path.Combine("test", "tall.test.js"), file);
        int failing = LineOf(file, "assert.equal(service.getState().fired.length, 0)");

        string? where = FailureLocation.Describe($"at TestContext.<anonymous> (test\\tall.test.js:{failing}:10)", _root)?.Replace("\r\n", "\n");

        Assert.NotNull(where);
        Assert.Contains($"- test/tall.test.js lines 1-{failing} (the failing test as written):\n  1: test('fires once', async () => {{", where);
        Assert.Contains("  2:   const provider = { getQuotes: async () => [quote(201)] };", where);
    }

    [Fact]
    public void A_failing_line_after_a_test_that_already_closed_is_not_shown_as_part_of_that_test()
    {
        string[] file =
        [
            "test('first', () => {",
            "  assert.ok(true);",
            "});",
            "",
            "assert.equal(helperResult(), 1);"
        ];
        WriteProjectFile(Path.Combine("test", "closed.test.js"), file);

        string? where = FailureLocation.Describe("at x (test\\closed.test.js:5:1)", _root)?.Replace("\r\n", "\n");

        Assert.Equal(Heading + "\n- test/closed.test.js line 5: assert.equal(helperResult(), 1);", where);
    }

    [Fact]
    public void The_block_text_stops_at_3000_characters_and_says_how_many_locations_were_left_out()
    {
        string[] file = Enumerable.Range(1, 6).SelectMany(number => new[]
        {
            $"test('long case {number}', () => {{",
            $"  // {new string('w', 185)}",
            $"  // {new string('w', 185)}",
            $"  // {new string('w', 185)}",
            $"  // {new string('w', 185)}",
            $"  // {new string('w', 185)}",
            $"  // {new string('w', 185)}",
            $"  assert.equal(longCase({number}), true);",
            "});",
        }).ToArray();
        WriteProjectFile(Path.Combine("test", "long.test.js"), file);
        string output = string.Concat(Enumerable.Range(1, 6).Select(number =>
            $"      at TestContext.<anonymous> (test\\long.test.js:{LineOf(file, $"assert.equal(longCase({number}),")}:3)\n"));

        string? where = FailureLocation.Describe(output, _root);

        Assert.NotNull(where);
        Assert.True(where.Length <= 3000, $"text is {where.Length} characters");
        int shown = Occurrences(where, "(the failing test as written)");
        Assert.Equal(2, shown);
        Assert.Contains("(4 more failing locations not shown)", where);
    }

    [Fact]
    public void Each_failing_test_of_a_long_node_run_is_shown_with_its_input_after_the_check_summary_shortens_it()
    {
        string[] file = Enumerable.Range(1, 5).SelectMany(number => new[]
        {
            $"test('case {number}', () => {{",
            $"  // Rule {number}: the made-up value must match the table.",
            $"  const input{number} = Date.UTC(2026, 5, {number});",
            $"  assert.equal(lookup(input{number}), 'open');",
            "});",
        }).ToArray();
        WriteProjectFile(Path.Combine("test", "hours.test.ts"), file);
        string Failure(int number) =>
            $"✖ case {number} (0.4ms)\n  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:\n\n  'closed' !== 'open'\n\n" +
            $"      at TestContext.<anonymous> (test\\hours.test.ts:{LineOf(file, $"assert.equal(lookup(input{number}),")}:10)\n" +
            "      at Test.runInAsyncScope (node:async_hooks:227:14)\n" +
            string.Concat(Enumerable.Range(0, 12).Select(frame => $"      at Test.run (node:internal/test_runner/test:{frame}:25)\n"));
        string output = "Exit code: 1\n--- stdout ---\n" + string.Concat(Enumerable.Range(1, 5).Select(Failure)) +
                        "ℹ tests 5\nℹ pass 0\nℹ fail 5\n\n✖ failing tests:\n\n" + string.Concat(Enumerable.Range(1, 5).Select(Failure));
        Assert.True(output.Length > 2500);

        string shown = PlanTools.ShortenCheckOutput(output);
        string? where = FailureLocation.Describe(shown, _root)?.Replace("\r\n", "\n");

        Assert.NotNull(where);
        Assert.Equal(5, Occurrences(where, "(the failing test as written)"));
        for (int number = 1; number <= 5; number++)
        {
            Assert.Contains($"const input{number} = Date.UTC(2026, 5, {number});", where);
        }
    }

    [Fact]
    public void The_summary_keeps_the_first_project_frame_under_each_failure_and_drops_dependency_and_runtime_frames()
    {
        string output = "Exit code: 1\n" +
                        "✖ alpha one (1ms)\n  AssertionError [ERR_ASSERTION]: first\n" +
                        "      at helper (node_modules\\dep\\index.js:10:5)\n" +
                        "      at TestContext.<anonymous> (test\\alpha.test.ts:3:9)\n" +
                        "      at more (test\\alpha.test.ts:7:1)\n" +
                        "      at Test.run (node:internal/test_runner/test:1306:25)\n" +
                        "✖ beta two (1ms)\n  AssertionError [ERR_ASSERTION]: second\n" +
                        "      at TestContext.<anonymous> (test\\beta.test.ts:5:9)\n" +
                        "      at Test.run (node:internal/test_runner/test:1306:25)\n" +
                        "ℹ fail 2\n" +
                        string.Concat(Enumerable.Range(0, 80).Select(line => $"progress line {line} with some padding so the output is long enough to shorten\n"));
        Assert.True(output.Length > 2500);

        string shown = PlanTools.ShortenCheckOutput(output);

        Assert.Contains("at TestContext.<anonymous> (test\\alpha.test.ts:3:9)", shown);
        Assert.DoesNotContain("alpha.test.ts:7:1", shown);
        Assert.Contains("at TestContext.<anonymous> (test\\beta.test.ts:5:9)", shown);
        Assert.DoesNotContain("node_modules", shown);
        Assert.DoesNotContain("node:internal", shown);
    }

    [Fact]
    public void A_node_test_declaration_line_is_not_taken_for_the_place_a_test_failed()
    {
        string[] file =
        [
            "const { test } = require('node:test');",
            "",
            "test('opens late', () => {",
            "  const at = new Date(Date.UTC(2026, 5, 19, 8));",
            "  assert.equal(opensAt(at), 9);",
            "});",
            "",
            "test('closes early', () => {",
            "  const at = new Date(Date.UTC(2026, 5, 19, 20));",
            "  assert.equal(closesAt(at), 17);",
            "});",
        ];
        WriteProjectFile(Path.Combine("test", "hours.test.js"), file);
        // node:test prints a "test at <file>:<line>:<column>" line before each failure: the declaration, not the failing line.
        string Failure(string name, string assertion) =>
            "test at test\\hours.test.js:1:993\n" +
            $"✖ {name} (1.2ms)\n  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:\n" +
            $"      at TestContext.<anonymous> (test\\hours.test.js:{LineOf(file, assertion)}:3)\n";
        string output = "Exit code: 1\n✖ failing tests:\n\n" +
                        Failure("opens late", "assert.equal(opensAt") + Failure("closes early", "assert.equal(closesAt");

        string? where = FailureLocation.Describe(output, _root)?.Replace("\r\n", "\n");

        Assert.NotNull(where);
        Assert.Equal(2, Occurrences(where, "(the failing test as written)"));
        Assert.Contains("- test/hours.test.js lines 3-5 (the failing test as written):", where);
        Assert.Contains("- test/hours.test.js lines 8-10 (the failing test as written):", where);
        Assert.DoesNotContain("line 1:", where);
    }

    [Fact]
    public void Two_files_with_a_test_starting_on_the_same_line_are_two_blocks()
    {
        string[] first = ["const { test } = require('node:test');", "", "test('first file', () => {", "  const left = 1;", "  assert.equal(left, 2);", "});"];
        string[] second = ["const { test } = require('node:test');", "", "test('second file', () => {", "  const right = 3;", "  assert.equal(right, 4);", "});"];
        WriteProjectFile(Path.Combine("test", "first.test.js"), first);
        WriteProjectFile(Path.Combine("test", "second.test.js"), second);
        string output = $"AssertionError: first\n    at TestContext.<anonymous> (test\\first.test.js:{LineOf(first, "assert.equal(left")}:3)\n" +
                        $"AssertionError: second\n    at TestContext.<anonymous> (test\\second.test.js:{LineOf(second, "assert.equal(right")}:3)";

        string? where = FailureLocation.Describe(output, _root)?.Replace("\r\n", "\n");

        Assert.NotNull(where);
        Assert.Equal(2, Occurrences(where, "(the failing test as written)"));
        Assert.Contains("- test/first.test.js lines 3-5 (the failing test as written):\n  3: test('first file', () => {\n  4:   const left = 1;\n  5:   assert.equal(left, 2);", where);
        Assert.Contains("- test/second.test.js lines 3-5 (the failing test as written):\n  3: test('second file', () => {\n  4:   const right = 3;\n  5:   assert.equal(right, 4);", where);
    }

    [Fact]
    public void A_test_block_longer_than_the_text_limit_is_shown_as_its_single_failing_line()
    {
        string[] file = [.. new[] { "test('huge', () => {" }, .. Enumerable.Range(0, 14).Select(line => $"  // {new string('h', 183)}"),
                         "  assert.equal(huge(), true);"];
        WriteProjectFile(Path.Combine("test", "huge.test.js"), file);
        string output = $"AssertionError: huge\n    at TestContext.<anonymous> (test\\huge.test.js:{file.Length}:3)";

        string? where = FailureLocation.Describe(output, _root)?.Replace("\r\n", "\n");

        Assert.NotNull(where);
        Assert.Equal(Heading + "\n- test/huge.test.js line 16: assert.equal(huge(), true);", where);
        Assert.DoesNotContain("lines 1-16", where);
    }
}
