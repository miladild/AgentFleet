using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class PlanGuardTests
{
    private static readonly IReadOnlySet<string> ReadOnly = new HashSet<string> { "read_file", "search_files", "docs_search" };

    private static readonly PlanGateResult Planning = new(PlanPhase.Planning, null);

    private static ChatResponseUpdate Call(params string[] names) =>
        new(ChatRole.Assistant, names.Select(name => (AIContent)new FunctionCallContent("id-" + name, name, new Dictionary<string, object?> { ["path"] = "x" })).ToList());

    private static string[] Names(ChatResponseUpdate update) =>
        update.Contents.OfType<FunctionCallContent>().Select(call => call.Name).ToArray();

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("run_command")]
    [InlineData("run_git_command")]
    [InlineData("run_sandboxed_code")]
    [InlineData("complete_step")]
    [InlineData("some-mcp_delete_everything")]
    public void While_planning_a_call_to_a_tool_that_was_never_allowed_is_rewritten_to_the_blocked_stand_in(string tool)
    {
        var blocked = new List<string>();

        ChatResponseUpdate guarded = PlanGate.GuardCalls(Call(tool), Planning, ReadOnly, blocked.Add);

        FunctionCallContent call = Assert.Single(guarded.Contents.OfType<FunctionCallContent>());
        Assert.Equal(PlanGate.BlockedToolName, call.Name);
        Assert.Equal(tool, call.Arguments!["tool"]);
        Assert.Equal("id-" + tool, call.CallId);
        Assert.Equal([tool], blocked);
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("search_files")]
    [InlineData("docs_search")]
    [InlineData("propose_plan")]
    public void While_planning_read_only_tools_and_propose_plan_pass_through_untouched(string tool)
    {
        ChatResponseUpdate update = Call(tool);

        ChatResponseUpdate guarded = PlanGate.GuardCalls(update, Planning, ReadOnly);

        Assert.Same(update, guarded);
        Assert.Equal([tool], Names(guarded));
    }

    [Fact]
    public void A_mixed_update_blocks_only_the_calls_that_are_not_allowed_and_keeps_text()
    {
        var update = new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent("Let me look."),
            new FunctionCallContent("a", "read_file"),
            new FunctionCallContent("b", "write_file")
        });

        ChatResponseUpdate guarded = PlanGate.GuardCalls(update, Planning, ReadOnly);

        Assert.Equal(["read_file", PlanGate.BlockedToolName], Names(guarded));
        Assert.Equal("Let me look.", guarded.Text);
    }

    [Fact]
    public void With_plan_mode_off_nothing_is_rewritten()
    {
        ChatResponseUpdate update = Call("write_file", "run_command");

        ChatResponseUpdate guarded = PlanGate.GuardCalls(update, new PlanGateResult(PlanPhase.Off, null), ReadOnly);

        Assert.Equal(["write_file", "run_command"], Names(guarded));
    }

    [Fact]
    public void While_a_plan_runs_chat_may_only_read_and_check_progress_so_work_is_never_done_twice()
    {
        var executing = new PlanGateResult(PlanPhase.Executing, null);

        ChatResponseUpdate guarded = PlanGate.GuardCalls(Call("write_file", "get_plan", "read_file", "propose_plan"), executing, ReadOnly);

        Assert.Equal([PlanGate.BlockedToolName, "get_plan", "read_file", PlanGate.BlockedToolName], Names(guarded));
    }

    [Fact]
    public void An_update_without_calls_is_returned_as_is_and_a_blocked_call_is_not_rewritten_twice()
    {
        var text = new ChatResponseUpdate(ChatRole.Assistant, "just text");
        Assert.Same(text, PlanGate.GuardCalls(text, Planning, ReadOnly));

        ChatResponseUpdate already = Call(PlanGate.BlockedToolName);
        ChatResponseUpdate guarded = PlanGate.GuardCalls(already, Planning, ReadOnly);
        Assert.Equal([PlanGate.BlockedToolName], Names(guarded));
    }

    [Fact]
    public void The_blocked_message_says_what_happened_and_what_to_do_instead()
    {
        string message = PlanGate.BlockedMessage("write_file");

        Assert.StartsWith("Blocked: write_file", message);
        Assert.Contains("nothing was changed", message);
        Assert.Contains("propose_plan", message);
    }

    [Fact]
    public void The_stand_in_is_never_offered_to_the_model_in_any_phase()
    {
        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(() => "x", name: "read_file"),
                AIFunctionFactory.Create(() => "x", name: "propose_plan"),
                AIFunctionFactory.Create(() => "x", name: PlanGate.BlockedToolName)
            ]
        };

        foreach (PlanPhase phase in Enum.GetValues<PlanPhase>())
        {
            string[] offered = PlanGate.FilterTools(options, phase, ReadOnly)!.Tools!.Select(tool => tool.Name).ToArray();
            Assert.DoesNotContain(PlanGate.BlockedToolName, offered);
        }
    }
}
