using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class WorkerPlatformPipelineTests
{
    [Theory]
    [InlineData("linux", false)]
    [InlineData("windows", false)]
    [InlineData("linux", true)]
    [InlineData("windows", true)]
    public async Task The_agent_sends_worker_instructions_inside_the_scope_and_hub_instructions_afterwards(string platform, bool streaming)
    {
        var recorder = new RecordingClient();
        using IChatClient pipeline = new ChatClientBuilder(recorder)
            .Use(inner => new WorkerPlatformChatClient(inner))
            .UseFunctionInvocation()
            .Build();
        var agent = new ChatClientAgent(pipeline, instructions: "You are a local coding assistant.\n\n" + HubPlatform.PathInstructions);
        var config = new FleetWorkerWorkspaceConfig("worker.example.test", "fleet-test", "C:/fleet-test/key", "SHA256:test",
            platform == "linux" ? "/fleet-test/workspace" : "C:/fleet-test/workspace", platform);
        using var worker = new WorkerWorkspaceSession("worker-test", config, "C:/fleet-test/project", NullLogger.Instance, TimeSpan.FromSeconds(1));
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            AdditionalProperties = new() { [FleetRoutingChatClient.RunnerTierKey] = "light" }
        });

        Assert.Null(WorkerWorkspaceContext.Current);
        using (worker.Enter())
        {
            await Task.Yield();
            await RunAsync(agent, streaming, runOptions);

            RecordingClient.Request request = Assert.Single(recorder.Requests);
            Assert.Equal(platform, request.Platform);
            Assert.Contains(platform == "linux" ? "Linux worker" : "Windows worker", request.Instructions);
            Assert.DoesNotContain(HubPlatform.PathInstructions, request.Instructions);
            Assert.Contains("Use project-relative paths", request.Instructions);
            Assert.Equal("light", request.Tier);
            Assert.Contains(HubPlatform.PathInstructions, agent.Instructions);
        }

        Assert.Null(WorkerWorkspaceContext.Current);
        await RunAsync(agent, streaming, null);

        Assert.Equal(2, recorder.Requests.Count);
        Assert.Null(recorder.Requests[1].Platform);
        Assert.Contains(HubPlatform.PathInstructions, recorder.Requests[1].Instructions);
        Assert.DoesNotContain("During this plan step", recorder.Requests[1].Instructions);
    }

    private static async Task RunAsync(ChatClientAgent agent, bool streaming, ChatClientAgentRunOptions? options)
    {
        AgentSession session = await agent.CreateSessionAsync();
        if (streaming)
        {
            await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync("Inspect the project.", session, options))
            {
            }
        }
        else
        {
            await agent.RunAsync("Inspect the project.", session, options);
        }
    }

    private sealed class RecordingClient : IChatClient
    {
        internal sealed record Request(string Instructions, string? Platform, string? Tier);
        public List<Request> Requests { get; } = [];

        private void Record(IEnumerable<ChatMessage> messages, ChatOptions? options) => Requests.Add(new Request(
            string.Join('\n', messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text)) + "\n" + options?.Instructions,
            WorkerWorkspaceContext.Current?.Platform,
            options?.AdditionalProperties?.TryGetValue(FleetRoutingChatClient.RunnerTierKey, out object? tier) == true ? tier?.ToString() : null));

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Record(messages, options);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Record(messages, options);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Done.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
