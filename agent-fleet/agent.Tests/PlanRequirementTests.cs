using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentFleet.Tests;

public sealed class PlanRequirementTests : PlanTestBase
{
    private static readonly FakeValidator AlwaysValid = new(code => new DiagramCheck(true, true, code, null));
    private const string TwoSentenceGoal = "Limit requests per client. Reject bursts above the limit.";

    private PlanTools Tools(TestFirstMode mode = TestFirstMode.On) =>
        new(Store, AlwaysValid, (_, _, _) => Task.FromResult("Exit code: 0\n--- stdout ---\nok"), testFirst: mode);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // A tests step followed by the code step that makes its tests pass: the shape the test-first review expects.
    private static JsonElement? TestFirstSteps() => JsonSerializer.SerializeToElement(new[]
    {
        new PlanStepInput("Write limit tests", "Reject zero and negative input", ["tests/LimitTests.cs"], "dotnet test", "standard", Kind: "tests"),
        new PlanStepInput("Add the limit", "Make the tests pass", ["src/limit.cs"], "dotnet test", "standard"),
    });

    private async Task<string> Propose(PlanTools tools, string goal, JsonElement? requirements, bool plannerProposed = true) =>
        await tools.ProposePlanAsync("Limit", goal, @"C:\p", null, null, null, null, TestFirstSteps(), default,
            plannerProposed: plannerProposed, requirements: requirements);

    private PlanRecord Saved() => Store.Get(Assert.Single(Store.List()).Id)!;

    [Fact]
    public async Task Requirements_sent_as_strings_are_stored_with_plan_ids_in_order()
    {
        await Propose(Tools(), "Limit requests.", Json("[\"Rejects zero\",\"Rejects negatives\"]"));

        PlanRecord saved = Saved();
        Assert.Equal(["R1", "R2"], saved.Requirements!.Select(requirement => requirement.Id));
        Assert.Equal(["Rejects zero", "Rejects negatives"], saved.Requirements!.Select(requirement => requirement.Text));
        Assert.All(saved.Requirements!, requirement => Assert.Empty(requirement.CoveredBy));
    }

    [Fact]
    public async Task Requirements_sent_as_objects_use_their_text_or_description_field()
    {
        await Propose(Tools(), "Limit requests.", Json("[{\"id\":\"X9\",\"text\":\"Rejects zero\"},{\"description\":\"Accepts one\"}]"));

        Assert.Equal(["Rejects zero", "Accepts one"], Saved().Requirements!.Select(requirement => requirement.Text));
    }

    [Fact]
    public async Task Ids_the_model_sends_are_ignored_and_ids_follow_the_order_of_the_list()
    {
        await Propose(Tools(), "Limit requests.", Json("[{\"id\":\"R7\",\"text\":\"first in the list\"},{\"id\":\"R1\",\"text\":\"second\"}]"));

        PlanRecord saved = Saved();
        Assert.Equal(["R1", "R2"], saved.Requirements!.Select(requirement => requirement.Id));
        Assert.Equal(["first in the list", "second"], saved.Requirements!.Select(requirement => requirement.Text));
    }

    [Fact]
    public async Task Requirements_sent_as_a_string_holding_json_are_read()
    {
        await Propose(Tools(), "Limit requests.", Json("\"[\\\"Rejects zero\\\", \\\"Rejects negatives\\\"]\""));

        Assert.Equal(["Rejects zero", "Rejects negatives"], Saved().Requirements!.Select(requirement => requirement.Text));
    }

    [Fact]
    public async Task Absent_requirements_leave_the_plan_without_a_list()
    {
        await Propose(Tools(), "Limit requests.", requirements: null);

        Assert.Null(Saved().Requirements);
    }

