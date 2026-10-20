namespace AgentFleet.Tests;

// Which tools a plan runner request is offered. With the setting off the list is the one a step had before dispute_test
// existed; dispute_test goes only to an ordinary step while frozen tests are in play; the ruler and the reviewer read and answer.
public sealed class DisputeToolPolicyTests
{
    // Every tool name the fleet registers for a model (Program.cs).
    private static readonly string[] AllTools =
    [
        "run_sandboxed_code", "read_file", "write_file", "edit_file", "list_directory", "find_files", "search_files", "run_git_command",
        "run_command", "web_search", "web_fetch", "project_overview", "move_file", "delete_file", "http_request", "propose_plan", "get_plan",
        "report_blocker", "dispute_test", "propose_check", "validate_diagram", "record_decision", "search_context"
    ];

    // What a model carrying out a step was offered before dispute_test existed.
    private static readonly string[] StepTools =
    [
        "read_file", "write_file", "edit_file", "list_directory", "find_files", "search_files", "project_overview", "move_file", "delete_file",
        "run_command", "run_git_command", "get_plan", "search_context", "report_blocker"
    ];

    private static readonly string[] ReadOnlyTools = ["read_file", "list_directory", "find_files", "search_files", "project_overview"];

    private static string[] Offered(bool fromRunner, string? role, bool disputeOffered = false) =>
        AllTools.Where(tool => PlanRunnerToolPolicy.Offers(tool, fromRunner, role, disputeOffered))
            .OrderBy(tool => tool, StringComparer.Ordinal)
            .ToArray();

    private static string[] Sorted(IEnumerable<string> tools) => tools.OrderBy(tool => tool, StringComparer.Ordinal).ToArray();

    [Fact]
    public void With_the_setting_off_a_step_is_offered_the_tools_it_was_before()
    {
        Assert.Equal(Sorted(StepTools), Offered(fromRunner: true, role: null, disputeOffered: false));
    }

    [Fact]
    public void With_frozen_tests_in_play_a_step_is_offered_dispute_test_and_nothing_else_new()
    {
        Assert.Equal(Sorted(StepTools.Append(TestDispute.Tool)), Offered(fromRunner: true, role: null, disputeOffered: true));
    }

    [Fact]
    public void A_tests_step_is_never_offered_dispute_test()
    {
        Assert.Equal(Sorted(StepTools), Offered(fromRunner: true, role: PlanRunnerToolPolicy.TestWriterRole, disputeOffered: true));
    }

    [Fact]
    public void The_ruler_and_the_reviewer_are_offered_only_the_read_only_tools()
    {
        Assert.Equal(Sorted(ReadOnlyTools), Offered(fromRunner: true, role: PlanRunnerToolPolicy.TestRulingRole, disputeOffered: true));
        Assert.Equal(Sorted(ReadOnlyTools), Offered(fromRunner: true, role: PlanRunnerToolPolicy.StepReviewRole, disputeOffered: true));
    }

    [Fact]
    public void The_auditor_is_never_offered_dispute_test()
    {
        Assert.DoesNotContain(TestDispute.Tool, Offered(fromRunner: true, role: PlanRunnerToolPolicy.CheckAuditRole, disputeOffered: true));
    }

    [Fact]
    public void Chat_is_never_offered_dispute_test()
    {
        Assert.DoesNotContain(TestDispute.Tool, Offered(fromRunner: false, role: null, disputeOffered: true));
    }

    [Fact]
    public void The_ruler_is_refused_dispute_test_and_every_tool_but_the_read_tools()
    {
        Assert.Null(PlanRunnerToolPolicy.Refusal(TestDispute.Tool, null, null));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal(TestDispute.Tool, null, PlanRunnerToolPolicy.TestRulingRole));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal("report_blocker", null, PlanRunnerToolPolicy.TestRulingRole));
        Assert.NotNull(PlanRunnerToolPolicy.Refusal("write_file", null, PlanRunnerToolPolicy.TestRulingRole));
        Assert.Null(PlanRunnerToolPolicy.Refusal("read_file", null, PlanRunnerToolPolicy.TestRulingRole));
    }
}
