using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// The few moments of a plan run that are worth telling the user about, each told once: a step was parked, the run ended
/// with steps that need the user, the plan is done, the run deadline passed. Kind is the run event's name.
/// </summary>
/// <param name="Cause">What stopped a parked step, in a few fixed words ("check", "environment", "working-time"...). Never output or file contents.</param>
internal sealed record PlanNotice(
    string Kind,
    string PlanId,
    string PlanTitle,
    int StepsDone,
    int StepsParked,
    int StepsTotal,
    int? StepId,
    string? StepTitle,
    string? Cause,
    DateTimeOffset AtUtc);

internal interface IPlanNotifier
{
    /// <summary>Fire and forget: telling the user must never hold up, or break, the run.</summary>
    void Notify(PlanNotice notice);
}

/// <summary>
/// An optional webhook (<c>notifyUrl</c> in the fleet config, off by default) that gets one small JSON message per notice.
/// The fleet is LAN-only, so the address must be on the private network: loopback, a private or link-local address, a
/// Tailscale (100.64.0.0/10) address, or a name that resolves to one. The message carries the plan's and the step's
/// titles, counts and a cause word, never a file's contents or a check's output. Redirects are not followed.
/// </summary>
internal sealed class WebhookPlanNotifier(HttpClient client, string url, ILogger logger) : IPlanNotifier
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Why a URL cannot be a notice address, or null when it can (the name is resolved later, when a notice is sent).</summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return "notifyUrl must be an http or https address.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "notifyUrl must not carry a user name or password.";
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? literal) && !IsPrivate(literal))
        {
            return "notifyUrl must be on the private network (the fleet is LAN-only).";
        }

        return null;
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }

    public static string Body(PlanNotice notice) => JsonSerializer.Serialize(new
    {
        @event = notice.Kind,
        planId = notice.PlanId,
        plan = notice.PlanTitle,
        stepsDone = notice.StepsDone,
        stepsParked = notice.StepsParked,
        stepsTotal = notice.StepsTotal,
        stepId = notice.StepId,
        step = notice.StepTitle,
        cause = notice.Cause,
        at = notice.AtUtc
    });

    public void Notify(PlanNotice notice) => _ = Task.Run(() => SendAsync(notice));

    internal async Task SendAsync(PlanNotice notice)
    {
        try
        {
            if (Problem(url) is { } problem)
            {
                logger.LogWarning("The plan notice was not sent: {Problem}", problem);
                return;
            }

            var uri = new Uri(url.Trim());
            IPAddress[] addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.Host);
            if (addresses.Length == 0 || addresses.Any(address => !IsPrivate(address)))
            {
                logger.LogWarning("The plan notice was not sent: {Host} does not resolve to a private address (the fleet is LAN-only).", uri.Host);
                return;
            }

            using var cancellation = new CancellationTokenSource(Timeout);
            using var content = new StringContent(Body(notice), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(uri, content, cancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The plan notice to {Host} was answered with HTTP {Status}.", uri.Host, (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or SocketException or InvalidOperationException)
        {
            logger.LogWarning("The plan notice could not be sent: {Message}", exception.Message);
        }
    }
}
