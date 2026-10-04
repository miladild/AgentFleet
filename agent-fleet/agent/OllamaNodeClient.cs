using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaChatRequest = OllamaSharp.Models.Chat.ChatRequest;
using OpenAI;

namespace AgentFleet;

/// <summary>
/// The chat client for one machine. Ollama machines are spoken to through Ollama's native API, because that is the only
/// way to choose how much conversation the model sees: its OpenAI-compatible endpoint ignores num_ctx, and Ollama's
/// default of 4096 tokens is less than the fleet's instructions and tool list alone (measured: about 7000 tokens for a
/// plain "hello"). Ollama then silently drops the start of the request, and a plan step's model loses its task.
/// </summary>
internal static class NodeThinking
{
    /// <summary>A model that thinks first is told not to.</summary>
    public const string Off = "off";

    /// <summary>The model's own behaviour.</summary>
    public const string Model = "model";

    public static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? Off : value.Trim().ToLowerInvariant();
}

internal static class OllamaNodeClient
{
    /// <summary>
    /// The most a machine is asked for when it does not set its own contextLength. Each request asks only for what it
    /// needs (see ContextSizeChatClient); this is the ceiling, for a long plan step reading several files.
    /// </summary>
    public const int DefaultContextLength = 32768;

    /// <summary>The routing model reads one short prompt: a large window would only take graphics memory from the others.</summary>
    public const int TriageContextLength = 8192;

    public static int ConfiguredDefault(string? environmentValue) =>
        int.TryParse(environmentValue, out int value) && value is >= 2048 and <= 1_048_576 ? value : DefaultContextLength;

    public static IChatClient Create(
        Uri openAiEndpoint,
        string model,
        string? api,
        int contextLength,
        TimeSpan networkTimeout,
        ILogger logger,
        string thinking = NodeThinking.Off,
        HttpMessageHandler? handler = null)
    {
        IChatClient raw;
        if (string.Equals(api, "openai", StringComparison.OrdinalIgnoreCase))
        {
            var client = new OpenAIClient(
                new ApiKeyCredential("ollama"), // Servers that ignore the key still need one.
                new OpenAIClientOptions { Endpoint = openAiEndpoint, NetworkTimeout = networkTimeout });
            raw = client.GetChatClient(model).AsIChatClient();
        }
        else
        {
            var root = new UriBuilder(openAiEndpoint.Scheme, openAiEndpoint.Host, openAiEndpoint.IsDefaultPort ? -1 : openAiEndpoint.Port, "/").Uri;
            var http = handler is null
                ? new HttpClient { BaseAddress = root, Timeout = networkTimeout }
                : new HttpClient(handler) { BaseAddress = root, Timeout = networkTimeout };
            raw = new ContextSizeChatClient(new OllamaApiClient(http, model), http, model, contextLength, logger,
                thinkingOff: NodeThinking.Normalize(thinking) == NodeThinking.Off);
        }

        // Works around a known, open Ollama bug (ollama/ollama#18530, #18563) where its qwen3-coder tool-call parser can
        // silently fail to structure a tool call, leaking it as plain <function=...> text instead. No-op otherwise.
        return new ToolCallRescueChatClient(raw, logger);
    }
}

/// <summary>
/// Puts num_ctx on every request: enough for this request (its messages, instructions and tool list, plus room for
/// the answer), in a few fixed sizes, at most the machine's maximum and what the model was trained for. Asking only for
/// what is needed keeps a model in graphics memory on a small card (measured: rnj-1 on a 6 GB card fits at 8K but spills
/// to the processor at 32K and takes minutes per call). A size, once used, is kept for twenty minutes so a conversation
/// that grows does not make Ollama reload the model at every step down and up.
/// </summary>
internal sealed class ContextSizeChatClient(IChatClient inner, HttpClient http, string model, int maximum, ILogger logger, bool thinkingOff = false) : DelegatingChatClient(inner)
{
    /// <summary>The smallest window a request asks for; the health probe asks for it when nothing is loaded.</summary>
    internal const int SmallestSize = 8192;

    private static readonly int[] Sizes = [SmallestSize, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144];
    private static readonly TimeSpan KeepSize = TimeSpan.FromMinutes(20);
    internal const int MaxAnswerTokens = 4096;

    // Measured on fleet traffic with a Qwen tokenizer: tool calls and results, full of Windows paths and JSON, run
    // 2.57 characters per token; code about 3.75, prose about 4.1. Estimating at 2.5 keeps the worst case in the window.
    internal const double CharactersPerToken = 2.5;

