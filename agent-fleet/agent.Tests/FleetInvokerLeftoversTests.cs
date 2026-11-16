using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>
/// A test run that a plan step's worker starts in the step's folder has the files it created removed when it ends. Any other command
/// keeps the files it writes, because those are the worker's own work and the extra round for unnamed files judges them.
/// </summary>
[Collection(ContextTestBase.SqliteRecordCollection)]
public sealed class FleetInvokerLeftoversTests : ContextTestBase
{
    private FleetFunctionInvoker NewInvoker(FleetPlanStore plans, PlanTools tools)
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"fleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """
            { "triageModel": "t", "mode": "conservative", "planModeEnabled": false,
              "nodes": [{ "name": "hub", "url": "http://127.0.0.1:11434/v1", "model": "m", "purpose": "", "fallback": true }], "tools": {} }
            """);
        var configStore = new FleetConfigStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = configPath }).Build());
        return new FleetFunctionInvoker(Journal, new FleetPlanModeService(configStore, RequestContext), plans,
            new SwappableNameSet(PlanGate.BuiltInReadOnlyTools), tools, NullLogger.Instance, 25);
    }

    // Calls one tool with the arguments of each scripted call in turn, then answers.
    private sealed class ToolCallingModel(string tool, params Dictionary<string, object?>[] calls) : IChatClient
    {
        private int _requests;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            int round = _requests++;
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

    private static string Repository(string folder)
    {
        Directory.CreateDirectory(folder);
        FailFirstTests.Git(folder, "init", "-q");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "old a");
        FailFirstTests.Git(folder, "add", "-A");
        FailFirstTests.Git(folder, "commit", "-q", "-m", "baseline");
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        return folder;
    }

    [Fact]
    public async Task A_test_run_in_the_step_folder_has_its_files_removed_and_another_command_keeps_what_it_wrote()
    {
        string repo = Repository(Path.Combine(Root, "leftovers-repo"));
        string plansDir = Path.Combine(Root, "plans-leftovers");
        Directory.CreateDirectory(plansDir);
        var plans = new FleetPlanStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["FLEET_PLANS_DIR"] = plansDir }).Build());
        PlanRecord plan = plans.Approve(plans.Create("Plan", "goal", repo, ["a"], ["q?"], ["r"], null, null,
            [new PlanStepInput("edit a", "detail", ["a.txt"], "check", "standard")]).Id)!;
        var tools = new PlanTools(plans, new FakeValidator(code => new DiagramCheck(true, true, code, null)),
            (command, directory, _) => Task.FromResult(command.StartsWith("git ", StringComparison.Ordinal)
                ? PlanRunLeftoversTests.RunGit(command, directory)
                : "Exit code: 0\n--- stdout ---\nok"));

        // The tool writes what the command would: ten data files for a test run, one notes file for anything else.
        AITool runCommand = AIFunctionFactory.Create(string (string command) =>
        {
            string folder = repo;
            if (command == "npm test")
            {
                Directory.CreateDirectory(Path.Combine(folder, "data"));
                for (int index = 0; index < 10; index++) File.WriteAllText(Path.Combine(folder, "data", $"{index}.json"), "{}");
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(folder, "notes"));
                File.WriteAllText(Path.Combine(folder, "notes", "out.txt"), "kept");
            }

            return "Exit code: 0";
        }, "run_command", "Runs a command.");

        var model = new ToolCallingModel("run_command", new Dictionary<string, object?> { ["command"] = "npm test" },
            new Dictionary<string, object?> { ["command"] = "echo hello" });
        IChatClient client = new ChatClientBuilder(model).UseFunctionInvocation(null, options =>
        {
            options.MaximumIterationsPerRequest = 20;
            options.FunctionInvoker = NewInvoker(plans, tools).InvokeAsync;
        }).Build();
        var chatOptions = new ChatOptions
        {
            Tools = [runCommand],
            AdditionalProperties = new AdditionalPropertiesDictionary { [FleetRoutingChatClient.RunnerTierKey] = "standard" }
        };
        using IDisposable scope = Scope(NewContextId());

        ChatResponse response = await client.GetResponseAsync($"go {FleetPlanStore.Marker(plan.Id)}", chatOptions, CancellationToken.None);

        var results = response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => result.Result?.ToString() ?? string.Empty).ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains("[HARNESS] The test run created files the plan does not name, and they were removed", results[0], StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(repo, "data")));
        Assert.Equal("Exit code: 0", results[1]);
        Assert.True(File.Exists(Path.Combine(repo, "notes", "out.txt")), "a command that is not a test run keeps the files it wrote");
    }
}
