using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// Puts every AG-UI run into its durable context before the framework's endpoint sees it: the thread id
/// is the context id, the transcript the client sent is journaled (idempotently: a message already
/// recorded is not recorded twice), and the identity is available to everything the run touches.
/// A request that is not a run, or has no GUID thread id, passes through untouched.
/// </summary>
internal static class FleetContextMiddleware
{
    public static void Use(IApplicationBuilder app, FleetContextStore store, FleetRequestContext requestContext, ILogger logger)
    {
        app.Use(async (context, next) =>
        {
            FleetRunRequest? run = null;
            try
            {
                run = await FleetRequestContext.ReadRunAsync(context.Request, context.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not read the run request for the context journal.");
            }

            if (run is null)
            {
                await next();
                return;
            }

            RecordRun(store, run, logger);
            using (requestContext.Push(run.Identity))
            {
                await next();
            }
        });
    }

    internal static void RecordRun(FleetContextStore store, FleetRunRequest run, ILogger logger)
    {
        string id = run.Identity.ContextId;
        try
        {
            FleetContextSummary? existing = store.GetContext(id);
            string? title = existing is null || existing.Title == "Untitled" ? DeriveTitle(run.Messages) : null;
            if (run.Messages is { } messages && run.JournalOnly)
            {
                // A surface that joined this conversation: its messages go into the record, the transcript
                // the web UI shows stays the conversation's own.
                if (existing is null)
                {
                    store.EnsureContext(id, title);
                }

                store.JournalMessages(id, messages, run.Identity.Surface);
            }
            else if (run.Messages is { } transcript)
            {
                store.UpsertMessages(id, title, transcript, run.Identity.Surface);
            }
            else
            {
                store.EnsureContext(id, title);
            }

            store.AppendEvent(
                id,
                FleetContextEventKind.RunInput,
                new { runId = run.Identity.RunId, surface = run.Identity.Surface, messageCount = run.Messages?.GetArrayLength() ?? 0 },
                actor: "client", agent: run.Identity.Surface);
        }
        catch (Exception exception)
        {
            // The journal must never be the reason a chat fails.
            logger.LogWarning(exception, "Could not journal the run for context {ContextId}.", id);
        }
    }

    internal static string? DeriveTitle(JsonElement? messages)
    {
        if (messages is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        foreach (JsonElement message in array.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object ||
                ContextText.String(message, "role") != "user" ||
                !message.TryGetProperty("content", out JsonElement content))
            {
                continue;
            }

            string text = content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : ContextText.MessageText(JsonSerializer.SerializeToElement(new { message = message }));
            string cleaned = text.Trim().ReplaceLineEndings(" ");
            if (cleaned.Length > 0)
            {
                return cleaned.Length > 42 ? cleaned[..42].TrimEnd() + "…" : cleaned;
            }
        }

        return null;
    }
}
