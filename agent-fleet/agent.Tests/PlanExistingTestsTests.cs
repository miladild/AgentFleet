using System.Text;

namespace AgentFleet.Tests;

public sealed class PlanExistingTestsTests : IDisposable
{
    private const string ExistingLine = "Existing tests (read one with read_file and write the new test the same way: same imports, " +
        "same setup, calling the real code and asserting on its real result): ";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-tests-" + Guid.NewGuid().ToString("N"));

    public PlanExistingTestsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Files_that_import_a_project_file_come_first_then_the_rest_alphabetically()
    {
        Write("test/b.test.ts", "Synthetic test without an import.");
        Write("test/a.test.ts", "import { x } from '../src/x';\ntest('x', () => {});");
        Write("notes.txt", "Synthetic notes.");

        Assert.Equal(["test/a.test.ts", "test/b.test.ts"], PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public void An_import_file_is_preferred_even_when_its_name_sorts_later()
    {
        Write("test/a.test.ts", "Synthetic test without an import.");
        Write("test/z.test.ts", "const x = require(\"../src/x\");\ntest('x', () => {});");

        Assert.Equal(["test/z.test.ts", "test/a.test.ts"], PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public void Only_the_first_4000_characters_are_read_for_an_import()
    {
        Write("test/a.test.ts", new string('/', 4000) + "\nimport { x } from '../src/x';\ntest('x', () => {});");
        Write("test/b.test.ts", "import { y } from './y';\ntest('y', () => {});");

        Assert.Equal(["test/b.test.ts", "test/a.test.ts"], PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public void At_most_three_files_are_listed()
    {
        for (int i = 1; i <= 4; i++)
        {
            Write($"tests/t{i}.test.js", "Synthetic test.");
        }

        Assert.Equal(["tests/t1.test.js", "tests/t2.test.js", "tests/t3.test.js"], PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public void Files_the_step_itself_names_are_left_out_whichever_slash_it_uses()
    {
        Write("test/a.test.ts", "import { x } from '../src/x';\ntest('x', () => {});");
        Write("test/b.test.ts", "Synthetic test.");

        Assert.Equal(["test/b.test.ts"], PlanExistingTests.Find(_root, ["test\\a.test.ts"]));
    }

    [Fact]
    public void Folders_named_like_a_test_file_are_not_listed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "x.test.ts"));
        Directory.CreateDirectory(Path.Combine(_root, "test", "y.test.ts"));
        Write("test/z.test.ts", "Synthetic test.");

        Assert.Equal(["test/z.test.ts"], PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public void Files_over_200000_bytes_are_not_listed_and_exactly_200000_is_listed()
    {
        Write("test/big.test.ts", new string('a', 200_001));
        Write("test/ok.test.ts", new string('a', 200_000));

        Assert.Equal(["test/ok.test.ts"], PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public void Nothing_under_node_modules_is_listed_and_the_folders_are_not_walked_recursively()
    {
        Write("test/node_modules/x.test.ts", "Synthetic test.");
        Write("test/deeper/nested.test.ts", "Synthetic test.");
        Write("test/a.test.ts", "Synthetic test.");

        Assert.Equal(["test/a.test.ts"], PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public void Top_level_test_and_spec_files_and_the_named_folders_are_all_found()
    {
        Write("a.spec.js", "Synthetic spec.");
        Write("__tests__/c.test.js", "Synthetic test.");
        Write("spec/d.spec.rb", "Synthetic spec.");
        Write("README.md", "Synthetic readme.");

        Assert.Equal(["__tests__/c.test.js", "a.spec.js", "spec/d.spec.rb"], PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public void Missing_or_blank_folder_and_a_folder_with_no_tests_give_nothing()
    {
        Write("src/app.ts", "Synthetic code.");

        Assert.Empty(PlanExistingTests.Find(Path.Combine(_root, "missing"), []));
        Assert.Empty(PlanExistingTests.Find(null, []));
        Assert.Empty(PlanExistingTests.Find("", []));
        Assert.Empty(PlanExistingTests.Find(_root, []));
    }

    [Fact]
    public async Task A_tests_step_gets_the_line_once_right_after_the_goal_when_there_are_no_documents()
    {
        Write("test/app.test.ts", "import { app } from '../src/app';\ntest('app', () => {});");
        Write("test/board.test.ts", "Synthetic test.");
        var input = PlanPromptGoldenTests.Scenario("first");
        input = input with { Plan = input.Plan with { WorkingDirectory = _root }, Step = input.Step with { Kind = "tests" } };
        PlanStepPromptData data = input.Data();
        string line = ExistingLine + "test/app.test.ts, test/board.test.ts.";

        string text = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);

        Assert.Equal(1, text.Split(line).Length - 1);
        Assert.Contains($"Overall goal: {input.Plan.Goal}{Environment.NewLine}{line}{Environment.NewLine}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tests_step_gets_the_line_right_after_the_documents_line()
    {
        Write("test/app.test.ts", "import { app } from '../src/app';\ntest('app', () => {});");
        Write("PLAN.md", "Synthetic plan.");
        var input = PlanPromptGoldenTests.Scenario("first");
        input = input with { Plan = input.Plan with { WorkingDirectory = _root }, Step = input.Step with { Kind = "tests" } };
        PlanStepPromptData data = input.Data();
        string documents = "Project documents (read the part about this step with read_file before you change code; " +
            "they may state rules the tests only sample): PLAN.md.";
        string line = ExistingLine + "test/app.test.ts.";

        string text = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);

        Assert.Contains($"{documents}{Environment.NewLine}{line}{Environment.NewLine}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_that_writes_no_tests_gets_no_line()
    {
        Write("test/app.test.ts", "import { app } from '../src/app';\ntest('app', () => {});");
        var input = PlanPromptGoldenTests.Scenario("first");
        input = input with { Plan = input.Plan with { WorkingDirectory = _root } };
        PlanStepPromptData data = input.Data();

        string text = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);

        Assert.DoesNotContain("Existing tests", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tests_step_in_a_folder_with_no_tests_gets_no_line()
    {
        Write("src/app.ts", "Synthetic code.");
        var input = PlanPromptGoldenTests.Scenario("first");
        input = input with { Plan = input.Plan with { WorkingDirectory = _root }, Step = input.Step with { Kind = "tests" } };
        PlanStepPromptData data = input.Data();

        string text = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);

        Assert.DoesNotContain("Existing tests", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_folder_gives_the_same_line_every_time()
    {
        Write("test/app.test.ts", "import { app } from '../src/app';\ntest('app', () => {});");
        Write("test/board.test.ts", "Synthetic test.");
        var input = PlanPromptGoldenTests.Scenario("first");
        input = input with { Plan = input.Plan with { WorkingDirectory = _root }, Step = input.Step with { Kind = "tests" } };
        PlanStepPromptData data = input.Data();

        string first = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);
        string second = await PlanPromptComposition.ComposeAsync(data.Parts.Text, data);

        Assert.Equal(first, second);
    }
}
