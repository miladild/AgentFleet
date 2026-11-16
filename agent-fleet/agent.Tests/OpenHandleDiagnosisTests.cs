using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A node test run that ends because something stays open after its tests is explained to the worker once, counted by the
/// stuck-step guard like any other infrastructure result, and never taken as a pass. The commands that run without the force-exit
/// flag carry the preload that names what is open.
/// </summary>
[Collection(ContextTestBase.SqliteRecordCollection)]
public sealed class OpenHandleInvokerTests : ContextTestBase
{
    // What the hub gives back for a command that was ended because its preload reported an open handle.
    private const string EndedEarly =
        "Error: the command was ended early because it showed a stop line (see the output below).\n" +
        "--- partial stdout ---\n✔ first test (2.5ms)\n✔ second test (0.4ms)\n\n[open-after-tests] TCPServerWrap\n\n--- partial stderr ---\n";

    private const string Crash = "Exit code: 1\n--- stderr ---\nAssertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\\win\\async.c, line 94";

    private FleetFunctionInvoker NewInvoker()
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """
            { "triageModel": "t", "mode": "conservative", "planModeEnabled": false,
              "nodes": [{ "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "m", "purpose": "", "fallback": true }], "tools": {} }
            """);
        var configStore = new FleetConfigStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = configPath }).Build());
        string plansDir = Path.Combine(Root, "plans-open-handle");
        Directory.CreateDirectory(plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = plansDir }).Build());
        var tools = new PlanTools(plans, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (_, _, _) => Task.FromResult("ok"));
        return new FleetFunctionInvoker(Journal, new FleetPlanModeService(configStore, RequestContext), plans,
            new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), tools, NullLogger.Instance, 25);
    }

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

    // Runs the commands one per request as a plan step's worker. "ran" holds what the tool was really given, in order.
    private async Task<(List<string> Results, List<string> Ran, int Requests)> RunAsync(Func<string, int, string> script, params string[] commands)
    {
        var ran = new List<string>();
        var guard = new StuckStepGuard(new HarnessOptions());
        AITool runCommand = AIFunctionFactory.Create(string (string command, string? workingDirectory = null) =>
        {
            ran.Add(command);
            return script(command, ran.Count);
        }, "run_command", "Runs a command.");
        var model = new ScriptedModel(commands);
        IChatClient client = new ChatClientBuilder(model).UseFunctionInvocation(null, options =>
        {
            options.IncludeDetailedErrors = true;
            options.MaximumIterationsPerRequest = 200;
            options.MaximumConsecutiveErrorsPerRequest = 6;
            options.FunctionInvoker = NewInvoker().InvokeAsync;
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
        return (results, ran, model.Requests);
    }

    [Fact]
    public async Task A_plain_node_test_command_runs_with_the_force_exit_flags_and_no_preload()
    {
        (_, List<string> ran, _) = await RunAsync((_, _) => "Exit code: 0\n--- stdout ---\nok", "node --test a.test.js");

        Assert.Equal(["node --test --test-force-exit --test-timeout=30000 a.test.js"], ran);
    }

    [Fact]
    public async Task A_node_test_command_with_a_time_limit_but_no_force_exit_flag_runs_with_the_preload()
    {
        (_, List<string> ran, _) = await RunAsync((_, _) => "Exit code: 0\n--- stdout ---\nok", "node --test --test-timeout=5000 a.test.js");

        Assert.Equal([$"node {OpenHandlePreload.Argument()} --test --test-timeout=5000 a.test.js"], ran);
    }

    [Fact]
    public async Task A_command_that_is_not_a_node_test_run_is_run_as_written()
    {
        (_, List<string> ran, _) = await RunAsync((_, _) => "Exit code: 0\n--- stdout ---\nok", "npm run build", "ls");

        Assert.Equal(["npm run build", "ls"], ran);
    }

    [Fact]
    public async Task A_command_run_again_as_written_after_the_forced_exit_crashed_carries_the_preload()
    {
        (_, List<string> ran, _) = await RunAsync((_, count) => count == 1 ? Crash : "Exit code: 0\n--- stdout ---\nok", "node --test a.test.js");

        Assert.Equal(2, ran.Count);
        Assert.Contains("--test-force-exit", ran[0], StringComparison.Ordinal);
        Assert.Equal($"node {OpenHandlePreload.Argument()} --test a.test.js", ran[1]);
    }

    [Fact]
    public async Task The_first_open_handle_result_is_explained_and_a_repeat_gets_only_the_guards_note_then_a_stop()
    {
        (List<string> results, _, int requests) = await RunAsync((_, _) => EndedEarly,
            "node --test --test-timeout=5000 a.test.js", "node --test --test-timeout=6000 a.test.js", "node --test --test-timeout=7000 a.test.js", "node --test --test-timeout=8000 a.test.js");

        Assert.Equal(3, results.Count);
        string explanation = OpenHandlePreload.Explanation("TCPServerWrap");

        // The first: the explanation, once, and no guard note yet.
        Assert.StartsWith(EndedEarly, results[0], StringComparison.Ordinal);
        Assert.Contains(explanation, results[0], StringComparison.Ordinal);
        Assert.Contains("TCPServerWrap", results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("[Fleet stuck-step guard]", results[0], StringComparison.Ordinal);

        // The second: the guard's note, which names the resource; the explanation is not said again.
        Assert.DoesNotContain(explanation, results[1], StringComparison.Ordinal);
        Assert.Contains("[Fleet stuck-step guard]", results[1], StringComparison.Ordinal);
        Assert.Contains("(TCPServerWrap)", results[1], StringComparison.Ordinal);
        Assert.Contains("2 times in a row", results[1], StringComparison.Ordinal);
        Assert.DoesNotContain("round ends here", results[1], StringComparison.OrdinalIgnoreCase);

        // The third: a stop, which ends the turn.
        Assert.DoesNotContain(explanation, results[2], StringComparison.Ordinal);
        Assert.Contains("3 times in a row", results[2], StringComparison.Ordinal);
        Assert.Contains("round ends here", results[2], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task A_test_run_that_passes_between_two_open_handle_results_is_explained_again_on_the_next_one()
    {
        (List<string> results, _, _) = await RunAsync((_, count) => count == 2 ? "Exit code: 0\n--- stdout ---\nok" : EndedEarly,
            "node --test --test-timeout=5000 a.test.js", "node --test --test-timeout=6000 a.test.js", "node --test --test-timeout=7000 a.test.js");

        string explanation = OpenHandlePreload.Explanation("TCPServerWrap");
        Assert.Contains(explanation, results[0], StringComparison.Ordinal);
        Assert.DoesNotContain(explanation, results[1], StringComparison.Ordinal);
        Assert.Contains(explanation, results[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_without_the_preload_is_not_explained_even_when_its_output_has_the_marker()
    {
        (List<string> results, _, _) = await RunAsync((_, _) => EndedEarly, "node --test --test-force-exit a.test.js");

        Assert.Equal(EndedEarly, results[0]);
    }

    [Fact]
    public async Task A_worker_command_that_hangs_after_its_tests_is_read_as_an_open_handle_not_as_the_time_limit()
    {
        // On a worker the runner's time limit is an exception; its partial output holds the preload's line.
        const string partial = "✔ first test (2.5ms)\n\n[open-after-tests] TCPServerWrap\n";
        string Script(string command, int count) => throw new WorkerCommandTimeoutException("Operation has timed out.",
            new Renci.SshNet.Common.SshOperationTimeoutException("Operation has timed out."), partial);

        (List<string> results, _, _) = await RunAsync(Script,
            "node --test --test-timeout=5000 a.test.js", "node --test --test-timeout=6000 a.test.js", "node --test --test-timeout=7000 a.test.js");

        Assert.Equal(3, results.Count);
        Assert.Contains("TCPServerWrap", results[0], StringComparison.Ordinal);
        Assert.Contains(OpenHandlePreload.Explanation("TCPServerWrap"), results[0], StringComparison.Ordinal);
        Assert.DoesNotContain("time limit", results[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("something stays open", results[1], StringComparison.Ordinal);
        Assert.DoesNotContain("hit the time limit", results[1], StringComparison.Ordinal);
        Assert.Contains("3 times in a row", results[2], StringComparison.Ordinal);
    }
}

/// <summary>A step's own check whose run ended because something stayed open after its tests fails, with the diagnosis.</summary>
public sealed class OpenHandleCheckTests : PlanTestBase
{
    private const string EndedEarly =
        "Error: the command was ended early because it showed a stop line (see the output below).\n" +
        "--- partial stdout ---\n✔ first test (2.5ms)\n\n[open-after-tests] TCPServerWrap\n\n--- partial stderr ---\n";

    private async Task<(StepCompletion Result, List<string> Ran)> CheckAsync(string verify, Func<string, string> output)
    {
        PlanRecord plan = Store.Create("Fix", "Fix the parser", null, [], [], [], null, null,
            [new PlanStepInput("Fix the parser", "Make parse work", ["src/a.js"], verify, "light")]);
        Store.Approve(plan.Id, exportToProject: false);
        var ran = new List<string>();
        var tools = new PlanTools(Store, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (command, _, _) =>
        {
            ran.Add(command);
            return Task.FromResult(output(command));
        });

        return (await tools.TryCompleteStepAsync(plan.Id, 1, null, default), ran);
    }

    [Fact]
    public async Task A_check_that_ends_with_something_still_open_fails_with_the_diagnosis()
    {
        (StepCompletion result, _) = await CheckAsync("node --test x.test.js",
            _ => EndedEarly);

        Assert.False(result.Done);
        Assert.Contains("TCPServerWrap", result.Output, StringComparison.Ordinal);
        Assert.Contains("tests ran to the end", result.Output, StringComparison.Ordinal);
        Assert.Contains("Do not run the same command again unchanged", result.Output, StringComparison.Ordinal);
        Assert.Equal(OutcomeClass.OpenHandle, OutcomeClassifier.Of(result.ExitCode, false, result.Output!));
    }

    [Fact]
    public async Task A_check_with_good_looking_result_lines_and_the_marker_is_not_a_pass()
    {
        const string exitedZero = "Exit code: 0\n--- stdout ---\n✔ first test (2.5ms)\nℹ pass 1\nℹ fail 0\n\n[open-after-tests] TCPServerWrap\n";

        (StepCompletion result, _) = await CheckAsync("node --test x.test.js", _ => exitedZero);

        Assert.False(result.Done);
        Assert.Contains("tests ran to the end", result.Output, StringComparison.Ordinal);
        Assert.Equal(OutcomeClass.OpenHandle, OutcomeClassifier.Of(result.ExitCode, false, result.Output!));
    }

    [Fact]
    public async Task A_check_that_passes_cleanly_is_unchanged_and_runs_its_original_with_the_preload()
    {
        (StepCompletion result, List<string> ran) = await CheckAsync("node --test x.test.js", _ => "Exit code: 0\n--- stdout ---\nok");

        Assert.True(result.Done);
        Assert.Equal(["node --test --test-force-exit x.test.js", $"node {OpenHandlePreload.Argument()} --test x.test.js"], ran);
    }

    [Fact]
    public async Task A_chained_check_runs_once_with_the_preload_in_its_test_command()
    {
        (_, List<string> ran) = await CheckAsync("cd app && node --test x.test.js", _ => "Exit code: 0\n--- stdout ---\nok");

        Assert.Equal([$"cd app && node {OpenHandlePreload.Argument()} --test x.test.js"], ran);
    }

    [Fact]
    public async Task A_check_that_carries_the_force_exit_flag_itself_is_run_as_written()
    {
        (_, List<string> ran) = await CheckAsync("node --test --test-force-exit x.test.js", _ => "Exit code: 0\n--- stdout ---\nok");

        Assert.Equal(["node --test --test-force-exit x.test.js"], ran);
    }
}
