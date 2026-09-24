using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace AgentFleet;

/// <param name="Validated">
/// False when nothing could check the diagram (the checker was unreachable), so the caller
/// can say the diagram is unchecked instead of pretending it passed.
/// </param>
internal sealed record DiagramCheck(bool Valid, bool Validated, string Code, string? Error);

internal interface IDiagramValidator
{
    Task<DiagramCheck> CheckAsync(string code, CancellationToken cancellationToken);
}

/// <summary>
/// Cleans up the mistakes models make most often in Mermaid before it is parsed. Measured
/// on the fleet's own models: the standard worker failed every flowchart it drew, always
/// on parentheses inside an unquoted node label such as A[Heavy Hub (Ollama)], which
/// Mermaid only accepts as A["Heavy Hub (Ollama)"]. A deterministic fix beats asking a
/// model to repair its own syntax.
/// </summary>
internal static partial class DiagramSanitizer
{
    public static string Fix(string code)
    {
        string text = code.Trim();

        // A model asked for "a Mermaid diagram" often wraps it in a fence anyway.
        Match fenced = FencePattern().Match(text);
        if (fenced.Success)
        {
            text = fenced.Groups["body"].Value.Trim();
        }

        string firstLine = text.Split('\n', 2)[0].Trim();
        if (firstLine.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) ||
            firstLine.StartsWith("graph", StringComparison.OrdinalIgnoreCase))
        {
            text = UnquotedParenthesisLabel().Replace(text, "${id}[\"${label}\"]");
        }

        return text;
    }

    [GeneratedRegex(@"^```(?:mermaid)?[ \t]*\r?\n(?<body>[\s\S]*?)\r?\n```\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FencePattern();

    [GeneratedRegex("(?<id>\\b[A-Za-z_][A-Za-z0-9_]*)\\[(?<label>[^\\[\\]\"\\r\\n]*[()][^\\[\\]\"\\r\\n]*)\\]")]
    private static partial Regex UnquotedParenthesisLabel();
}

/// <summary>
/// Checks Mermaid with the real parser, which runs in the web app's Node process (it needs a
/// DOM shim, so it lives next to the UI that renders diagrams anyway). Fails open: with the
/// web app unreachable the diagram is kept and reported as unchecked, because a plan should
/// never be lost over a diagram.
/// </summary>
internal sealed class FrontendDiagramValidator : IDiagramValidator
{
    public const string HttpClientName = "fleet-diagram";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;

    public FrontendDiagramValidator(IHttpClientFactory httpClientFactory, string baseUrl)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<DiagramCheck> CheckAsync(string code, CancellationToken cancellationToken)
    {
        string fixedCode = DiagramSanitizer.Fix(code);
        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"{_baseUrl}/api/validate-diagram",
                new { code = fixedCode },
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DiagramCheck(true, false, fixedCode, null);
            }

            DiagramCheckResponse? body = await response.Content.ReadFromJsonAsync<DiagramCheckResponse>(cancellationToken);
            return body is null
                ? new DiagramCheck(true, false, fixedCode, null)
                : new DiagramCheck(body.Valid, true, fixedCode, body.Error);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new DiagramCheck(true, false, fixedCode, null);
        }
    }

    private sealed record DiagramCheckResponse(bool Valid, string? Error);
}
