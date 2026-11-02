using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

// The agent is the framework's HarnessAgent: these tests hold that the model sees what the fleet gives it and nothing the harness
// would add by default, that a tool call passes the fleet's own invocation hook exactly once, and that the runner's request
// properties (tier, machine, role) still reach the routing client.
public sealed class HarnessAgentPipelineTests
{
    private sealed class Model : IChatClient
    {
        public List<(string? Instructions, string[] Tools, object? Tier)> Seen { get; } = [];
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            object? tier = null;
            options?.AdditionalProperties?.TryGetValue(FleetRoutingChatClient.RunnerTierKey, out tier);
            Seen.Add((options?.Instructions, options?.Tools?.Select(tool => tool.Name).ToArray() ?? [], tier));
            return Task.FromResult(_calls++ % 2 == 0
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent($"c{_calls}", "read_file", new Dictionary<string, object?> { ["path"] = "a.txt" })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static (AIAgent Agent, Model Model, Func<int> HookCalls) Build()
    {
        var model = new Model();
        int hookCalls = 0;
        IChatClient agentClient = new ChatClientBuilder(model)
            .UseFunctionInvocation(configure: invocation => invocation.FunctionInvoker = (context, token) =>
            {
                hookCalls++;
                return context.Function.InvokeAsync(context.Arguments, token);
            })
            .Build();
        AIFunction readFile = AIFunctionFactory.Create((string path) => $"contents of {path}", "read_file");
        AIAgent agent = FleetHarness.BuildAgent(agentClient, "OUR INSTRUCTIONS", [readFile], [new MachinePathContextProvider()]);
        return (agent, model, () => hookCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_model_sees_only_the_fleets_instructions_and_tools_and_a_tool_call_passes_the_hook_once(bool streaming)
    {
        (AIAgent agent, Model model, Func<int> hookCalls) = Build();
        AgentSession session = await agent.CreateSessionAsync();

        if (streaming)
        {
            await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("do it", session)) { }
        }
        else
        {
            Assert.Equal("done", (await agent.RunAsync("do it", session)).Text);
        }

        Assert.Equal(1, hookCalls());
        Assert.Equal(2, model.Seen.Count);
        Assert.All(model.Seen, seen =>
        {
            Assert.Equal("OUR INSTRUCTIONS\n" + HubPlatform.PathInstructions, seen.Instructions?.Replace("\r\n", "\n"));
            Assert.Equal(["read_file"], seen.Tools);
        });
    }

    [Fact]
    public async Task The_runners_request_properties_reach_the_chat_client_through_a_step_session()
    {
        (AIAgent agent, Model model, _) = Build();
        AIAgent stepAgent = agent.AsBuilder().UseAIContextProviders(new PlanEnvironmentContextProvider()).Build();
        AgentSession session = await stepAgent.CreateSessionAsync();
        var options = new ChatClientAgentRunOptions(new ChatOptions
        {
            AdditionalProperties = new() { [FleetRoutingChatClient.RunnerTierKey] = "standard" }
        });

        await stepAgent.RunAsync("do it", session, options);

        Assert.All(model.Seen, seen => Assert.Equal("standard", seen.Tier));
    }
}
