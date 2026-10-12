using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

internal sealed class OllamaErrorBodyHandler(HttpMessageHandler inner, ILogger logger, string model) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        string? path = request.RequestUri?.AbsolutePath;
        if (response.IsSuccessStatusCode || path is null ||
            !(path.StartsWith("/api/chat", StringComparison.Ordinal) || path.StartsWith("/api/generate", StringComparison.Ordinal)))
        {
            return response;
        }

        HttpContent content = response.Content;
        string body = await content.ReadAsStringAsync(cancellationToken);
        MediaTypeHeaderValue? contentType = content.Headers.ContentType;
        Encoding encoding = contentType?.CharSet is { Length: > 0 } charset ? Encoding.GetEncoding(charset) : Encoding.UTF8;
        var restored = new StringContent(body, encoding);
        if (contentType is not null)
        {
            restored.Headers.ContentType = contentType;
        }

        response.Content = restored;
        content.Dispose();
        logger.LogWarning("Ollama {Model} answered {Status} for {Path}: {Body}", model, (int)response.StatusCode, path,
            body[..Math.Min(body.Length, 1000)]);
        return response;
    }
}
