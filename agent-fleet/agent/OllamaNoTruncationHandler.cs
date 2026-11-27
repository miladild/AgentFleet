using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentFleet;

/// <summary>
/// Asks Ollama, on every chat request, not to shorten a prompt that does not fit. By default it drops the oldest messages
/// until the rest fits and answers as if nothing happened, and the task is the oldest message after the system's
/// (measured: a prompt of about 23000 tokens sent to a 16384 window was answered from 369 of them, without the task). Asked
/// not to, it refuses the request, and the client shortens the conversation itself. The client library has no field for
/// this on a chat request, so it is added to the request's body.
/// </summary>
internal sealed class OllamaNoTruncationHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is { } content && request.RequestUri?.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal) == true)
        {
            string body = await content.ReadAsStringAsync(cancellationToken);
            if (WithoutTruncation(body) is { } changed)
            {
                request.Content = new StringContent(changed, Encoding.UTF8, "application/json");
                content.Dispose();
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    // The body with "truncate": false, or null when it is not an object this can be added to.
    private static string? WithoutTruncation(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject json)
            {
                return null;
            }

            json["truncate"] = false;
            return json.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
