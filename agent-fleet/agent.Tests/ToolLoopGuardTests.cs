using Microsoft.Extensions.AI;

namespace AgentFleet.Tests;

public sealed class ToolLoopGuardTests
{
    private static FunctionCallContent Call(string id, string path = "a.txt", string tool = "read_file") =>
        new(id, tool, new Dictionary<string, object?> { ["path"] = path });

    // A user message, then one assistant call and its tool result per pair.
    private static List<ChatMessage> Turn(params (FunctionCallContent Call, string Result)[] pairs)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "carry out the step") };
        foreach ((FunctionCallContent call, string result) in pairs)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, [call]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
        }

        return messages;
    }

    [Fact]
    public void The_same_call_with_the_same_error_is_counted_including_the_one_that_just_finished()
    {
        List<ChatMessage> messages = Turn((Call("1"), "Error: no such file a.txt"), (Call("2"), "Error: no such file a.txt"));

        Assert.Equal(3, ToolLoopGuard.Repeats(messages, Call("3"), "Error: no such file a.txt"));
        Assert.Equal(1, ToolLoopGuard.Repeats(messages, Call("3", path: "b.txt"), "Error: no such file b.txt"));
        Assert.Equal(1, ToolLoopGuard.Repeats(messages, Call("3", tool: "list_directory"), "Error: no such file a.txt"));
        Assert.Equal(1, ToolLoopGuard.Repeats(messages, Call("3"), "Error: access denied to a.txt"));
    }

    [Fact]
    public void A_call_that_worked_in_between_or_a_different_turn_does_not_count()
    {
        List<ChatMessage> messages = Turn(
            (Call("1"), "Error: no such file a.txt"),
            (Call("2"), "the file's contents"),
            (Call("3"), "Error: no such file a.txt"));
        Assert.Equal(3, ToolLoopGuard.Repeats(messages, Call("4"), "Error: no such file a.txt"));

        // The follow-up message of the next round starts a new turn: what failed before it does not count.
        messages.Add(new ChatMessage(ChatRole.User, "Round 2: the approved check did not pass yet."));
        Assert.Equal(1, ToolLoopGuard.Repeats(messages, Call("5"), "Error: no such file a.txt"));
    }

    [Fact]
    public void Timings_and_whitespace_do_not_make_a_failure_different_but_a_different_error_does()
    {
        List<ChatMessage> messages = Turn(
            (Call("1", tool: "run_command"), "Exit code: 1\n--- stdout ---\nFAILED widget (12.4 ms)\n  1 failing"),
            (Call("2", tool: "run_command"), "Exit code: 1\n--- stdout ---\nFAILED   widget (15 ms)\n 1 failing"));

        Assert.Equal(3, ToolLoopGuard.Repeats(messages, Call("3", tool: "run_command"), "Exit code: 1\n--- stdout ---\nFAILED widget (9 ms)\n1 failing"));
        Assert.Equal(1, ToolLoopGuard.Repeats(messages, Call("3", tool: "run_command"), "Exit code: 1\n--- stdout ---\nFAILED widget (9 ms)\n2 failing"));
    }

    [Theory]
    [InlineData("Error: nope", true)]
    [InlineData("  failed: nope", true)]
    [InlineData("Exit code: 1\n--- stdout ---\nboom", true)]
    [InlineData("Exit code: 0\n--- stdout ---\nok", false)]
    [InlineData("contents of the file", false)]
    public void Only_results_that_say_the_call_failed_are_errors(string result, bool error) =>
        Assert.Equal(error, ToolLoopGuard.IsError(result));

    [Theory]
    [InlineData("Error: nope", true)]
    [InlineData("Unchanged: no file change", true)]
    [InlineData("contents of the file", false)]
    public void Errors_and_unchanged_results_are_no_progress(string result, bool noProgress) =>
        Assert.Equal(noProgress, ToolLoopGuard.IsNoProgress(result));

    // A model that answers every request with the same call until it has made `calls` of them, then says it is done.
    private sealed class RepeatingModel(Func<int, FunctionCallContent> call, int calls) : IChatClient
    {
        public int Requests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests++;
            ChatMessage reply = Requests <= calls
                ? new ChatMessage(ChatRole.Assistant, [call(Requests)])
                : new ChatMessage(ChatRole.Assistant, "I gave up.");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // The same wiring Program.cs gives the plan runner's requests: the guard looks at every finished call.
    private static async Task<(string[] Results, int Executed, RepeatingModel Model)> RunAsync(
        Func<int, FunctionCallContent> call, int calls, Func<string, int, string> tool)
    {
        int executed = 0;
        AITool read = AIFunctionFactory.Create((string path) => tool(path, ++executed), "read_file", "Reads a file.");
        var model = new RepeatingModel(call, calls);
        IChatClient client = new ChatClientBuilder(model)
            .UseFunctionInvocation(null, options =>
            {
                options.MaximumIterationsPerRequest = 40;
                options.FunctionInvoker = async (context, cancellationToken) =>
                {
                    object? result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
                    return ToolLoopGuard.Apply(context, result);
                };
            })
            .Build();

        ChatResponse response = await client.GetResponseAsync("carry out the step", new ChatOptions { Tools = [read] });

        string[] results = response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(content => content.Result?.ToString() ?? string.Empty).ToArray();
        return (results, executed, model);
    }

    [Fact]
    public async Task The_third_identical_failure_gets_a_note_and_the_fifth_ends_the_turn()
    {
        (string[] results, int executed, RepeatingModel model) = await RunAsync(_ => Call("x"), 10, (path, _) => $"Error: no such file {path}");

        Assert.Equal(5, executed);
        Assert.Equal(5, model.Requests); // asked five times, then the turn ended without asking again
        Assert.Equal(5, results.Length);
        Assert.All(results[..2], result => Assert.DoesNotContain(ToolLoopGuard.Marker, result));
        Assert.Contains("failed the same way 3 times in this turn", results[2]);
        Assert.Contains("try a different approach", results[2]);
        Assert.DoesNotContain(ToolLoopGuard.Marker, results[3]);
        Assert.Contains("failed the same way 5 times in this turn, so the turn ends here", results[4]);
        Assert.StartsWith("Error: no such file a.txt", results[2]);
    }

    [Fact]
    public async Task Timings_in_the_error_do_not_hide_a_loop()
    {
        (string[] results, int executed, _) = await RunAsync(_ => Call("x"), 10, (path, n) => $"Error: {path} did not load in {n * 7} ms");

        Assert.Equal(5, executed);
        Assert.Contains("3 times", results[2]);
    }

    [Fact]
    public async Task A_model_that_changes_its_arguments_each_time_is_left_alone()
    {
        (string[] results, int executed, _) = await RunAsync(round => Call("x" + round, path: $"file{round}.txt"), 8, (path, _) => $"Error: no such file {path}");

        Assert.Equal(8, executed);
        Assert.All(results, result => Assert.DoesNotContain(ToolLoopGuard.Marker, result));
    }

    [Fact]
    public async Task Five_identical_successful_calls_get_a_note_on_three_and_end_the_turn_on_five()
    {
        (string[] results, int executed, RepeatingModel model) = await RunAsync(_ => Call("x"), 8, (_, _) => "the same file contents");

        Assert.Equal(5, executed);
        Assert.Equal(5, model.Requests);
        Assert.Equal(5, results.Length);
        Assert.DoesNotContain(ToolLoopGuard.Marker, results[0]);
        Assert.DoesNotContain(ToolLoopGuard.Marker, results[1]);
        Assert.Contains("This exact call has now been made 3 times in this turn with the same result", results[2]);
        Assert.DoesNotContain(ToolLoopGuard.Marker, results[3]);
        Assert.Contains("This exact call has now been made 5 times in this turn with the same result", results[4]);
    }

    [Fact]
    public async Task Repeated_unchanged_results_use_the_existing_no_progress_threshold()
    {
        (string[] results, int executed, _) = await RunAsync(_ => Call("x"), 8, (_, _) => "Unchanged: nothing was written");

        Assert.Equal(5, executed);
        Assert.Contains("failed the same way 3 times in this turn", results[2]);
        Assert.Contains("failed the same way 5 times in this turn, so the turn ends here", results[4]);
        Assert.DoesNotContain(ToolLoopGuard.Marker, results[3]);
    }

    [Fact]
    public async Task Alternating_successful_calls_are_counted_separately_inside_the_recent_window()
    {
        (string[] results, int executed, _) = await RunAsync(
            round => round % 2 == 1 ? Call("same-a", path: "a.txt") : Call("b" + round, path: $"b{round}.txt"),
            12,
            (path, _) => $"contents of {path}");

        Assert.Equal(9, executed);
        Assert.Contains("made 3 times", results[4]);
        Assert.Contains("made 5 times", results[8]);
        Assert.All([results[1], results[3], results[5], results[7]], result => Assert.DoesNotContain(ToolLoopGuard.Marker, result));
    }

    [Fact]
    public async Task Identical_successful_calls_with_different_results_do_not_count()
    {
        (string[] results, int executed, _) = await RunAsync(_ => Call("same"), 8, (_, count) => $"contents version {count}");

        Assert.Equal(8, executed);
        Assert.All(results, result => Assert.DoesNotContain(ToolLoopGuard.Marker, result));
    }

    [Fact]
    public async Task Identical_successful_calls_more_than_ten_calls_apart_do_not_count()
    {
        (string[] results, int executed, _) = await RunAsync(
            round => round % 11 == 1 ? Call("same", path: "same.txt") : Call("filler" + round, path: $"filler{round}.txt"),
            23,
            (_, _) => "same result");

        Assert.Equal(23, executed);
        Assert.All(results, result => Assert.DoesNotContain(ToolLoopGuard.Marker, result));
    }

    [Fact]
    public async Task A_model_that_changes_course_after_the_note_is_not_stopped()
    {
        // Three identical failures, then a different call that works, then the same failing call again: counted from the
        // same turn, so the 4th identical failure gets no note and the turn goes on.
        (string[] results, int executed, _) = await RunAsync(
            round => round == 4 ? Call("y", path: "other.txt") : Call("x" + round),
            5,
            (path, _) => path == "a.txt" ? "Error: no such file a.txt" : "other contents");

        Assert.Equal(5, executed);
        Assert.Contains("3 times", results[2]);
        Assert.Equal("other contents", results[3]);
        Assert.DoesNotContain(ToolLoopGuard.Marker, results[4]);
    }

    // The wiring Program.cs gives the plan runner, for a write_file that needs a path and a content.
    private static async Task<(string[] Results, int Written, RepeatingModel Model)> WriteWithoutContentAsync(bool fromRunner, int calls)
    {
        int written = 0;
        AITool write = AIFunctionFactory.Create((string path, string content) => { written++; return $"Wrote {path}"; }, "write_file", "Writes a file.");
        var model = new RepeatingModel(round => new FunctionCallContent("w" + round, "write_file", new Dictionary<string, object?> { ["path"] = round % 2 == 0 ? "src/a.js" : "test/a.test.js" }), calls);
        IChatClient client = new ChatClientBuilder(model)
            .UseFunctionInvocation(null, options =>
            {
                options.IncludeDetailedErrors = true;
                options.MaximumIterationsPerRequest = 20;
                options.MaximumConsecutiveErrorsPerRequest = 6;
                options.FunctionInvoker = async (context, cancellationToken) =>
                {
                    object? result = await ToolLoopGuard.InvokeAsync(context, fromRunner, cancellationToken);
                    return ToolLoopGuard.Apply(context, result);
                };
            })
            .Build();

        ChatResponse response = await client.GetResponseAsync("carry out the step", new ChatOptions { Tools = [write] });
        string[] results = response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(content => content.Result?.ToString() ?? string.Empty).ToArray();
        return (results, written, model);
    }

    [Fact]
    public async Task A_write_without_its_content_comes_back_as_an_error_the_model_can_read_and_the_third_ends_the_turn()
    {
        // Measured on a worker: ten calls in a row without `content`, and the exception the sixth raised threw the round away.
        (string[] results, int written, RepeatingModel model) = await WriteWithoutContentAsync(fromRunner: true, calls: 10);

        Assert.Equal(0, written);
        Assert.Equal(3, results.Length);
        Assert.Equal(3, model.Requests); // the turn ended without asking again
        Assert.All(results, result => Assert.StartsWith("Error: write_file was called without `content`", result));
        Assert.Contains("complete text of the file", results[0]);
        Assert.DoesNotContain(ToolLoopGuard.Marker, results[1]);
        Assert.Contains("3 calls in this turn left out a required argument, so the turn ends here", results[2]);
    }

    [Fact]
    public async Task Outside_a_plan_step_a_call_without_a_required_argument_is_left_to_the_function_invoker()
    {
        (string[] results, int written, _) = await WriteWithoutContentAsync(fromRunner: false, calls: 3);

        Assert.Equal(0, written);
        Assert.All(results, result => Assert.DoesNotContain("Error: write_file was called without", result));
    }

    [Theory]
    [InlineData("The arguments dictionary is missing a value for the required parameter 'content'. (Parameter 'arguments')", "content")]
    [InlineData("The arguments dictionary is missing a value for the required parameter 'oldText'. (Parameter 'arguments')", "oldText")]
    public void Only_a_missing_required_argument_is_explained(string message, string parameter)
    {
        Assert.Contains($"`{parameter}`", ToolLoopGuard.MissingArgument("edit_file", new ArgumentException(message))!);
        Assert.Null(ToolLoopGuard.MissingArgument("edit_file", new ArgumentException("The value must not be negative.")));
        Assert.Null(ToolLoopGuard.MissingArgument("edit_file", new InvalidOperationException(message)));
    }

}
