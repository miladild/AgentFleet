using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// Internet access for the fleet's models, running entirely on the hub - no new
/// machine, no container, no account or API key. Search goes through DuckDuckGo's
/// plain-HTML endpoint (html.duckduckgo.com/html/), the same lite, JS-free page
/// simple clients and CLI tools commonly use; no official API, no key, no per-query
/// cost. Fetch returns readable text (scripts/styles/nav stripped, HTML entities
/// decoded), not raw markup, since that's what these models can actually use. Same
/// no-allowlist, no-approval-gate posture as every other tool in this fleet.
/// </summary>
internal static class WebTools
{
    public const string HttpClientName = "fleet-web";

    private const int DefaultMaxResults = 5;
    private const int HardMaxResults = 10;
    private const int MaxFetchCharacters = 12_000;
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AgentFleet/1.0";

    public static async Task<string> SearchWebAsync(
        string query,
        int? maxResults,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: query must not be empty.";
        }

        int limit = Math.Clamp(maxResults is > 0 ? maxResults.Value : DefaultMaxResults, 1, HardMaxResults);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
        request.Headers.UserAgent.ParseAdd(UserAgent);

        HttpResponseMessage response;
        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "web_search failed for query '{Query}'.", query);
            return $"Error: could not reach the search engine: {exception.Message}";
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return $"Error: search engine returned HTTP {(int)response.StatusCode}.";
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            List<HtmlNode> titleNodes = (doc.DocumentNode.SelectNodes("//a[contains(concat(' ', normalize-space(@class), ' '), ' result__a ')]")
                ?? Enumerable.Empty<HtmlNode>()).ToList();
            List<HtmlNode> snippetNodes = (doc.DocumentNode.SelectNodes("//*[contains(concat(' ', normalize-space(@class), ' '), ' result__snippet ')]")
                ?? Enumerable.Empty<HtmlNode>()).ToList();

            var results = new List<string>();
            for (int i = 0; i < titleNodes.Count && results.Count < limit; i++)
            {
                string title = WebUtility.HtmlDecode(titleNodes[i].InnerText).Trim();
                string url = ResolveDuckDuckGoUrl(titleNodes[i].GetAttributeValue("href", ""));
                if (title.Length == 0 || url.Length == 0)
                {
                    continue;
                }

                string snippet = i < snippetNodes.Count ? WebUtility.HtmlDecode(snippetNodes[i].InnerText).Trim() : "";
                results.Add($"{results.Count + 1}. {title}\n   {url}\n   {snippet}");
            }

            return results.Count == 0 ? $"No results found for \"{query}\"." : string.Join("\n\n", results);
        }
    }

    public static async Task<string> FetchUrlAsync(
        string url,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https"))
        {
            return "Error: url must be an absolute http(s) URL.";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, parsed);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        HttpResponseMessage response;
        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "web_fetch failed for url '{Url}'.", url);
            return $"Error: could not reach {url}: {exception.Message}";
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return $"Error: {url} returned HTTP {(int)response.StatusCode}.";
            }

            string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return Truncate(body, url);
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(body);

            foreach (HtmlNode node in (doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//noscript|//header")
                ?? Enumerable.Empty<HtmlNode>()).ToList())
            {
                node.Remove();
            }

            string text = WebUtility.HtmlDecode(doc.DocumentNode.InnerText);
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @"[ \t]*\n[ \t]*", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

            return Truncate(text, url);
        }
    }

    private static string Truncate(string text, string url)
    {
        if (text.Length <= MaxFetchCharacters)
        {
            return text;
        }

        return text[..MaxFetchCharacters] +
            $"\n\n[truncated: {url} returned {text.Length} characters, showing first {MaxFetchCharacters}]";
    }

    // DuckDuckGo's HTML results wrap each link in a redirect:
    // //duckduckgo.com/l/?uddg=<url-encoded-real-url>&rut=...
    private static string ResolveDuckDuckGoUrl(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return "";
        }

        int uddgIndex = href.IndexOf("uddg=", StringComparison.Ordinal);
        if (uddgIndex < 0)
        {
            return href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href;
        }

        string encoded = href[(uddgIndex + 5)..];
        int ampersand = encoded.IndexOf('&');
        if (ampersand >= 0)
        {
            encoded = encoded[..ampersand];
        }

        return Uri.UnescapeDataString(encoded);
    }
}
