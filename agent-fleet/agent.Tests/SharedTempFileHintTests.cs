namespace AgentFleet.Tests;

/// <summary>
/// A check that fails while a test writes to a fixed name in the temp folder. A later test or round then sees what an earlier
/// one left in that file, so the retry names the file and the line and asks for a name that belongs to one test only.
/// </summary>
public sealed class SharedTempFileHintTests : IDisposable
{
    private const string Failing = "✖ alerts round trip\nAssertionError: expected 2 to equal 1";
    private const string Passing = "✔ alerts round trip (2.1ms)\nℹ tests 1\nℹ pass 1\nℹ fail 0";
    private const string FixedNameLine = "  const file = join(tmpdir(), 'alerts_test1.json');";

    private const string FixedNameTest = """
        import { test } from 'node:test';
        import { tmpdir } from 'node:os';
        import { join } from 'node:path';
        test('round trip', () => {
          const file = join(tmpdir(), 'alerts_test1.json');
        });
        """;

    private const string PythonFixedName = """
        import os
        import tempfile

        def test_round_trip():
            path = os.path.join(tempfile.gettempdir(), "alerts.json")
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-shared-temp-" + Guid.NewGuid().ToString("N"));

    public SharedTempFileHintTests() => Directory.CreateDirectory(_root);

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
    public void A_fixed_name_in_a_failing_run_names_the_file_and_line()
    {
        Write("test/alerts.test.ts", FixedNameTest);

        Assert.Equal(Expected("test/alerts.test.ts", 5), SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void The_same_failure_gives_the_same_hint_every_time()
    {
        Write("test/alerts.test.ts", FixedNameTest);

        Assert.Equal(SharedTempFileHint.Hint(Failing, _root), SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void A_python_gettempdir_join_with_a_literal_names_the_file_and_line()
    {
        Write("tests/test_alerts.py", PythonFixedName);

        Assert.Equal(Expected("tests/test_alerts.py", 5),
            SharedTempFileHint.Hint("FAILED tests/test_alerts.py::test_round_trip\nAssertionError: expected 2", _root));
    }

    [Theory]
    [InlineData("✖ alerts round trip")]
    [InlineData("FAIL alerts round trip")]
    [InlineData("1 test failed")]
    [InlineData("AssertionError: expected 2")]
    [InlineData("Assertion failed: expected 2")]
    public void Each_failing_word_in_the_output_gives_the_hint(string output)
    {
        Write("test/alerts.test.ts", FixedNameTest);

        Assert.NotNull(SharedTempFileHint.Hint(output, _root));
    }

    [Theory]
    [InlineData(Passing)]
    [InlineData("ok 1 - alerts round trip")]
    [InlineData("")]
    public void Output_without_a_failing_word_gives_no_hint(string output)
    {
        Write("test/alerts.test.ts", FixedNameTest);

        Assert.Null(SharedTempFileHint.Hint(output, _root));
    }

    [Theory]
    [InlineData("  const file = join(tmpdir(), 'alerts_' + randomUUID());")]
    [InlineData("  const file = join(tmpdir(), 'alerts_' + process.pid + '.json');")]
    [InlineData("  const file = join(tmpdir(), 'alerts_' + Math.random() + '.json');")]
    [InlineData("  const file = join(tmpdir(), 'alerts_' + Date.now() + '.json');")]
    [InlineData("  const file = join(tmpdir(), 'alerts_' + uuid() + '.json');")]
    [InlineData("  const dir = mkdtempSync(join(tmpdir(), 'alerts-'));")]
    [InlineData("  const file = join(tmpdir(), `alerts_${Date.now()}.json`);")]
    public void A_line_with_a_unique_name_or_a_template_gives_no_hint(string line)
    {
        Write("test/alerts.test.ts", line + "\n");

        Assert.Null(SharedTempFileHint.Hint(Failing, _root));
    }

    [Theory]
    [InlineData("    with tempfile.NamedTemporaryFile() as file:")]
    [InlineData("    with tempfile.TemporaryDirectory() as folder:")]
    [InlineData("    path = os.path.join(tempfile.gettempdir(), \"alerts.json\")  # NamedTemporaryFile, see above")]
    public void Python_temporary_file_helpers_give_no_hint(string line)
    {
        Write("tests/test_alerts.py", "import os\nimport tempfile\n\ndef test_round_trip():\n" + line + "\n");

        Assert.Null(SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void No_test_file_or_no_matching_line_gives_no_hint()
    {
        Assert.Null(SharedTempFileHint.Hint(Failing, _root));

        Write("test/alerts.test.ts", "test('round trip', () => {});\n");
        Assert.Null(SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void A_missing_project_folder_or_empty_output_gives_no_hint()
    {
        Write("test/alerts.test.ts", FixedNameTest);

        Assert.Null(SharedTempFileHint.Hint(null, _root));
        Assert.Null(SharedTempFileHint.Hint(Failing, null));
        Assert.Null(SharedTempFileHint.Hint(Failing, Path.Combine(_root, "missing")));
    }

    [Fact]
    public void A_test_file_over_200000_bytes_is_not_read()
    {
        const string text = "  const file = join(tmpdir(), 'alerts.json');\n";
        Write("test/big.test.ts", text + new string('/', 200_001 - text.Length));

        Assert.Null(SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void A_test_file_of_exactly_200000_bytes_is_read()
    {
        const string text = "  const file = join(tmpdir(), 'alerts.json');\n";
        Write("test/big.test.ts", text + new string('/', 200_000 - text.Length));

        Assert.Equal(Expected("test/big.test.ts", 1), SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void A_file_under_node_modules_is_not_read()
    {
        Write("node_modules/alerts.test.js", FixedNameLine + "\n");

        Assert.Null(SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void A_folder_named_like_a_test_file_is_not_read()
    {
        Write("x.test.ts/inner.js", FixedNameLine + "\n");

        Assert.Null(SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void When_two_files_match_the_ordinal_first_path_is_named()
    {
        // A top-level file sorts before a file in a folder, so the hint must not follow the directory listing order.
        Write("test/zeta.test.js", FixedNameLine + "\n");
        Write("alpha.test.js", FixedNameLine + "\n");

        Assert.Equal(Expected("alpha.test.js", 1), SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void Capital_letters_sort_before_small_ones_in_the_file_order()
    {
        Write("alpha.test.js", FixedNameLine + "\n");
        Write("Zeta.test.js", FixedNameLine + "\n");

        Assert.Equal(Expected("Zeta.test.js", 1), SharedTempFileHint.Hint(Failing, _root));
    }

    [Fact]
    public void The_first_matching_line_of_a_file_is_named()
    {
        Write("test/alerts.test.js", "const a = 1;\n" + FixedNameLine + "\nconst b = 2;\n" + FixedNameLine + "\n");

        Assert.Equal(Expected("test/alerts.test.js", 2), SharedTempFileHint.Hint(Failing, _root));
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Expected(string relative, int line) =>
        $"{relative} line {line} writes to a fixed name in the temp folder, so a later test or round sees what an earlier one left. " +
        "Give each test its own name (randomUUID from node:crypto, or fs.mkdtempSync) and remove the file in a finally block, " +
        "or in Python use tempfile.";
}

/// <summary>
/// The temp-file hint is said in the retry message and in the continuation prompt, after the undefined-name hint, and only when
/// it applies.
/// </summary>
public sealed class SharedTempFileHintWiringTests : IDisposable
{
    private const string Failure = "ReferenceError: loadAlerts is not defined\n    at socket (src/web/board.js:4:5)\n" +
        "✖ alerts round trip\nAssertionError: expected 2 to equal 1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-shared-temp-wiring-" + Guid.NewGuid().ToString("N"));

    public SharedTempFileHintWiringTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "web"));
        Directory.CreateDirectory(Path.Combine(_root, "test"));
        File.WriteAllText(Path.Combine(_root, "src", "web", "board.js"), "const refresh = 5000;\n\nfunction loadAlerts() {\n}\n");
        File.WriteAllText(Path.Combine(_root, "test", "alerts.test.js"), "const x = 1;\nconst file = join(tmpdir(), 'alerts.json');\n");
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
    public void The_repair_message_carries_the_hint_once_after_the_undefined_name_hint()
    {
        var (plan, step) = Plan();

        string message = RepairMessages.Build(plan, step, Failure, 2, [], [], sameOutput: false, changedFiles: null);

        string hint = SharedTempFileHint.Hint(Failure, _root)!;
        Assert.Equal("test/alerts.test.js line 2 writes to a fixed name in the temp folder, so a later test or round sees what an earlier one left. " +
            "Give each test its own name (randomUUID from node:crypto, or fs.mkdtempSync) and remove the file in a finally block, " +
            "or in Python use tempfile.", hint);
        Assert.Equal(1, Occurrences(message, hint));
        Assert.True(message.IndexOf(UndefinedNameHint.Hint(Failure, _root)!, StringComparison.Ordinal) < message.IndexOf(hint, StringComparison.Ordinal));
    }

    [Fact]
    public void The_retry_prompt_carries_the_hint_once_after_the_undefined_name_hint()
    {
        var (plan, step) = Plan();

        string prompt = PlanPromptComposition.BuildPrompt(plan, step, 2, Failure);

        string hint = SharedTempFileHint.Hint(Failure, _root)!;
        Assert.Equal(1, Occurrences(prompt, hint));
        Assert.True(prompt.IndexOf(UndefinedNameHint.Hint(Failure, _root)!, StringComparison.Ordinal) < prompt.IndexOf(hint, StringComparison.Ordinal));
    }

    [Fact]
    public void Neither_message_has_the_hint_when_the_output_has_no_failing_word()
    {
        var (plan, step) = Plan();
        const string passing = "ReferenceError: loadAlerts is not defined\n    at socket (src/web/board.js:4:5)\nℹ fail 0";

        string message = RepairMessages.Build(plan, step, passing, 2, [], [], sameOutput: false, changedFiles: null);
        string prompt = PlanPromptComposition.BuildPrompt(plan, step, 2, passing);

        Assert.DoesNotContain("writes to a fixed name", message);
        Assert.DoesNotContain("writes to a fixed name", prompt);
    }

    private (PlanRecord Plan, PlanStep Step) Plan()
    {
        var step = new PlanStep(1, "alerts", "detail", ["test/alerts.test.js"], "node --test", "standard", StepStatus.Running, null, 1, null, null);
        var plan = new PlanRecord("0123456789abcdef0123456789abcdef", "t", "Price alerts", PlanStatus.Running, null, [], [], [], null, null,
            [step], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) with { WorkingDirectory = _root };
        return (plan, step);
    }

    private static int Occurrences(string text, string fragment) => text.Split(fragment).Length - 1;
}
