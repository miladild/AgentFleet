using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A plan step's model that tries to change the project's dependencies is told no in the tool's result and the round goes on,
/// so it can do the work another way; with the guard switched off the call runs as before.
/// </summary>
[Collection(ContextTestBase.SqliteRecordCollection)]
public sealed class FileGuardInvokerTests : ContextTestBase
{
    private FleetFunctionInvoker NewInvoker(HarnessOptions? harness)
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """
            { "triageModel": "t", "mode": "conservative", "planModeEnabled": false,
              "nodes": [{ "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "m", "purpose": "", "fallback": true }], "tools": {} }
            """);
        var configStore = new FleetConfigStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = configPath }).Build());
        string plansDir = Path.Combine(Root, "plans-file-guard");
        Directory.CreateDirectory(plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = plansDir }).Build());
        var tools = new PlanTools(plans, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (_, _, _) => Task.FromResult("ok"));
        return new FleetFunctionInvoker(Journal, new FleetPlanModeService(configStore, RequestContext), plans,
            new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), tools, NullLogger.Instance, 25, harness: harness);
    }

    /// <summary>Calls one tool with the arguments of each scripted call in turn, then answers.</summary>
    private sealed class ScriptedModel(string tool, params Dictionary<string, object?>[] calls) : IChatClient
    {
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int round = Requests++;
            ChatMessage reply = round < calls.Length
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{round}", tool, calls[round])])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private async Task<(List<string> Ran, List<string> Results, int Requests)> RunRoundAsync(HarnessOptions? harness, string tool, string argument, params string[] values)
    {
        var ran = new List<string>();
        // The tool takes its argument under the name the real tool does: path for a file tool, command for run_command.
        AITool real = tool == "run_command"
            ? AIFunctionFactory.Create(string (string command) => { ran.Add(command); return "ok"; }, tool, "A tool.")
            : AIFunctionFactory.Create(string (string path) => { ran.Add(path); return "ok"; }, tool, "A tool.");
        var model = new ScriptedModel(tool, values.Select(value => new Dictionary<string, object?> { [argument] = value }).ToArray());
        IChatClient client = new ChatClientBuilder(model).UseFunctionInvocation(null, options =>
        {
            options.IncludeDetailedErrors = true;
            options.MaximumIterationsPerRequest = 200;
            options.FunctionInvoker = NewInvoker(harness).InvokeAsync;
        }).Build();
        var chatOptions = new ChatOptions
        {
            Tools = [real],
            AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = "standard" }
        };
        using IDisposable scope = Scope(NewContextId());
        ChatResponse response = await client.GetResponseAsync("go", chatOptions, CancellationToken.None);
        var results = response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? string.Empty).ToList();
        return (ran, results, model.Requests);
    }

    [Fact]
    public async Task A_write_to_package_json_is_denied_in_the_result_and_the_round_goes_on_to_the_next_call()
    {
        (List<string> ran, List<string> results, int requests) = await RunRoundAsync(null, "edit_file", "path", "package.json", "src/a.ts");

        Assert.Equal(["src/a.ts"], ran);
        Assert.StartsWith("DENIED: package.json", results[0], StringComparison.Ordinal);
        Assert.Equal("ok", results[1]);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task An_install_command_is_denied_in_the_result_and_the_round_goes_on()
    {
        (List<string> ran, List<string> results, int requests) = await RunRoundAsync(null, "run_command", "command", "npm install tsx@latest", "npm test");

        Assert.Equal(["npm test"], ran);
        Assert.StartsWith("DENIED: this command installs or changes packages", results[0], StringComparison.Ordinal);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task With_the_guard_switched_off_the_call_runs_as_before()
    {
        (List<string> ran, _, _) = await RunRoundAsync(new HarnessOptions(EnableFileGuard: false), "edit_file", "path", "package.json");

        Assert.Equal(["package.json"], ran);
    }
}
