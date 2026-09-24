using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class FleetRoutingPromptTests
{
    private static readonly Dictionary<string, string> ThreeTiers = new()
    {
        [FleetTiers.Heavy] = "big",
        [FleetTiers.Standard] = "mid",
        [FleetTiers.Light] = "tiny"
    };

    [Fact]
    public void Conservative_prompt_describes_every_tier_with_the_configured_names()
    {
        string prompt = FleetRoutingPrompt.BuildInstructions(FleetMode.Conservative, ThreeTiers);

        Assert.Contains("three-node fleet", prompt);
        Assert.Contains("- big: complex", prompt);
        Assert.Contains("- mid: an ordinary", prompt);
        Assert.Contains("- tiny: a trivial", prompt);
        Assert.Contains("Can you build Blazor apps? -> mid", prompt);
        Assert.Contains("\"hey\" / \"how's your day?\" -> tiny", prompt);
        Assert.Contains("Design a microservice architecture for an e-commerce checkout system -> big", prompt);
        Assert.Contains("When in doubt between mid and tiny, prefer mid", prompt);
        Assert.EndsWith("Never claim to call tools.", prompt);
    }

    [Fact]
    public void Aggressive_prompt_prefers_the_heavy_node()
    {
        string prompt = FleetRoutingPrompt.BuildInstructions(FleetMode.Aggressive, ThreeTiers);

        Assert.Contains("The big machine's GPU is currently free", prompt);
        Assert.Contains("Can you build Blazor apps? -> big", prompt);
        Assert.Contains("Refactor this function to remove duplicate error handling -> big", prompt);
        Assert.Contains("What does the % operator do in Python? -> tiny", prompt);
        Assert.Contains("When in doubt, prefer big", prompt);
    }

    [Fact]
    public void A_missing_tier_sends_its_work_to_the_nearest_existing_one()
    {
        var noStandard = new Dictionary<string, string>
        {
            [FleetTiers.Heavy] = "big",
            [FleetTiers.Light] = "tiny"
        };

        string prompt = FleetRoutingPrompt.BuildInstructions(FleetMode.Conservative, noStandard);

        Assert.Contains("two-node fleet", prompt);
        Assert.DoesNotContain("mid", prompt);
        Assert.Contains("Can you build Blazor apps? -> big", prompt);
        Assert.Contains("When in doubt, prefer big - tiny is reserved", prompt);
    }

    [Fact]
    public void Aggressive_mode_without_a_heavy_node_behaves_like_conservative()
    {
        var noHeavy = new Dictionary<string, string>
        {
            [FleetTiers.Standard] = "mid",
            [FleetTiers.Light] = "tiny"
        };

        string prompt = FleetRoutingPrompt.BuildInstructions(FleetMode.Aggressive, noHeavy);

        Assert.DoesNotContain("GPU is currently free", prompt);
        Assert.Contains("Can you build Blazor apps? -> mid", prompt);
    }

    [Theory]
    [InlineData(FleetTiers.Heavy, "big")]
    [InlineData(FleetTiers.Standard, "mid")]
    [InlineData(FleetTiers.Light, "tiny")]
    public void ResolveTier_returns_the_node_for_a_present_tier(string tier, string expected) =>
        Assert.Equal(expected, FleetRoutingPrompt.ResolveTier(tier, ThreeTiers));

    [Fact]
    public void ResolveTier_falls_back_in_preference_order()
    {
        Assert.Equal("big", FleetRoutingPrompt.ResolveTier(FleetTiers.Standard, new Dictionary<string, string> { [FleetTiers.Heavy] = "big", [FleetTiers.Light] = "tiny" }));
        Assert.Equal("mid", FleetRoutingPrompt.ResolveTier(FleetTiers.Light, new Dictionary<string, string> { [FleetTiers.Heavy] = "big", [FleetTiers.Standard] = "mid" }));
        Assert.Equal("tiny", FleetRoutingPrompt.ResolveTier(FleetTiers.Heavy, new Dictionary<string, string> { [FleetTiers.Light] = "tiny" }));
    }

    [Fact]
    public void Schema_constrains_the_answer_to_the_given_node_names()
    {
        JsonElement schema = FleetRoutingPrompt.BuildSchema(["a", "b-2"]);

        string[] allowed = schema.GetProperty("properties").GetProperty("node").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["a", "b-2"], allowed);
        Assert.Equal("node", schema.GetProperty("required")[0].GetString());
    }
}

public sealed class ParseRouteTests
{
    private static readonly HashSet<string> Allowed = ["big", "mid", "tiny"];

    [Theory]
    [InlineData("{\"node\":\"mid\"}", "mid")]
    [InlineData("{\"node\": \" tiny \"}", "tiny")]
    [InlineData("big", "big")]
    public void Valid_answers_are_used(string text, string expected) =>
        Assert.Equal(expected, FleetRoutingChatClient.ParseRoute(text, Allowed, "big"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"node\":\"somewhere-else\"}")]
    [InlineData("{\"other\":1}")]
    [InlineData("Sure! Here is a long answer to your question.")]
    public void Anything_else_falls_back_to_the_fallback_node(string? text) =>
        Assert.Equal("mid", FleetRoutingChatClient.ParseRoute(text, Allowed, "mid"));
}

public sealed class ToolCallRescueTests
{
    [Fact]
    public void A_leaked_function_block_becomes_a_real_call()
    {
        const string leaked = "<function=web_search>\n<parameter=query>\nReqnroll docs\n</parameter>\n<parameter=maxResults>\n5\n</parameter>\n</function>\n</tool_call>";

        FunctionCallContent? call = ToolCallRescueChatClient.TryRescue(leaked);

        Assert.NotNull(call);
        Assert.Equal("web_search", call!.Name);
        Assert.Equal("Reqnroll docs", call.Arguments!["query"]);
        Assert.Equal("5", call.Arguments["maxResults"]);
        Assert.StartsWith("rescued-", call.CallId);
    }

    [Fact]
    public void Prose_before_the_block_and_a_missing_closing_tag_are_tolerated()
    {
        FunctionCallContent? call = ToolCallRescueChatClient.TryRescue(
            "I'll look that up.\n\n<function=list_directory>\n<parameter=path>\nC:\\work\n");

        Assert.NotNull(call);
        Assert.Equal("list_directory", call!.Name);
        Assert.Equal("C:\\work", call.Arguments!["path"]);
    }

    [Fact]
    public void A_multi_line_parameter_value_is_kept_whole()
    {
        FunctionCallContent? call = ToolCallRescueChatClient.TryRescue(
            "<function=write_file>\n<parameter=path>\na.txt\n</parameter>\n<parameter=content>\nline1\nline2\n</parameter>\n</function>");

        Assert.Equal("line1\nline2", call!.Arguments!["content"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Just an ordinary answer with no tool call.")]
    [InlineData("<function= not a real block")]
    public void Ordinary_text_is_left_alone(string text) =>
        Assert.Null(ToolCallRescueChatClient.TryRescue(text));
}
