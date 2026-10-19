using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class PlanPromptGoldenTests
{
    public static IEnumerable<object[]> Cases => new[]
    {
        "first", "retry", "parallel", "rescue", "windows", "linux", "guidance",
        "durable", "files", "brief", "earlier", "no-check", "resumed", "combined", "chat"
    }.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Model_messages_match_the_original_bytes(string name)
    {
        var client = new RecordingClient();
        var agent = new ChatClientAgent(client, instructions: "Synthetic instructions.");
        if (name == "chat")
        {
            AIAgent chain = agent.AsBuilder().UseAIContextProviders(new PlanEnvironmentContextProvider(),
                new PlanFilesContextProvider(), new PlanContinuationContextProvider()).Build();
            AgentSession session = await chain.CreateSessionAsync();
            await chain.RunAsync("Inspect the project.", session);
            await chain.RunAsync("Explain the result.", session);
        }
        else
        {
            var input = Scenario(name);
            IStepSession session = new FleetStepSession(agent, "probe");
            session.ConfigurePrompt(input.Data());
            await session.SendAsync(input.Prompt(), "standard", "worker.example.test", default);
            await session.SendAsync("The check failed. Fix the assertion.", "standard", "worker.example.test", default);
        }

        string actual = JsonSerializer.Serialize(client.Requests);
        using Stream stream = typeof(PlanPromptGoldenTests).Assembly.GetManifestResourceStream(
            $"AgentFleet.Tests.PromptGoldens.{name}.json")!;
        using var reader = new StreamReader(stream);
        string expected = await reader.ReadToEndAsync();
        if (!OperatingSystem.IsWindows())
        {
            // Only builder-generated CRLFs vary by host; embedded project data uses explicit LF.
            expected = expected.Replace("\\r\\n", "\\n", StringComparison.Ordinal);
        }
        Assert.Equal(expected, actual);
    }

    internal sealed record Input(PlanRecord Plan, PlanStep Step, int Attempt = 1,
        string? LastFailure = null, string? FileContext = null, bool ParallelGroup = false,
        bool DurableContext = false, string? RetryRoute = null, bool WorkerWorkspace = true,
        string? WorkerMachine = "worker.example.test", string? WorkerPlatform = "linux",
        string? Brief = null, bool StepGuidance = false)
    {
        public PlanStepPromptData Data() => new(Plan, Step, Attempt, LastFailure, FileContext,
            ParallelGroup, DurableContext, RetryRoute, WorkerWorkspace, WorkerMachine, WorkerPlatform, Brief, StepGuidance);
        public string Prompt() => PlanRunner.BuildPrompt(Plan, Step, Attempt, LastFailure, FileContext,
            ParallelGroup, DurableContext, RetryRoute, WorkerWorkspace, WorkerMachine, WorkerPlatform, Brief, StepGuidance);
    }

    internal static Input Scenario(string name)
    {
        var step = new PlanStep(3, "Add the guard", "Reject empty input.", ["src/guard.ts", "test/guard.test.ts"],
            "node --test", "standard", StepStatus.Pending, null, 0, null, null);
        var earlier = new PlanStep(1, "Define input", "", [], null, "standard", StepStatus.Done,
            "Added the interface.", 1, null, null);
        var second = earlier with { Id = 2, Title = "Add parser", Note = "   " };
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var plan = new PlanRecord("synthetic-plan", "Guard input", "Reject invalid values", "approved",
            "C:/fleet-test/project", ["Keep the existing API.", "Use built-in tools."], [], [], null, null,
            [earlier with { Status = StepStatus.Pending }, second with { Status = StepStatus.Pending }, step], now, now, now);
        var input = new Input(plan, step);
        return name switch
        {
            "retry" => input with { Attempt = 2, LastFailure = "Exit code: 1\nexpected 3 got 2" },
            "resumed" => input with { LastFailure = "Error: command timeout" },
            "parallel" => input with { ParallelGroup = true },
            "rescue" => input with { RetryRoute = "The worker could not finish. Continue on the selected rescue route." },
            "windows" => input with { WorkerPlatform = "windows" },
            "guidance" => input with { StepGuidance = true },
            "durable" => input with { DurableContext = true },
            "files" => input with { FileContext = "--- BEGIN UNTRUSTED FILE: src/guard.ts ---\nexport const guard = () => true;\n--- END UNTRUSTED FILE: src/guard.ts ---" },
            "brief" => input with { Brief = "Earlier rounds: kept the parser.\nRead the changed files.\n" },
            "earlier" => input with { Plan = plan with { Steps = [earlier, second, step] } },
            "no-check" => input with { Step = step with { Verify = null, Detail = "", Files = [] } },
            "combined" => input with { Plan = plan with { Steps = [earlier, second, step] }, Attempt = 3,
                LastFailure = "Error: test/guard.test.ts:7:2\nexpected valid input", FileContext = "file bytes\n\n",
                ParallelGroup = true, DurableContext = true, RetryRoute = "Continue the rescue attempt.",
                WorkerPlatform = "windows", Brief = "Prior round notes without a final newline.", StepGuidance = true },
            _ => input
        };
    }

    internal sealed record Message(string Role, string Text);
    internal sealed record Request(Message[] Messages, string? Instructions);
    internal sealed class RecordingClient : IChatClient
    {
        public List<Request> Requests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new Request(messages.Select(message => new Message(message.Role.Value, message.Text ?? "")).ToArray(), options?.Instructions));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
