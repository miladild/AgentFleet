using System.Text.Json;

namespace AgentFleet.Tests;

// Requirement list and coverage rules that a review found open: skipped tests, helper methods, literal forms of names, and bounds.
public sealed class PlanRequirementReviewTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<PlanRequirement> Requirements(params string[] texts) =>
        texts.Select((text, index) => new PlanRequirement("R" + (index + 1), text, [])).ToList();

    private static IReadOnlyList<PlanRequirement> Cover(IReadOnlyList<PlanRequirement> requirements, string path, string text) =>
        RequirementCoverage.Apply(requirements, [new TestFileText(path, text)]);

    [Theory]
    [InlineData("test.skip(\"R1: rejects zero\", () => {});")]
    [InlineData("it.skip(\"R1: rejects zero\", () => {});")]
    [InlineData("describe.skip(\"R1: rejects zero\", () => {});")]
    [InlineData("it.todo(\"R1: rejects zero\");")]
    [InlineData("test.skip.each([1])(\"R1 %s\", () => {});")]
    public void Skipped_or_todo_javascript_tests_do_not_cover_a_requirement(string call)
    {
        string text = call + "\ntest(\"R2: accepts one\", () => {});\n";

        IReadOnlyList<PlanRequirement> covered = Cover(Requirements("Rejects zero", "Accepts one"), "tests/limit.test.js", text);

        Assert.Empty(covered[0].CoveredBy);
        Assert.Equal(["R2: accepts one"], covered[1].CoveredBy);
    }

    [Fact]
    public void A_file_whose_only_tests_are_skipped_does_not_cover_by_its_text()
    {
        IReadOnlyList<PlanRequirement> covered = Cover(Requirements("Rejects zero"), "tests/limit.test.js", "test.skip(\"R1: rejects zero\", () => {});\n");

        Assert.Empty(covered[0].CoveredBy);
    }

    [Theory]
    [InlineData("test.each([[1], [2]])(\"R1: rejects %d\", (n) => {});", "R1: rejects %d")]
    [InlineData("it.each(['a', 'b'])(\"R1: names %s\", () => {});", "R1: names %s")]
    [InlineData("describe.each([1])(\"R1: group %d\", () => {});", "R1: group %d")]
    public void Each_tables_name_their_tests(string call, string expected)
    {
        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/limit.test.js", call + "\n");

        Assert.Equal([expected], names);
    }

    [Fact]
    public void A_helper_method_without_a_test_attribute_does_not_cover_a_requirement()
    {
        const string file = "public class LimitTests\n{\n    [Fact] public void Accepts_one() { }\n    private static void R2_helper() { }\n}\n";

        IReadOnlyList<PlanRequirement> covered = Cover(Requirements("Rejects zero", "Accepts one"), "tests/LimitTests.cs", file);

        Assert.Equal(["Accepts_one"], RequirementCoverage.TestNames("tests/LimitTests.cs", file));
        Assert.Empty(covered[1].CoveredBy);
    }

    [Theory]
    [InlineData("[Fact(Skip = \"not yet\")] public void R1_rejects_zero() { }")]
    [InlineData("[Theory(Skip = \"not yet\")] public void R1_rejects_zero(int n) { }")]
    [InlineData("[Ignore(\"not yet\")] [Test] public void R1_rejects_zero() { }")]
    [InlineData("[Test] [Ignore(\"not yet\")] public void R1_rejects_zero() { }")]
    public void A_skipped_csharp_test_does_not_cover_a_requirement(string skipped)
    {
        string file = "public class LimitTests\n{\n    " + skipped + "\n    [Fact] public void R2_accepts_one() { }\n}\n";

        IReadOnlyList<PlanRequirement> covered = Cover(Requirements("Rejects zero", "Accepts one"), "tests/LimitTests.cs", file);

        Assert.Empty(covered[0].CoveredBy);
        Assert.Equal(["R2_accepts_one"], covered[1].CoveredBy);
    }

    [Fact]
    public void A_verbatim_display_name_is_read_like_a_plain_one()
    {
        const string file = "public class LimitTests\n{\n    [Fact(DisplayName = @\"R5: accepts one\")]\n    public void Accepts_one() { }\n}\n";

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/LimitTests.cs", file);

        Assert.NotNull(names);
        Assert.Contains("R5: accepts one", names);
    }

    [Fact]
    public void A_go_test_named_with_an_id_straight_after_the_prefix_is_covered()
    {
        IReadOnlyList<PlanRequirement> covered = Cover(Requirements("Rejects zero"), "limit_test.go", "func TestR1_rejects_zero(t *testing.T) {}\n");

        Assert.Equal(["TestR1_rejects_zero"], covered[0].CoveredBy);
    }

    [Fact]
    public void A_python_docstring_that_mentions_a_test_is_not_a_test()
    {
        const string file = "def test_R1_rejects_zero():\n    \"\"\"\n    def test_R2_accepts_one():\n    \"\"\"\n    pass\n";

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/test_limit.py", file);

        Assert.Equal(["test_R1_rejects_zero"], names);
    }

    [Fact]
    public void A_python_test_with_a_skip_decorator_does_not_cover_a_requirement()
    {
        const string file = "@pytest.mark.skip(reason=\"later\")\ndef test_R1_rejects_zero():\n    pass\n\n" +
                            "@unittest.skip(\"later\")\ndef test_R2_accepts_one():\n    pass\n\ndef test_R3_keeps_order():\n    pass\n";

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/test_limit.py", file);

        Assert.Equal(["test_R3_keeps_order"], names);
    }

    [Fact]
    public void A_skip_if_decorator_still_counts_because_the_test_may_run()
    {
        const string file = "@pytest.mark.skipif(sys.platform == 'win32', reason='posix only')\ndef test_R1_rejects_zero():\n    pass\n";

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/test_limit.py", file);

        Assert.Equal(["test_R1_rejects_zero"], names);
    }

    [Fact]
    public void A_placeholder_word_at_the_start_of_a_requirement_is_kept()
    {
        IReadOnlyList<string> texts = PlanTools.ToRequirementTexts(Json("[\"None of the inputs may be mutated\", \"Rejects zero\"]"));

        Assert.Equal(["None of the inputs may be mutated", "Rejects zero"], texts);
    }

    [Fact]
    public void A_requirement_that_is_only_a_placeholder_is_still_dropped()
    {
        IReadOnlyList<string> texts = PlanTools.ToRequirementTexts(Json("[\"None\", \"N/A\", \"Nothing.\", \"Rejects zero\"]"));

        Assert.Equal(["Rejects zero"], texts);
    }

    [Fact]
    public void Assumptions_still_drop_a_sentence_that_starts_with_none()
    {
        IReadOnlyList<string> items = PlanTools.ToStringList(Json("[\"None of the inputs may be mutated\", \"Keeps order\"]"));

        Assert.Equal(["Keeps order"], items);
    }

    [Fact]
    public void An_object_with_another_text_field_name_keeps_its_text()
    {
        IReadOnlyList<string> texts = PlanTools.ToRequirementTexts(Json("[{\"id\":\"A1\",\"requirement\":\"Rejects zero\"}]"));

        Assert.Equal(["Rejects zero"], texts);
    }

    [Fact]
    public void A_requirement_with_a_line_break_is_stored_on_one_line()
    {
        IReadOnlyList<string> texts = PlanTools.ToRequirementTexts(Json("[\"Rejects zero\\nand one\"]"));

        Assert.Equal(["Rejects zero and one"], texts);
    }

    [Fact]
    public void Requirements_that_differ_only_in_punctuation_are_listed_twice()
    {
        IReadOnlyList<string> problems = PlanRequirements.Problems("Limit requests.", ["Rejects zero.", "Rejects zero"]);

        Assert.NotEmpty(problems);
    }

    [Theory]
    [InlineData("Limit requests per client, e.g. per IP address.")]
    [InlineData("Limit requests per client, i.e. per IP address, etc.")]
    public void Common_abbreviations_do_not_make_a_goal_long(string goal) =>
        Assert.False(PlanRequirements.IsLongGoal(goal));

    [Fact]
    public void Two_sentences_in_a_japanese_or_chinese_goal_make_it_long() =>
        Assert.True(PlanRequirements.IsLongGoal("限制每个客户的请求。拒绝突发流量。"));

    [Fact]
    public void A_list_of_forty_one_requirements_is_refused()
    {
        var texts = Enumerable.Range(1, PlanRequirements.MaxRequirements + 1).Select(number => "Rejects case " + number).ToArray();

        IReadOnlyList<string> problems = PlanRequirements.Problems("Limit requests.", texts);

        Assert.NotEmpty(problems);
    }

    [Fact]
    public void A_requirement_longer_than_the_limit_is_refused()
    {
        IReadOnlyList<string> problems = PlanRequirements.Problems("Limit requests.", [new string('a', PlanRequirements.MaxRequirementCharacters + 1)]);

        Assert.NotEmpty(problems);
    }

    [Fact]
    public void The_uncovered_feedback_stays_short_for_thirty_long_requirements()
    {
        var uncovered = Enumerable.Range(1, 30)
            .Select(number => new PlanRequirement("R" + number, new string((char)('a' + number % 26), 200), []))
            .ToArray();

        string feedback = RequirementCoverage.UncoveredFeedback(uncovered);

        Assert.True(feedback.Length < 3000, $"feedback is {feedback.Length} characters");
    }

    [Fact]
    public void A_regex_literal_with_a_quote_does_not_hide_a_comment_marker_on_its_line()
    {
        const string file = "const quote = /\"/; // test(\"R1: commented out\", () => {});\ntest(\"R2: accepts one\", () => {});\n";

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/quote.test.js", file);

        Assert.Equal(["R2: accepts one"], names);
    }

    [Fact]
    public void A_division_sign_is_not_read_as_the_start_of_a_regex_literal()
    {
        const string file = "const ratio = total / count; test(\"R1: ratio\", () => {});\n";

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/ratio.test.js", file);

        Assert.Equal(["R1: ratio"], names);
    }
}
