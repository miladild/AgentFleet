using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// The invoker ends a runner round when the edit check gate asks for it, and keeps going when no gate is active.
/// </summary>
[Collection(ContextTestBase.SqliteRecordCollection)]
public sealed class EditCheckInvokerTests : ContextTestBase
{
    private const string Passing = "Exit code: 0\n--- stdout ---\nok";
    private const string Failing = "Exit code: 1\n--- stdout ---\n✖ shop session shop open (1ms)";

    private FleetPlanModeService PlanModeOff()
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """
            { "triageModel": "t", "mode": "conservative", "planModeEnabled": false,
              "nodes": [{ "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "m", "purpose": "", "fallback": true }], "tools": {} }
            """);
        var configStore = new FleetConfigStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = configPath }).Build());
        return new FleetPlanModeService(configStore, RequestContext);
    }

    private FleetFunctionInvoker NewInvoker()
    {
        string plansDir = Path.Combine(Root, "plans-edit-check");
        Directory.CreateDirectory(plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = plansDir }).Build());
        var validator = new FakeValidator(code => new DiagramCheck(true, true, code, null));
        Func<string, string?, CancellationToken, Task<string>> checkCommand = (_, _, _) => Task.FromResult(Passing);
        var tools = new PlanTools(plans, validator, checkCommand);
        return new FleetFunctionInvoker(Journal, PlanModeOff(), plans, new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), tools,
            NullLogger.Instance, 25);
    }

    private sealed class ScriptedModel(int editRounds) : IChatClient
    {
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int round = Requests++;
            ChatMessage reply = round < editRounds
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{round}", "edit_file", new Dictionary<string, object?> { ["path"] = $"file-{round}.ts" })])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private async Task RunRunnerRoundAsync(FleetFunctionInvoker invoker, ScriptedModel model, AITool tool)
    {
        IChatClient client = new ChatClientBuilder(model).UseFunctionInvocation(null, o =>
        {
            o.IncludeDetailedErrors = true;
            o.MaximumIterationsPerRequest = 200;
            o.MaximumConsecutiveErrorsPerRequest = 6;
            o.FunctionInvoker = invoker.InvokeAsync;
        }).Build();

        var options = new ChatOptions
        {
            Tools = [tool],
            AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = "standard" }
        };
        using IDisposable scope = Scope(NewContextId());
        await client.GetResponseAsync("go", options, CancellationToken.None);
    }

    private static EditCheckGate NewGate(string check) =>
        new(new NoFiles(), _ => Task.FromResult(check), output => RoundScore.Of(PlanFailure.FailureSignature(output), output),
            new EditCheckSettings(), _ => { });

    /// <summary>The file side of the gate is not used here: the probe tool only asks the gate about the edit.</summary>
    private sealed class NoFiles : IEditCheckFiles
    {
        public Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public Task WriteBytesAsync(string path, byte[]? contents, CancellationToken cancellationToken) => Task.CompletedTask;

        public string Canonical(string path) => path;
    }

    private static AITool ProbeTool(EditCheckGate gate, List<string> calls) =>
        AIFunctionFactory.Create(async (string path) =>
        {
            calls.Add(path);
            await gate.BeforeMutationAsync(path, CancellationToken.None);
            return await gate.AfterMutationAsync(path, "Wrote " + path, CancellationToken.None);
        }, "edit_file", "Edits a file.");

    [Fact]
    public async Task A_round_ends_after_the_edit_that_makes_every_check_pass()
    {
        EditCheckGate gate = NewGate(Passing);
        var calls = new List<string>();
        var model = new ScriptedModel(editRounds: 2);

        using (EditCheckContext.Push(gate))
        {
            await RunRunnerRoundAsync(NewInvoker(), model, ProbeTool(gate, calls));
        }

        Assert.True(gate.StopRequested);
        Assert.Single(calls);
        Assert.Equal(1, model.Requests);
    }

    [Fact]
    public async Task A_round_keeps_going_when_the_gate_does_not_ask_to_stop()
    {
        EditCheckGate gate = NewGate(Failing);
        var calls = new List<string>();
        var model = new ScriptedModel(editRounds: 2);

        using (EditCheckContext.Push(gate))
        {
            await RunRunnerRoundAsync(NewInvoker(), model, ProbeTool(gate, calls));
        }

        Assert.False(gate.StopRequested);
        Assert.Equal(2, calls.Count);
        Assert.Equal(3, model.Requests);
    }

    [Fact]
    public async Task Without_a_gate_the_round_is_not_ended_by_the_edit_check()
    {
        EditCheckGate gate = NewGate(Passing);
        var calls = new List<string>();
        var model = new ScriptedModel(editRounds: 2);

        await RunRunnerRoundAsync(NewInvoker(), model, ProbeTool(gate, calls));

        Assert.Equal(2, calls.Count);
        Assert.Equal(3, model.Requests);
    }
}