    // A prompt this close to the window's end ate the room kept for the answer: Ollama then drops the start of the
    // conversation to make it fit, task included (measured: a worker lost its task after fifteen tool rounds and
    // answered as a "personal life coach").
    internal const int FullMargin = MaxAnswerTokens / 2;

    private readonly object _gate = new();
    private int? _modelMaximum;
    private bool _looked;
    private int _kept;
    private DateTimeOffset _keptUntil;
    private double _scale = 1.0;

    // The latest turns stay whole when a conversation is shortened to fit: the model is in the middle of them.
    internal const int KeepRecentMessages = 6;

    internal sealed record Sized(ChatOptions Options, int Estimate, int Size, bool Chosen, IReadOnlyList<ChatMessage> Messages);

    /// <summary>How much bigger this machine's prompts have turned out than estimated (1 until one did).</summary>
    internal double Scale
    {
        get
        {
            lock (_gate)
            {
                return _scale;
            }
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        Sized sized = await PrepareAsync(list, options, cancellationToken);
        ChatResponse response = await base.GetResponseAsync(sized.Messages, sized.Options, cancellationToken);
        if (!Observe(sized, response.Usage))
        {
            return response;
        }

        // The answer came from a cut prompt: ask again with the bigger window the prompt turned out to need.
        Sized bigger = await PrepareAsync(list, options, cancellationToken);
        if (bigger.Size <= sized.Size)
        {
            return response;
        }

        logger.LogWarning("Asking {Model} again with {Size} tokens of context instead of {Previous}.", model, bigger.Size, sized.Size);
        response = await base.GetResponseAsync(bigger.Messages, bigger.Options, cancellationToken);
        Observe(bigger, response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        Sized sized = await PrepareAsync(list, options, cancellationToken);
        UsageDetails? usage = null;
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(sized.Messages, sized.Options, cancellationToken))
        {
            usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
            yield return update;
        }

        // Too late to ask again (the answer is already on its way), but the next request gets the bigger window.
        Observe(sized, usage);
    }

    internal async Task<ChatOptions> WithContextAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
        (await PrepareAsync(messages, options, cancellationToken)).Options;

    /// <summary>
    /// Learns from the prompt size Ollama reports. True when the prompt filled the window, so the answer came from a
    /// conversation with its start cut off.
    /// </summary>
    internal bool Observe(Sized sized, UsageDetails? usage)
    {
        if (!sized.Chosen || usage?.InputTokenCount is not long actual || sized.Estimate <= 0)
        {
            return false;
        }

        bool full = actual >= sized.Size - FullMargin;
        double observed = (double)actual / sized.Estimate;
        if (full)
        {
            // What Ollama kept is only a lower bound for what the prompt needed.
            observed = Math.Max(observed, (double)sized.Size / sized.Estimate) * 1.25;
        }

        lock (_gate)
        {
            _scale = Math.Clamp(Math.Max(_scale, observed * 1.1), 1.0, 4.0);
        }

        if (full)
        {
            logger.LogWarning(
                "A prompt to {Model} used {Actual} of its {Size} tokens of context, so Ollama may have dropped the start of the " +
                "conversation. Estimates for this machine are now scaled by {Scale:0.00}.", model, actual, sized.Size, Scale);
        }

        return full;
    }

    private async Task<Sized> PrepareAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        ChatOptions sized = options?.Clone() ?? new ChatOptions();
        sized.AdditionalProperties ??= [];
        if (thinkingOff && sized.RawRepresentationFactory is null)
        {
            // Ollama's think: false. A model that thinks first spends its answer budget on it (see FleetNodeConfig.Thinking);
            // a model that cannot think ignores it.
            sized.RawRepresentationFactory = _ => new OllamaChatRequest { Think = false };
        }

        if (sized.AdditionalProperties.ContainsKey("num_ctx"))
        {
            return new Sized(sized, 0, 0, Chosen: false, messages);
        }

        int ceiling = Math.Min(maximum, await ModelMaximumAsync(cancellationToken) ?? int.MaxValue);
        double scale = Scale;
        int estimate = EstimateTokens(messages, options);
        if (estimate * scale + 4096 > ceiling)
        {
            IReadOnlyList<ChatMessage> fitted = FitToWindow(messages, options, ceiling - 4096, scale);
            if (!ReferenceEquals(fitted, messages))
            {
                int before = estimate;
                messages = fitted;
                estimate = EstimateTokens(messages, options);
                logger.LogInformation(
                    "A conversation with {Model} outgrew its {Ceiling}-token window: left out its oldest tool calls and shortened old output, from about {Before} to {After} tokens, keeping the task and the latest turns.",
                    model, ceiling, (int)(before * scale), (int)(estimate * scale));
            }
        }

        int needed = (int)Math.Min(int.MaxValue - 8192, estimate * scale) + 4096;
        int size;
        lock (_gate)
        {
            size = Sizes.FirstOrDefault(candidate => candidate >= needed, Sizes[^1]);
            if (_keptUntil > DateTimeOffset.UtcNow)
            {
                size = Math.Max(size, _kept);
            }

            size = Math.Min(size, ceiling);
            _kept = size;
            _keptUntil = DateTimeOffset.UtcNow + KeepSize;
        }

        if (needed > ceiling)
        {
            logger.LogWarning(
                "A request to {Model} needs about {Needed} tokens but the most it may use is {Ceiling}: Ollama will drop the start of it. " +
                "Give this machine a larger contextLength (if its memory allows) or a model with a longer window.", model, needed, ceiling);
        }

        sized.AdditionalProperties["num_ctx"] = size;

        // Ollama's native API writes until the window is full unless told otherwise, so a model stuck repeating itself
        // could hold a machine past the network timeout (measured: a worker ran for over five minutes on one call). 4096
        // tokens is room for a file of a few hundred lines in one write_file call; bigger files are what edit_file is for.
        sized.MaxOutputTokens ??= Math.Min(MaxAnswerTokens, size / 2);
        return new Sized(sized, estimate, size, Chosen: true, messages);
    }

