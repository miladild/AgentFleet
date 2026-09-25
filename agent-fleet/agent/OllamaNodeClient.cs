using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;

namespace AgentFleet;

/// <summary>
/// The chat client for one machine. Ollama machines are spoken to through Ollama's native API, because that is the only
/// way to choose how much conversation the model sees: its OpenAI-compatible endpoint ignores num_ctx, and Ollama's
/// default of 4096 tokens is less than the fleet's instructions and tool list alone (measured: about 7000 tokens for a
/// plain "hello"). Ollama then silently drops the start of the request, and a plan step's model loses its task.
/// </summary>
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
        ILogger logger)
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
            var http = new HttpClient { BaseAddress = root, Timeout = networkTimeout };
            raw = new ContextSizeChatClient(new OllamaApiClient(http, model), http, model, contextLength, logger);
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
internal sealed class ContextSizeChatClient(IChatClient inner, HttpClient http, string model, int maximum, ILogger logger) : DelegatingChatClient(inner)
{
    private static readonly int[] Sizes = [8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144];
    private static readonly TimeSpan KeepSize = TimeSpan.FromMinutes(20);
    internal const int MaxAnswerTokens = 4096;

    private readonly object _gate = new();
    private int? _modelMaximum;
    private bool _looked;
    private int _kept;
    private DateTimeOffset _keptUntil;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        return await base.GetResponseAsync(list, await WithContextAsync(list, options, cancellationToken), cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        ChatOptions sized = await WithContextAsync(list, options, cancellationToken);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(list, sized, cancellationToken))
        {
            yield return update;
        }
    }

    internal async Task<ChatOptions> WithContextAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        ChatOptions sized = options?.Clone() ?? new ChatOptions();
        sized.AdditionalProperties ??= [];
        if (sized.AdditionalProperties.ContainsKey("num_ctx"))
        {
            return sized;
        }

        int ceiling = Math.Min(maximum, await ModelMaximumAsync(cancellationToken) ?? int.MaxValue);
        int needed = EstimateTokens(messages, options) + 4096;
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
        return sized;
    }

    // Roughly three characters per token for code and English together; erring high only costs a little memory.
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

        return (int)Math.Min(int.MaxValue, characters / 3);
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
