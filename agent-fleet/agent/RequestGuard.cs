using System.Net;

namespace AgentFleet;

/// <summary>
/// Protects a service that has no login from being driven by a web page. A page you visit in a
/// browser can make your browser send requests to this backend (http://localhost:8000), and with
/// DNS rebinding a hostile site can even make those requests look same-origin. Both are stopped
/// by insisting that the address the request was sent to is one this machine is meant to answer to.
///
/// Allowed: localhost, any IP address written out as digits (an attacker cannot rebind those),
/// this machine's own name, and names listed in FLEET_ALLOWED_HOSTS. Requests carrying an Origin
/// header (which only browsers add) must name an allowed host as well.
/// </summary>
internal static class RequestGuard
{
    public static IReadOnlyList<string> ParseAllowedNames(string? setting) =>
        (setting ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    public static bool IsAllowedHost(string? hostHeader, IReadOnlyCollection<string> extraNames)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        string host = HostOnly(hostHeader.Trim());
        if (host.Length == 0)
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }

        string machine = Environment.MachineName;
        if (host.Equals(machine, StringComparison.OrdinalIgnoreCase) ||
            host.Equals(machine + ".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return extraNames.Contains(host, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAllowedOrigin(string? origin, IReadOnlyCollection<string> extraNames) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "http" or "https" &&
        IsAllowedHost(uri.Host, extraNames);

    // "host", "host:8000", "[::1]" and "[::1]:8000" all reduce to the host alone.
    private static string HostOnly(string hostHeader)
    {
        if (hostHeader.StartsWith('['))
        {
            int end = hostHeader.IndexOf(']');
            return end > 0 ? hostHeader[1..end] : string.Empty;
        }

        int colon = hostHeader.LastIndexOf(':');
        return colon >= 0 ? hostHeader[..colon] : hostHeader;
    }

    public static void Use(IApplicationBuilder app, IReadOnlyCollection<string> extraNames)
    {
        app.Use(async (context, next) =>
        {
            bool hostOk = IsAllowedHost(context.Request.Host.Value, extraNames);
            bool originOk = !context.Request.Headers.TryGetValue("Origin", out var origin) ||
                            IsAllowedOrigin(origin.ToString(), extraNames);
            if (hostOk && originOk)
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(
                "Refused: the fleet only answers requests addressed to localhost, an IP address, or this machine's own name. " +
                "If you reach it by another name, list that name in FLEET_ALLOWED_HOSTS.");
        });
    }
}
