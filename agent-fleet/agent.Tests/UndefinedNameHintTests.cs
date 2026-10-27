namespace AgentFleet.Tests;

/// <summary>
/// A check that fails with "X is not defined" for a name the project declares in one of its source files. The retry message
/// names the file and the line, because the usual cause is a test that loads part of a file on its own and does not load that
/// name; the model then looks at the list of names the test loads instead of at the code that runs in the browser.
/// </summary>
public sealed class UndefinedNameHintTests : IDisposable
{
    private const string Node = "ReferenceError: loadAlerts is not defined\n    at socket (src/web/board.js:4:5)";
    private const string Browser = "Uncaught ReferenceError: loadAlerts is not defined";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-undefined-" + Guid.NewGuid().ToString("N"));

    public UndefinedNameHintTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "web"));
        File.WriteAllLines(Path.Combine(_root, "src", "web", "board.js"),
            ["const refresh = 5000;", "", "function loadAlerts() {", "  return fetch('/alerts');", "}"]);
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
    public void A_missing_name_declared_in_the_project_names_its_file_and_line()
    {
        string? hint = UndefinedNameHint.Hint(Node, _root);

        Assert.Equal("loadAlerts is declared in src/web/board.js (line 3). A test that runs part of that file on its own may not load " +
            "loadAlerts: look for the list of names it evaluates or the function it extracts, and add loadAlerts there, or move the call " +
            "so the code the test loads does not need loadAlerts.", hint);
    }

    [Theory]
    [InlineData(Node)]
    [InlineData(Browser)]
    public void Node_and_browser_wordings_of_the_error_both_give_the_hint(string output)
    {
        Assert.StartsWith("loadAlerts is declared in src/web/board.js (line 3).", UndefinedNameHint.Hint(output, _root));
    }

    [Fact]
    public void The_same_failure_gives_the_same_hint_every_time()
    {
        Assert.Equal(UndefinedNameHint.Hint(Node, _root), UndefinedNameHint.Hint(Node, _root));
    }

    [Fact]
    public void A_name_declared_nowhere_in_the_project_gives_no_hint()
    {
        Assert.Null(UndefinedNameHint.Hint("ReferenceError: fetchPrices is not defined", _root));
    }

    [Theory]
    [InlineData("loadAlerts is not defined")]
    [InlineData("TypeError: loadAlerts is not a function")]
    public void Output_without_a_reference_error_for_the_name_gives_no_hint(string output)
    {
        Assert.Null(UndefinedNameHint.Hint(output, _root));
    }

    [Fact]
    public void A_missing_project_folder_or_empty_output_gives_no_hint()
    {
        Assert.Null(UndefinedNameHint.Hint(null, _root));
        Assert.Null(UndefinedNameHint.Hint(Node, null));
        Assert.Null(UndefinedNameHint.Hint(Node, Path.Combine(_root, "missing")));
    }

    [Theory]
    [InlineData("function priceFeed() {}")]
    [InlineData("export function priceFeed() {}")]
    [InlineData("const priceFeed = () => 1;")]
    [InlineData("let priceFeed = 1;")]
    [InlineData("class priceFeed {}")]
    public void Each_top_level_declaration_form_counts(string declaration)
    {
        File.WriteAllText(Path.Combine(_root, "feed.js"), "// prices\n" + declaration + "\n");

        Assert.StartsWith("priceFeed is declared in feed.js (line 2).", UndefinedNameHint.Hint("ReferenceError: priceFeed is not defined", _root));
    }

    [Fact]
    public void A_declaration_inside_another_function_does_not_count()
    {
        File.WriteAllText(Path.Combine(_root, "feed.js"), "function outer() {\n  function priceFeed() {}\n}\n");

        Assert.Null(UndefinedNameHint.Hint("ReferenceError: priceFeed is not defined", _root));
    }

    [Fact]
    public void When_two_files_declare_the_name_the_first_in_sorted_order_is_named()
    {
        // The sorted order is ordinal, so a capital letter sorts before a small one. Directory listings on Windows sort without
        // case, which would put alpha.js first: the hint must not follow the listing.
        File.WriteAllText(Path.Combine(_root, "alpha.js"), "function loadAlerts() {}\n");
        File.WriteAllText(Path.Combine(_root, "Zeta.js"), "\nfunction loadAlerts() {}\n");

        Assert.StartsWith("loadAlerts is declared in Zeta.js (line 2).", UndefinedNameHint.Hint(Node, _root));
    }

    [Fact]
    public void A_declaration_under_node_modules_is_not_used()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "dep"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "dep", "index.js"), "function priceFeed() {}\n");

        Assert.Null(UndefinedNameHint.Hint("ReferenceError: priceFeed is not defined", _root));
    }

    [Fact]
    public void A_file_over_200000_bytes_is_not_searched()
    {
        string text = "function priceFeed() {}\n";
        File.WriteAllText(Path.Combine(_root, "big.js"), text + new string('/', 200_001 - text.Length));

        Assert.Null(UndefinedNameHint.Hint("ReferenceError: priceFeed is not defined", _root));
    }

    [Fact]
    public void A_file_of_exactly_200000_bytes_is_searched()
    {
        string text = "function priceFeed() {}\n";
        File.WriteAllText(Path.Combine(_root, "big.js"), text + new string('/', 200_000 - text.Length));

        Assert.StartsWith("priceFeed is declared in big.js (line 1).", UndefinedNameHint.Hint("ReferenceError: priceFeed is not defined", _root));
    }

    [Fact]
    public void A_follow_up_message_carries_the_hint_once_and_only_when_the_output_has_the_error()
    {
        string hint = UndefinedNameHint.Hint(Node, _root)!;
        var step = new PlanStep(1, "alerts", "detail", ["src/web/board.js"], "node --test", "standard", StepStatus.Running, null, 1, null, null);
        var plan = new PlanRecord("0123456789abcdef0123456789abcdef", "t", "Price alerts", PlanStatus.Running, null, [], [], [], null, null,
            [step], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) with { WorkingDirectory = _root };

        string message = RepairMessages.Build(plan, step, Node, 2, [], [], sameOutput: false, changedFiles: null);
        string quiet = RepairMessages.Build(plan, step, "AssertionError: values differ", 2, [], [], sameOutput: false, changedFiles: null);

        Assert.Equal(1, Occurrences(message, hint));
        Assert.DoesNotContain("is declared in", quiet);
    }

    [Fact]
    public void The_retry_prompt_of_a_new_attempt_carries_the_hint_once()
    {
        string hint = UndefinedNameHint.Hint(Node, _root)!;
        var step = new PlanStep(1, "alerts", "detail", ["src/web/board.js"], "node --test", "standard", StepStatus.Running, null, 1, null, null);
        var plan = new PlanRecord("0123456789abcdef0123456789abcdef", "t", "Price alerts", PlanStatus.Running, null, [], [], [], null, null,
            [step], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) with { WorkingDirectory = _root };

        string prompt = PlanPromptComposition.BuildPrompt(plan, step, 2, Node);

        Assert.Equal(1, Occurrences(prompt, hint));
    }

    private static int Occurrences(string text, string fragment) => text.Split(fragment).Length - 1;
}
