using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A worker that runs its tests again and again, written a different way each time, while every run hits the runner's time limit
/// (or crashes at exit, or leaves a server open) is told so on the second run and has its turn ended on the third. The command
/// text does not matter; the kind of result does.
/// </summary>
[Collection(ContextTestBase.SqliteRecordCollection)]
public sealed class StuckGuardInvokerTests : ContextTestBase
{
    private const string TimedOut = "Error: command exceeded the 120s timeout and was killed.\n--- partial stdout ---\nok 1 - first\n--- partial stderr ---\n";

    private static readonly string[] ThreeWaysToRunTests =
    [
        "node --test a.test.js",
        "node --test --test-timeout=30000 a.test.js 2>&1 | tail -20",
        "npm test"
    ];

    private FleetFunctionInvoker NewInvoker(HarnessOptions? harness)
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """
            { "triageModel": "t", "mode": "conservative", "planModeEnabled": false,
              "nodes": [{ "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "m", "purpose": "", "fallback": true }], "tools": {} }
            """);
        var configStore = new FleetConfigStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = configPath }).Build());
        string plansDir = Path.Combine(Root, "plans-stuck-guard");
        Directory.CreateDirectory(plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = plansDir }).Build());
        var tools = new PlanTools(plans, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (_, _, _) => Task.FromResult("ok"));
        return new FleetFunctionInvoker(Journal, new FleetPlanModeService(configStore, RequestContext), plans,
            new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), tools, NullLogger.Instance, 25, harness: harness);
    }

    /// <summary>Calls run_command with each given command in turn, one per request, then answers.</summary>
    private sealed class ScriptedModel(params string[] commands) : IChatClient
    {
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int round = Requests++;
            ChatMessage reply = round < commands.Length
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{round}", "run_command", new Dictionary<string, object?> { ["command"] = commands[round] })])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private async Task<(List<string> Results, int Requests, StuckStepGuard Guard)> RunAsync(HarnessOptions? harness, Func<string, string> script, params string[] commands)
    {
        var guard = new StuckStepGuard(harness ?? new HarnessOptions());
        AITool runCommand = AIFunctionFactory.Create(string (string command, string? workingDirectory = null) => script(command), "run_command", "Runs a command.");
        var model = new ScriptedModel(commands);
        IChatClient client = new ChatClientBuilder(model).UseFunctionInvocation(null, options =>
        {
            options.IncludeDetailedErrors = true;
            options.MaximumIterationsPerRequest = 200;
            options.MaximumConsecutiveErrorsPerRequest = 6;
            options.FunctionInvoker = NewInvoker(harness).InvokeAsync;
        }).Build();
        var activity = new StepToolActivity
        {
            StuckGuard = (family, outcome, output) => guard.Observe("plan-1", 1, 1, family, outcome, output)
        };
        var chatOptions = new ChatOptions
        {
            Tools = [runCommand],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [FleetRoutingChatClient.RunnerTierKey] = "standard",
                [StepToolActivity.Key] = activity
            }
        };
        using IDisposable scope = Scope(NewContextId());
        ChatResponse response = await client.GetResponseAsync("go", chatOptions, CancellationToken.None);
        var results = response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => result.Result?.ToString() ?? string.Empty).ToList();
        return (results, model.Requests, guard);
    }

    [Fact]
    public async Task Three_differently_written_test_commands_that_hit_the_time_limit_get_a_note_on_the_second_and_end_the_turn_on_the_third()
    {
        (List<string> results, int requests, _) = await RunAsync(null, _ => TimedOut, [.. ThreeWaysToRunTests, "node --test a.test.js"]);

        Assert.Equal(3, results.Count);
        Assert.Equal(TimedOut, results[0]);
        Assert.StartsWith(TimedOut, results[1], StringComparison.Ordinal);
        Assert.Contains("hit the time limit 2 times in a row", results[1], StringComparison.Ordinal);
        Assert.DoesNotContain("round ends here", results[1], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TimedOut, results[2], StringComparison.Ordinal);
        Assert.Contains("hit the time limit 3 times in a row", results[2], StringComparison.Ordinal);
        Assert.Contains("round ends here", results[2], StringComparison.OrdinalIgnoreCase);
        // The third result ended the turn: the model was asked three times, never a fourth.
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task A_build_command_that_hits_the_time_limit_is_not_counted()
    {
        (List<string> results, int requests, StuckStepGuard guard) = await RunAsync(null, _ => TimedOut, "npm run build", "tsc -p .", "dotnet build");

        Assert.All(results, result => Assert.Equal(TimedOut, result));
        Assert.Equal(4, requests);
        Assert.Null(guard.Streak("plan-1", 1, 1));
    }

    [Fact]
    public async Task A_test_run_that_passes_between_two_time_limits_starts_the_count_again()
    {
        int run = 0;
        (List<string> results, int requests, _) = await RunAsync(null, _ => ++run == 2 ? "Exit code: 0\n--- stdout ---\nok" : TimedOut, ThreeWaysToRunTests);

        Assert.Equal(TimedOut, results[0]);
        Assert.Equal("Exit code: 0\n--- stdout ---\nok", results[1]);
        Assert.Equal(TimedOut, results[2]);
        Assert.Equal(4, requests);
    }

    [Fact]
    public async Task Test_runs_that_crash_at_exit_are_counted_as_a_crash()
    {
        const string crash = "Exit code: -1073740791\n--- stdout ---\nok 1 - first\n--- stderr ---\nAssertion failed: !(handle->flags & UV_HANDLE_CLOSING)";

        (List<string> results, _, _) = await RunAsync(null, _ => crash, "node --test a.test.js", "npm test");

        Assert.Equal(crash, results[0]);
        Assert.Contains("crashed at exit 2 times in a row", results[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_failing_tests_are_left_to_the_other_guards()
    {
        const string failing = "Exit code: 1\n--- stdout ---\nnot ok 1 - first\n";

        (List<string> results, int requests, _) = await RunAsync(null, _ => failing, [.. ThreeWaysToRunTests, "npm run test"]);

        Assert.All(results, result => Assert.Equal(failing, result));
        Assert.Equal(5, requests);
    }

    [Fact]
    public async Task A_command_the_worker_machine_kills_at_the_time_limit_is_counted_the_same_way()
    {
        // On a worker the runner's time limit is an exception from the command, not a result.
        string Script(string command) => throw new WorkerCommandTimeoutException("Operation has timed out.",
            new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out."), "ok 1 - first");

        (List<string> results, int requests, _) = await RunAsync(null, Script, [.. ThreeWaysToRunTests, "node --test a.test.js"]);

        Assert.Equal(3, results.Count);
        Assert.Contains("Operation has timed out.", results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("times in a row", results[0], StringComparison.Ordinal);
        Assert.Contains("hit the time limit 2 times in a row", results[1], StringComparison.Ordinal);
        Assert.Contains("hit the time limit 3 times in a row", results[2], StringComparison.Ordinal);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task With_the_guard_switched_off_the_results_are_the_commands_own()
    {
        (List<string> results, int requests, _) = await RunAsync(new HarnessOptions(StuckGuardEnabled: false), _ => TimedOut, [.. ThreeWaysToRunTests, "node --test a.test.js"]);

        Assert.All(results, result => Assert.Equal(TimedOut, result));
        Assert.Equal(5, requests);
    }

    [Fact]
    public async Task A_step_with_no_guard_attached_runs_as_before()
    {
        AITool runCommand = AIFunctionFactory.Create(string (string command, string? workingDirectory = null) => TimedOut, "run_command", "Runs a command.");
        var model = new ScriptedModel(ThreeWaysToRunTests);
        IChatClient client = new ChatClientBuilder(model).UseFunctionInvocation(null, options =>
        {
            options.IncludeDetailedErrors = true;
            options.FunctionInvoker = NewInvoker(null).InvokeAsync;
        }).Build();
        var chatOptions = new ChatOptions
        {
            Tools = [runCommand],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [FleetRoutingChatClient.RunnerTierKey] = "standard",
                [StepToolActivity.Key] = new StepToolActivity()
            }
        };
        using IDisposable scope = Scope(NewContextId());

        ChatResponse response = await client.GetResponseAsync("go", chatOptions, CancellationToken.None);

        string[] results = response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => result.Result?.ToString() ?? string.Empty).ToArray();
        Assert.All(results, result => Assert.Equal(TimedOut, result));
        Assert.Equal(4, model.Requests);
    }
}