    [Fact]
    public async Task Requirements_survive_a_reload_and_are_exported_to_markdown()
    {
        await Propose(Tools(), "Limit requests.", Json("[\"Rejects zero\"]"));
        PlanRecord saved = Saved();

        PlanRecord reloaded = Store.Get(saved.Id)!;
        Assert.Equal("R1", Assert.Single(reloaded.Requirements!).Id);
        string markdown = FleetPlanStore.ToMarkdown(reloaded);
        Assert.Contains("## Requirements", markdown);
        Assert.Contains("R1: Rejects zero", markdown);
    }

    [Fact]
    public async Task A_plan_saved_before_the_list_existed_still_loads()
    {
        await Propose(Tools(), "Limit requests.", Json("[\"Rejects zero\"]"));
        PlanRecord saved = Saved();
        string path = Path.Combine(PlansDirectory, saved.Id + ".json");
        JsonObject record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(record.Remove("requirements"));
        File.WriteAllText(path, record.ToJsonString());

        PlanRecord loaded = Store.Get(saved.Id)!;
        Assert.Null(loaded.Requirements);
        Assert.Equal(saved.Steps.Count, loaded.Steps.Count);
    }

    [Fact]
    public async Task A_planner_plan_with_a_two_sentence_goal_and_no_requirements_is_refused()
    {
        string reply = await Propose(Tools(), TwoSentenceGoal, requirements: null);

        Assert.Contains("NOT saved", reply);
        Assert.Contains("requirements", reply);
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task A_one_sentence_goal_without_requirements_is_allowed()
    {
        await Propose(Tools(), "Limit requests per client.", requirements: null);

        Assert.Null(Saved().Requirements);
    }

    [Fact]
    public async Task A_long_goal_of_one_sentence_needs_requirements_too()
    {
        string longGoal = string.Concat(Enumerable.Repeat("Limit requests per client and keep the count ", 4)) + "per minute.";
        Assert.True(longGoal.Length > 160);

        string reply = await Propose(Tools(), longGoal, requirements: null);

        Assert.Contains("NOT saved", reply);
    }

    [Theory]
    [InlineData("[\"Rejects zero\",\"\"]")]
    [InlineData("[\"Rejects zero\",\"rejects  ZERO\"]")]
    public async Task Blank_and_duplicate_requirements_are_refused(string requirements)
    {
        string reply = await Propose(Tools(), "Limit requests.", Json(requirements));

        Assert.Contains("NOT saved", reply);
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task With_the_setting_off_a_long_goal_without_requirements_is_saved_as_before()
    {
        string reply = await Propose(Tools(TestFirstMode.Off), TwoSentenceGoal, requirements: null);

        Assert.DoesNotContain("NOT saved", reply);
        Assert.Null(Saved().Requirements);
    }

    [Fact]
    public async Task The_plan_file_and_api_routes_are_not_asked_for_requirements()
    {
        string reply = await Propose(Tools(), TwoSentenceGoal, requirements: null, plannerProposed: false);

        Assert.DoesNotContain("NOT saved", reply);
    }

    [Theory]
    [InlineData("it(\"R1: rejects zero\", () => {});", "R1: rejects zero")]
    [InlineData("test.only('R2: keeps \\'quotes\\'', () => {});", "R2: keeps \\'quotes\\'")]
    [InlineData("describe(\"R3 R4: group\", () => {});", "R3 R4: group")]
    public void Javascript_test_names_are_read_from_the_call_not_from_comments(string call, string expected)
    {
        string text = "// test('R9: commented out', () => {});\n" + call + "\n";

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/limit.test.js", text);

        Assert.Equal([expected], names);
    }

    [Fact]
    public void Csharp_method_names_and_display_names_are_read()
    {
        string text = """
            public class LimitTests
            {
                [Fact]
                public void R3_R4_rejects_leading_zeros() { }

                [Fact(DisplayName = "R5: accepts one")]
                public void Accepts_one() { }
            }
            """;

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/LimitTests.cs", text);

        Assert.NotNull(names);
        Assert.Contains("R3_R4_rejects_leading_zeros", names);
        Assert.Contains("R5: accepts one", names);
    }

    [Fact]
    public void A_comment_marker_inside_a_string_does_not_hide_the_rest_of_the_line()
    {
        string text = "var url = \"http://example.test/x\"; [Fact] public void R1_accepts_the_url() { }";

        IReadOnlyList<string>? names = RequirementCoverage.TestNames("tests/UrlTests.cs", text);

        Assert.Equal(["R1_accepts_the_url"], names);
    }

    [Fact]
    public void Python_and_go_names_are_read()
    {
        IReadOnlyList<string>? python = RequirementCoverage.TestNames("tests/test_limit.py", "# def test_R9_old():\ndef test_R1_rejects_zero():\n    pass\n");
        IReadOnlyList<string>? go = RequirementCoverage.TestNames("limit_test.go", "func Test_R2_rejects_zero(t *testing.T) {}\n");

        Assert.Equal(["test_R1_rejects_zero"], python);
        Assert.Equal(["Test_R2_rejects_zero"], go);
    }

    [Theory]
    [InlineData("R30 and xR3 are other ids", "R3", false)]
    [InlineData("R3_R4_rejects", "R3", true)]
    [InlineData("R4_R3", "R3", true)]
    [InlineData("R3", "R3", true)]
    [InlineData("r3 is lower case", "R3", false)]
    public void An_id_is_a_whole_token_and_matches_case_exactly(string text, string id, bool expected) =>
        Assert.Equal(expected, RequirementCoverage.ContainsId(text, id));

    [Fact]
    public void Ids_that_appear_only_in_comments_do_not_cover_a_requirement()
    {
        var requirements = Requirements("Rejects zero");
        const string file = "// [Fact] public void R1_rejects_zero() { }\npublic class LimitTests\n{\n    [Fact]\n    public void Accepts_one() { }\n}\n";

        IReadOnlyList<PlanRequirement> covered = RequirementCoverage.Apply(requirements, [new TestFileText("tests/LimitTests.cs", file)]);

        Assert.Empty(covered[0].CoveredBy);
    }

    [Fact]
    public void Ids_in_a_name_cover_the_requirement_and_the_names_are_collected()
    {
        var requirements = Requirements("Rejects zero", "Rejects negatives", "Rejects leading zeros");
        const string first = "[Fact] public void R1_rejects_zero() { }\n[Fact] public void R2_rejects_negatives() { }\n";
        const string second = "[Fact] public void R1_also_rejects_zero() { }\n";

        IReadOnlyList<PlanRequirement> covered = RequirementCoverage.Apply(requirements,
            [new TestFileText("tests/A.cs", first), new TestFileText("tests/B.cs", second)]);

        Assert.Equal(["R1_rejects_zero", "R1_also_rejects_zero"], covered[0].CoveredBy);
        Assert.Equal(["R2_rejects_negatives"], covered[1].CoveredBy);
        Assert.Empty(covered[2].CoveredBy);
    }

    [Theory]
    [InlineData("tests/notes.rb", "# R1 rejects zero\n")]
    [InlineData("tests/LimitTests.cs", "// R1 rejects zero\n")]
    public void A_file_with_no_readable_names_is_scanned_as_text(string path, string text)
    {
        Assert.Null(RequirementCoverage.TestNames(path, text));

        IReadOnlyList<PlanRequirement> covered = RequirementCoverage.Apply(Requirements("Rejects zero"), [new TestFileText(path, text)]);

        Assert.Equal([path + " (text match)"], covered[0].CoveredBy);
    }

    [Fact]
    public void Uncovered_feedback_names_each_uncovered_requirement_in_words()
    {
        var requirements = Requirements("Rejects zero", "Rejects leading zeros");

        string feedback = RequirementCoverage.UncoveredFeedback([requirements[1]]);

        Assert.Contains("R2", feedback);
        Assert.Contains("Rejects leading zeros", feedback);
        Assert.DoesNotContain("Rejects zero", feedback);
    }

    private static List<PlanRequirement> Requirements(params string[] texts) =>
        texts.Select((text, index) => new PlanRequirement("R" + (index + 1), text, [])).ToList();
}
