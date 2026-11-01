using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A repair round starts with the files it needs already in its message, so a model that only reads is told to write after a few
/// reads in a row and its round ends after a few more; a round has a cap on its calls too. Counting lives in the tool invoker.
/// </summary>
[Collection(ContextTestBase.SqliteRecordCollection)]
public sealed class ReadBudgetInvokerTests : ContextTestBase
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
        string plansDir = Path.Combine(Root, "plans-read-budget");
        Directory.CreateDirectory(plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = plansDir }).Build());
        var tools = new PlanTools(plans, new FakeValidator(code => new DiagramCheck(true, true, code, null)), (_, _, _) => Task.FromResult("ok"));
        return new FleetFunctionInvoker(Journal, new FleetPlanModeService(configStore, RequestContext), plans,
            new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), tools, NullLogger.Instance, 25, harness: harness);
    }

    /// <summary>Makes the scripted tool calls one per request, then answers.</summary>
    private sealed class ScriptedModel(params string[] tools) : IChatClient
    {
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int round = Requests++;
            ChatMessage reply = round < tools.Length
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{round}", tools[round], new Dictionary<string, object?> { ["path"] = $"src/f{round}.ts" })])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private async Task<(List<string> Ran, List<string> Results, StepToolActivity Activity)> RunRoundAsync(HarnessOptions? harness, bool repair, params string[] calls)
    {
        var ran = new List<string>();
        AITool[] real = new[] { "read_file", "edit_file" }
            .Select(name => (AITool)AIFunctionFactory.Create(string (string path) => { ran.Add(name); return "ok"; }, name, "A tool."))
            .ToArray();
        var model = new ScriptedModel(calls);
        IChatClient client = new ChatClientBuilder(model).UseFunctionInvocation(null, options =>
        {
            options.IncludeDetailedErrors = true;
            options.MaximumIterationsPerRequest = 200;
            options.FunctionInvoker = NewInvoker(harness).InvokeAsync;
        }).Build();
        var activity = new StepToolActivity { RepairRound = repair };
        var chatOptions = new ChatOptions
        {
            Tools = real,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [FleetRoutingChatClient.RunnerTierKey] = "standard",
                [StepToolActivity.Key] = activity
            }
        };
        using IDisposable scope = Scope(NewContextId());
        ChatResponse response = await client.GetResponseAsync("go", chatOptions, CancellationToken.None);
        var results = response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? string.Empty).ToList();
        return (ran, results, activity);
    }

    private static string[] Repeat(string tool, int count) => Enumerable.Repeat(tool, count).ToArray();

    [Fact]
    public async Task A_repair_round_that_only_reads_is_told_to_write_at_the_fourth_read_and_ends_at_the_sixth()
    {
        (List<string> ran, List<string> results, StepToolActivity activity) = await RunRoundAsync(null, repair: true, Repeat("read_file", 9));

        Assert.Equal(6, ran.Count);
        Assert.DoesNotContain("[HARNESS]", results[2], StringComparison.Ordinal);
        Assert.Contains("[HARNESS] You have made 4 consecutive read calls. Your next call MUST be edit_file, write_file, or report_blocker.", results[3], StringComparison.Ordinal);
        Assert.Contains("[HARNESS]", results[5], StringComparison.Ordinal);
        Assert.True(activity.EndedForReads);
    }

    [Fact]
    public async Task A_write_starts_the_count_again()
    {
        (List<string> ran, List<string> results, StepToolActivity activity) = await RunRoundAsync(null, repair: true,
            [.. Repeat("read_file", 3), "edit_file", .. Repeat("read_file", 3)]);

        Assert.Equal(7, ran.Count);
        Assert.DoesNotContain(results, result => result.Contains("[HARNESS]", StringComparison.Ordinal));
        Assert.False(activity.EndedForReads);
    }

    [Fact]
    public async Task A_first_round_reads_as_it_needs_to_and_is_not_counted()
    {
        (List<string> ran, List<string> results, StepToolActivity activity) = await RunRoundAsync(null, repair: false, Repeat("read_file", 9));

        Assert.Equal(9, ran.Count);
        Assert.DoesNotContain(results, result => result.Contains("[HARNESS]", StringComparison.Ordinal));
        Assert.False(activity.EndedForReads);
    }

    [Fact]
    public async Task With_the_filter_switched_off_a_repair_round_reads_as_before()
    {
        (List<string> ran, List<string> results, _) = await RunRoundAsync(new HarnessOptions(EnableBudgetFilter: false), repair: true, Repeat("read_file", 9));

        Assert.Equal(9, ran.Count);
        Assert.DoesNotContain(results, result => result.Contains("[HARNESS]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repair_round_ends_at_its_call_limit()
    {
        (List<string> ran, _, StepToolActivity activity) = await RunRoundAsync(new HarnessOptions(MaxCallsRepair: 5), repair: true, Repeat("edit_file", 9));

        Assert.Equal(5, ran.Count);
        Assert.False(activity.EndedForReads);
    }
}
