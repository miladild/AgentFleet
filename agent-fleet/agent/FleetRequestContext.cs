using System.Text.Json;

namespace AgentFleet;

/// <summary>
/// Identifies the durable Fleet context for the AG-UI request currently flowing
/// through the backend. The wire thread id is the context id everywhere.
/// </summary>
/// <param name="TaskId">The plan step (or other unit of work) the code running under this identity belongs to.</param>
/// <param name="Joined">
/// The request comes from a surface that joined the conversation with a message list of its own (the model
/// picker linked to a chat), so the conversation's recent turns are not in what it sent.
/// </param>
/// <param name="ClientContext">
/// What the client said about the user's situation (AG-UI's "context": the web UI's project folder, for example).
/// The framework's endpoint drops it, so the router hands it to the model with the durable record.
/// </param>
internal sealed record FleetRequestIdentity(
    string ContextId,
    string? RunId,
    string Surface,
    string? TaskId = null,
    bool Joined = false,
    IReadOnlyList<ClientContextItem>? ClientContext = null);

/// <summary>One AG-UI context entry: a description and its value, as text.</summary>
internal sealed record ClientContextItem(string Description, string Value)
{
    public const int MaxItems = 8;
    public const int MaxDescription = 200;
    public const int MaxValue = 2000;

    /// <summary>Reads AG-UI's "context" array, bounded so a client cannot flood the prompt.</summary>
    public static IReadOnlyList<ClientContextItem>? Read(JsonElement root)
    {
        if (!root.TryGetProperty("context", out JsonElement context) || context.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<ClientContextItem>();
        foreach (JsonElement entry in context.EnumerateArray())
        {
            if (items.Count == MaxItems || entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("description", out JsonElement description) || description.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("value", out JsonElement value))
            {
                continue;
            }

            string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            items.Add(new ClientContextItem(Clip(description.GetString()!.Trim(), MaxDescription), Clip(text.Trim(), MaxValue)));
        }

        return items.Count == 0 ? null : items;
    }

    /// <summary>The block the model is given, or null when there is nothing to say.</summary>
    public static string? Describe(IReadOnlyList<ClientContextItem>? items) =>
        items is not { Count: > 0 }
            ? null
            : "The user's current settings in the app they are chatting from (use them; the user can change them there):\n" +
              string.Join('\n', items.Select(item => $"- {item.Description}: {item.Value}"));

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}

/// <summary>An AG-UI run request reduced to what the journal needs: who it is for and the transcript it carried.</summary>
/// <param name="JournalOnly">
/// The client asked (forwardedProps.fleetTranscript = "journal") for its messages to be journaled without
/// replacing the conversation's visible transcript.
/// </param>
internal sealed record FleetRunRequest(FleetRequestIdentity Identity, JsonElement? Messages, bool JournalOnly = false);

internal sealed class FleetRequestContext
{
    private static readonly AsyncLocal<FleetRequestIdentity?> Ambient = new();

    public FleetRequestIdentity? Current => Ambient.Value;

    public IDisposable Push(FleetRequestIdentity identity)
    {
        FleetRequestIdentity? prior = Ambient.Value;
        Ambient.Value = identity;
        return new PopWhenDisposed(prior);
    }

    // Only the plain hyphenated form. The same GUID written another way (no hyphens, braces) would
    // otherwise become a second, separate context.
    public static bool IsValidContextId(string? value) => Guid.TryParseExact(value, "D", out _);

    /// <summary>
    /// Reads the run request without consuming it: the framework's own endpoint still gets the body.
    /// Requests that are not a run, or whose thread id is not a GUID, have no durable context.
    /// </summary>
    public static async Task<FleetRunRequest?> ReadRunAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(request.Method) || request.Path != "/" || request.ContentLength == 0)
        {
            return null;
        }

        request.EnableBuffering();
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? contextId = root.TryGetProperty("threadId", out JsonElement thread) && thread.ValueKind == JsonValueKind.String
                ? thread.GetString()
                : null;
            if (!IsValidContextId(contextId))
            {
                return null;
            }

            string? runId = root.TryGetProperty("runId", out JsonElement run) && run.ValueKind == JsonValueKind.String
                ? run.GetString()
                : null;
            string surface = "ag-ui";
            bool journalOnly = false;
            if (root.TryGetProperty("forwardedProps", out JsonElement forwarded) && forwarded.ValueKind == JsonValueKind.Object)
            {
                if (forwarded.TryGetProperty("fleetSurface", out JsonElement surfaceElement) &&
                    surfaceElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(surfaceElement.GetString()))
                {
                    surface = surfaceElement.GetString()!;
                }

                journalOnly = forwarded.TryGetProperty("fleetTranscript", out JsonElement transcript) &&
                              transcript.ValueKind == JsonValueKind.String &&
                              transcript.GetString() == "journal";
            }

            JsonElement? messages = root.TryGetProperty("messages", out JsonElement messagesElement) &&
                                    messagesElement.ValueKind == JsonValueKind.Array
                ? messagesElement.Clone()
                : null;
            return new FleetRunRequest(
                new FleetRequestIdentity(Guid.Parse(contextId!).ToString("D"), runId, surface, Joined: journalOnly, ClientContext: ClientContextItem.Read(root)),
                messages,
                journalOnly);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private sealed class PopWhenDisposed(FleetRequestIdentity? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Ambient.Value = prior;
            _disposed = true;
        }
    }
}