    /// <summary>
    /// A conversation cut to fit the window from the middle, not the start. Past the window Ollama drops messages from
    /// the start, and the task goes first (measured: a plan step on the hub grew to about 50000 tokens in 34 tool
    /// rounds, lost its task and went round in circles). Here the oldest tool calls go, each with its results, and the
    /// output and follow-ups that stay are shortened, until it fits; the system instructions, the first user message (the
    /// task) and the latest turns stay whole. A call that stays is never changed: a model copies the shape of the calls
    /// it sees (measured: with an old write_file shown without its content or with a placeholder in it, a worker's
    /// next write_file had no content or the placeholder as its content, 15 times in 15). The caller's list is not changed.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> FitToWindow(IReadOnlyList<ChatMessage> messages, ChatOptions? options, int budgetTokens, double scale)
    {
        if (EstimateTokens(messages, options) * scale <= budgetTokens)
        {
            return messages;
        }

        List<ChatMessage> fitted = [.. messages];
        bool[] gone = new bool[fitted.Count];
        int[] resultsEnd = ResultsEnds(fitted);
        int task = fitted.FindIndex(message => message.Role == ChatRole.User);

        // The latest turns can hold whole files (measured: a retry on the hub stayed at 29000 of its 28672 tokens after
        // every older turn was shortened, because its last turns wrote a file and read it back). The older of those go
        // too when the older ones are not enough, but never the last call and its result, which the model is acting on.
        if (!ShortenUntilItFits(0, fitted.Count - KeepRecentMessages, ShortenStep))
        {
            ShortenUntilItFits(Math.Max(0, fitted.Count - KeepRecentMessages), fitted.Count - 2, 1);
        }

        return fitted.Where((_, index) => !gone[index]).ToList();

        // The turns older than a boundary go or are shortened, and the boundary only stops at a multiple of ShortenStep:
        // between two such steps the oldest part of the conversation reaches the model unchanged, so Ollama keeps its work on it.
        bool ShortenUntilItFits(int from, int to, int step)
        {
            for (int index = from; index < to; index++)
            {
                if (!gone[index] && index != task && fitted[index].Role != ChatRole.System)
                {
                    if (resultsEnd[index] >= 0)
                    {
                        // A call and its results go together, or not at all.
                        if (resultsEnd[index] < to)
                        {
                            Array.Fill(gone, true, index, resultsEnd[index] - index + 1);
                        }
                    }
                    else if (Shortened(fitted[index]) is { } shorter)
                    {
                        fitted[index] = shorter;
                    }
                }

                bool boundary = (index + 1) % step == 0 || index == to - 1;
                if (boundary && EstimateTokens(fitted.Where((_, at) => !gone[at]).ToList(), options) * scale <= budgetTokens)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // For the message that holds tool calls: the index of the last message that answers them; -1 for any other message.
    private static int[] ResultsEnds(List<ChatMessage> messages)
    {
        int[] ends = new int[messages.Count];
        Array.Fill(ends, -1);
        for (int index = 0; index < messages.Count; index++)
        {
            if (messages[index].Role != ChatRole.Assistant || !messages[index].Contents.Any(content => content is FunctionCallContent))
            {
                continue;
            }

            int last = index;
            while (last + 1 < messages.Count && messages[last + 1].Role == ChatRole.Tool && messages[last + 1].Contents.Any(content => content is FunctionResultContent))
            {
                last++;
            }

            ends[index] = last > index ? last : -1;
        }

        return ends;
    }

    /// <summary>
    /// How many messages the shortened part of a conversation grows by at a time. Ollama keeps its work on the start of a
    /// prompt only while that start is unchanged (measured on a 15000-token prompt: the same prompt again took 0.2 s, with
    /// text added at its end 2 s, with one word changed near its start 28 s). Shortened one message at a time, the start
    /// changed at every call and a worker re-read nearly all of its window each time; in steps, only every few calls.
    /// </summary>
    internal const int ShortenStep = 8;

    private const int LongPart = 400;
    /// <summary>What stands in for shortened tool output; no tool may write it into a file (see WorkspaceTools).</summary>
    internal const string RemovedMarker = "[the rest was removed to keep this conversation inside the model's window]";

    private const string Removed = " " + RemovedMarker;

    private static ChatMessage? Shortened(ChatMessage message)
    {
        bool changed = false;
        var contents = new List<AIContent>(message.Contents.Count);
        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case FunctionResultContent result when result.Result?.ToString() is { Length: > LongPart } text:
                    contents.Add(new FunctionResultContent(result.CallId, text[..200] + Removed));
                    changed = true;
                    break;
                case TextContent text when message.Role == ChatRole.Assistant && text.Text is { Length: > 4 * LongPart }:
                    contents.Add(new TextContent(text.Text[..LongPart] + Removed));
                    changed = true;
                    break;
                // The check output of an earlier round of a plan step, sent back to the model as a follow-up message: the newer
                // rounds say what is still failing, so the old text can go. (The task, the first user message, is never
                // reached here, and neither is anyone's own message in a chat: only a follow-up that carries a plan's marker.)
                case TextContent text when message.Role == ChatRole.User && text.Text is { Length: > 4 * LongPart } &&
                                           FleetPlanStore.FindMarkerId(text.Text) is not null:
                    contents.Add(new TextContent(text.Text[..(2 * LongPart)] + Removed));
                    changed = true;
                    break;
                default:
                    contents.Add(content);
                    break;
            }
        }

        return changed
            ? new ChatMessage(message.Role, contents) { AuthorName = message.AuthorName, MessageId = message.MessageId, AdditionalProperties = message.AdditionalProperties }
            : null;
    }

    // See CharactersPerToken; erring high only costs a little memory.
    internal static int EstimateTokens(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        long characters = options?.Instructions?.Length ?? 0;
        foreach (ChatMessage message in messages)
        {
            foreach (AIContent content in message.Contents)
            {
                characters += content switch
                {
                    TextContent text => text.Text?.Length ?? 0,
                    FunctionCallContent call => call.Name.Length + (call.Arguments is null ? 0 : JsonSerializer.Serialize(call.Arguments).Length),
                    FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                    DataContent => 1500, // An image costs the vision model a fixed budget of tokens, not its byte size.
                    _ => 0
                };
            }
        }

        foreach (AITool tool in options?.Tools ?? [])
        {
            characters += tool.Name.Length + (tool.Description?.Length ?? 0) +
                          (tool is AIFunctionDeclaration declaration ? declaration.JsonSchema.GetRawText().Length : 0);
        }

        return (int)Math.Min(int.MaxValue, characters / CharactersPerToken);
    }

    private async Task<int?> ModelMaximumAsync(CancellationToken cancellationToken)
    {
        if (_looked)
        {
            return _modelMaximum;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using HttpResponseMessage response = await http.PostAsJsonAsync("api/show", new { model }, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument show = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                _modelMaximum = MaxContext(show.RootElement);
                _looked = true;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Unknown for now: look again on the next request.
        }

        return _modelMaximum;
    }

    // model_info carries "<architecture>.context_length"; a model with a scaled window reports the scaled one there.
    internal static int? MaxContext(JsonElement show)
    {
        if (!show.TryGetProperty("model_info", out JsonElement info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in info.EnumerateObject())
        {
            if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) &&
                !property.Name.Contains("original", StringComparison.Ordinal) &&
                property.Value.TryGetInt32(out int value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }
}
