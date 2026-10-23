using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class EditMissGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-edit-miss-" + Guid.NewGuid().ToString("N"));

    public EditMissGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 60).Select(i => $"row {i}")) + "\n");
        return path;
    }

    private static FunctionCallContent Edit(string id, string path, string oldText, string newText = "x") =>
        new(id, "edit_file", new Dictionary<string, object?> { ["path"] = path, ["oldText"] = oldText, ["newText"] = newText });

    // A model that makes the scripted calls one per request, then says it is done.
    private sealed class ScriptedModel(IReadOnlyList<FunctionCallContent> script) : IChatClient
    {
        private int _next;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatMessage reply = _next < script.Count
                ? new ChatMessage(ChatRole.Assistant, [script[_next++]])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // The same wiring Program.cs gives the plan runner: the guard looks at every finished call, the hub edit tool does the work.
    private static async Task<string[]> RunAsync(IReadOnlyList<FunctionCallContent> script)
    {
        AITool edit = AIFunctionFactory.Create(
            (string path, string oldText, string newText) => HubFileSystemTools.EditFileAsync(path, oldText, newText, null, default),
            "edit_file", "Edits a file.");
        IChatClient client = new ChatClientBuilder(new ScriptedModel(script))
            .UseFunctionInvocation(null, options =>
            {
                options.MaximumIterationsPerRequest = script.Count + 2;
                options.FunctionInvoker = async (context, cancellationToken) =>
                {
                    object? result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
                    return ToolLoopGuard.Apply(context, result);
                };
            })
            .Build();

        ChatResponse response = await client.GetResponseAsync("carry out the step", new ChatOptions { Tools = [edit] });
        return response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(content => content.Result?.ToString() ?? string.Empty).ToArray();
    }

    private const string Note = "edits to this file in a row failed";

    [Fact]
    public async Task The_third_miss_in_a_row_on_a_path_gets_the_closest_region_even_when_the_text_differs()
    {
        string file = Write("third.txt");

        string[] results = await RunAsync([
            Edit("1", file, "row 20\nmissing one"),
            Edit("2", file, "row 20\nmissing two"),
            Edit("3", file, "row 20\nmissing three"),
            Edit("4", file, "row 20\nmissing four")]);

        Assert.Contains("Error: oldText was not found", results[0]);
        Assert.DoesNotContain(Note, results[0]);
        Assert.DoesNotContain(Note, results[1]);
        Assert.Contains(Note, results[2]);
        Assert.Contains("The closest text, lines 20-59:", results[2]);
        Assert.Contains("\n59: row 59", results[2]);
        Assert.Contains(Note, results[3]);
    }

    [Fact]
    public async Task A_successful_edit_of_the_path_resets_the_count()
    {
        string file = Write("reset.txt");

        string[] results = await RunAsync([
            Edit("1", file, "row 20\nmissing one"),
            Edit("2", file, "row 20\nmissing two"),
            Edit("3", file, "row 1\n", "ROW 1\n"),
            Edit("4", file, "row 20\nmissing three"),
            Edit("5", file, "row 20\nmissing four"),
            Edit("6", file, "row 20\nmissing five")]);

        Assert.StartsWith("Edited", results[2]);
        Assert.DoesNotContain(Note, results[3]);
        Assert.DoesNotContain(Note, results[4]);
        Assert.Contains(Note, results[5]);
    }

    [Fact]
    public async Task Misses_are_counted_per_path()
    {
        string first = Write("first.txt");
        string second = Write("second.txt");

        string[] results = await RunAsync([
            Edit("1", first, "row 20\nmissing one"),
            Edit("2", first, "row 20\nmissing two"),
            Edit("3", second, "row 20\nmissing three"),
            Edit("4", first, "row 20\nmissing four")]);

        Assert.DoesNotContain(Note, results[2]);
        Assert.Contains(Note, results[3]);
    }
}
