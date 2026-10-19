using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class TestFirstIntegrationTests : PlanTestBase
{
    [Theory]
    [InlineData("missing")]
    [InlineData("vision")]
    public void Test_writer_must_name_a_configured_text_node_on_load(string writer)
    {
        string path = Path.Combine(PlansDirectory, "fleet.config.json");
        var config = new FleetConfig("t", "conservative", false,
            [new FleetNodeConfig("hub", "http://hub.example.test/v1", "m", "hub", "heavy", Fallback: true),
             new FleetNodeConfig("vision", "http://vision.example.test/v1", "m", "vision", Vision: true)],
            new Dictionary<string, FleetToolConfig>(), Plan: new FleetPlanConfig(writer));
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = path }).Build();
        Assert.Contains("plan.testWriterNode", Assert.Throws<InvalidOperationException>(() => new FleetConfigStore(configuration)).Message);
    }

    [Fact]
    public void Test_writer_config_round_trips_and_can_be_absent()
    {
        string path = Path.Combine(PlansDirectory, "fleet.config.json");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = path }).Build();
        var store = new FleetConfigStore(configuration);
        Assert.Null(store.Current.Plan);
        store.Save(store.Current with { Plan = new FleetPlanConfig("hub") });
        Assert.Equal("hub", new FleetConfigStore(configuration).Current.Plan!.TestWriterNode);
    }

    [Fact]
    public async Task External_proposals_keep_their_shape_while_planner_proposals_are_reviewed()
    {
        var tools = new PlanTools(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)),
            (_, _, _) => Task.FromResult("Exit code: 0"), workerWorkspacesEnabled: true, testFirst: TestFirstMode.On);
        var steps = JsonSerializer.SerializeToElement(new[] { new { title = "Implement limits", detail = "Reject zero", files = new[] { "src/limits.cs" }, verify = "dotnet test" } });
        Assert.Empty(tools.Review(PlansDirectory, steps));
        string rejected = await tools.ProposePlanAsync("Limits", "Reject zero", PlansDirectory, null, null, null, null, steps, default);
        Assert.Contains("earlier tests step", rejected);
        string accepted = await tools.ProposePlanAsync("Limits", "Reject zero", PlansDirectory, null, null, null, null, steps, default, plannerProposed: false);
        Assert.StartsWith("Plan saved", accepted);
        string file = Path.Combine(PlansDirectory, "plan.md");
        File.WriteAllText(file, $"# Limits\n\nGoal: Reject zero\n\nWorking directory: {PlansDirectory}\n\n## Step 1: Implement limits\n- Tier: standard\n- Files: src/limits.cs\n- Check: dotnet test\n\nReject zero\n");
        Assert.StartsWith("Plan saved", await tools.ProposePlanFromFileAsync(file, default));
    }

    [Fact]
    public void Test_writer_has_exactly_the_ordinary_tools_and_refusals()
    {
        foreach (string tool in new[] { "read_file", "write_file", "edit_file", "run_command", "report_blocker", "propose_check", "http_request", "web_search" })
        {
            Assert.Equal(PlanRunnerToolPolicy.Offers(tool, true, null), PlanRunnerToolPolicy.Offers(tool, true, PlanRunnerToolPolicy.TestWriterRole));
            Assert.Equal(PlanRunnerToolPolicy.Refusal(tool, "dotnet test"), PlanRunnerToolPolicy.Refusal(tool, "dotnet test", PlanRunnerToolPolicy.TestWriterRole));
        }
    }

    [Fact]
    public async Task Test_writer_still_gets_the_ordinary_work_nudge()
    {
        var client = new TextOnlyClient();
        await new FleetStepAgent(new Microsoft.Agents.AI.ChatClientAgent(client))
            .OpenSession(PlanRunnerToolPolicy.TestWriterRole).SendAsync("Write tests", "standard", "writer", default);
        Assert.Equal(2, client.Calls);
        Assert.Equal(FleetStepAgent.Nudge, client.LastMessage);
    }

    private sealed class TextOnlyClient : IChatClient
    {
        public int Calls { get; private set; }
        public string? LastMessage { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastMessage = messages.Last().Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Wrote the tests.")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
