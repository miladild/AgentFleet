namespace AgentFleet.Tests;

public sealed class PlanProjectDocumentsTests : IDisposable
{
    private const string DocumentLine = "Project documents (read the part about this step with read_file before you change code; " +
        "they may state rules the tests only sample): PLAN.md, ARCHITECTURE.md.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-docs-" + Guid.NewGuid().ToString("N"));

    public PlanProjectDocumentsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Lists_known_documents_with_their_real_names_in_a_fixed_order()
    {
        File.WriteAllText(Path.Combine(_root, "readme.md"), "Synthetic readme.");
        File.WriteAllText(Path.Combine(_root, "PLAN.md"), "Synthetic plan.");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "Synthetic notes.");

        Assert.Equal(["PLAN.md", "readme.md"], PlanProjectDocuments.Find(_root));
    }

    [Fact]
    public void Order_follows_the_known_list_not_the_alphabet()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "Synthetic readme.");
        File.WriteAllText(Path.Combine(_root, "ARCHITECTURE.md"), "Synthetic architecture.");
        File.WriteAllText(Path.Combine(_root, "PLAN.md"), "Synthetic plan.");

        Assert.Equal(["PLAN.md", "ARCHITECTURE.md", "README.md"], PlanProjectDocuments.Find(_root));
    }

    [Fact]
    public void Empty_folder_lists_nothing()
    {
        Assert.Empty(PlanProjectDocuments.Find(_root));
    }

    [Fact]
    public void Missing_or_blank_folder_lists_nothing()
    {
        Assert.Empty(PlanProjectDocuments.Find(Path.Combine(_root, "missing")));
        Assert.Empty(PlanProjectDocuments.Find(null));
        Assert.Empty(PlanProjectDocuments.Find(""));
    }

    [Fact]
    public void A_folder_named_like_a_document_is_not_listed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "PLAN.md"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "Synthetic readme.");

        Assert.Equal(["README.md"], PlanProjectDocuments.Find(_root));
    }

    [Fact]
    public void Documents_over_200000_bytes_are_not_listed_and_exactly_200000_is_listed()
    {
        File.WriteAllText(Path.Combine(_root, "PLAN.md"), new string('a', 200_001));
        File.WriteAllText(Path.Combine(_root, "SPEC.md"), new string('a', 200_000));

        Assert.Equal(["SPEC.md"], PlanProjectDocuments.Find(_root));
    }

    [Fact]
    public async Task Prompt_names_the_documents_once_right_after_the_overall_goal()
    {
        File.WriteAllText(Path.Combine(_root, "ARCHITECTURE.md"), "Synthetic architecture.");
        File.WriteAllText(Path.Combine(_root, "PLAN.md"), "Synthetic plan.");
        var input = PlanPromptGoldenTests.Scenario("first");
        input = input with { Plan = input.Plan with { WorkingDirectory = _root } };
        PlanStepPromptData data = input.Data();

        string text = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);

        Assert.Equal(1, text.Split(DocumentLine).Length - 1);
        Assert.Contains($"Overall goal: {input.Plan.Goal}{Environment.NewLine}{DocumentLine}{Environment.NewLine}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_has_no_document_line_and_is_unchanged_when_the_folder_holds_none()
    {
        var input = PlanPromptGoldenTests.Scenario("first");
        PlanStepPromptData without = input.Data();
        PlanStepPromptData empty = (input with { Plan = input.Plan with { WorkingDirectory = _root } }).Data();
        PlanStepPromptData missing = (input with { Plan = input.Plan with { WorkingDirectory = Path.Combine(_root, "missing") } }).Data();

        string expected = await PlanPromptComposition.ComposeAsync(without.Parts.Text, without);

        Assert.Equal(expected, await PlanPromptComposition.ComposeAsync(empty.Parts.Text, empty));
        Assert.Equal(expected, await PlanPromptComposition.ComposeAsync(missing.Parts.Text, missing));
        Assert.DoesNotContain("Project documents", expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_folder_gives_the_same_prompt_text_every_time()
    {
        File.WriteAllText(Path.Combine(_root, "ARCHITECTURE.md"), "Synthetic architecture.");
        File.WriteAllText(Path.Combine(_root, "PLAN.md"), "Synthetic plan.");
        var input = PlanPromptGoldenTests.Scenario("first");
        input = input with { Plan = input.Plan with { WorkingDirectory = _root } };
        PlanStepPromptData data = input.Data();

        string first = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);
        string second = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);

        Assert.Equal(first, second);
    }
}
