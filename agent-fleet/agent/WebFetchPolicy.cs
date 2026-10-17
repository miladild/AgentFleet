using System.Net;
using System.Net.Sockets;

namespace AgentFleet;

internal static class WebFetchPolicy
{
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".internal", ".localhost"];

    public static IReadOnlyList<string> NormalizeAllowedInternalHosts(IReadOnlyList<string>? hosts)
    {
        if (hosts is null)
        {
            return [];
        }

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? entry in hosts)
        {
            string host = CanonicalHost(entry ?? "");
            if (host.Length == 0 || (IPAddress.TryParse(host, out _) == false && Uri.CheckHostName(host) != UriHostNameType.Dns))
            {
                throw new InvalidOperationException(
                    "tools.webFetch.allowedInternalHosts entries must be exact host names or IP addresses.");
            }

            normalized.Add(host);
        }

        return normalized.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsAllowedInternalHost(string host, IReadOnlyList<string> allowedHosts) =>
        allowedHosts.Any(allowed => string.Equals(CanonicalHost(host), CanonicalHost(allowed), StringComparison.OrdinalIgnoreCase));

    public static string? Problem(Uri uri, FleetConfig config)
    {
        string host = CanonicalHost(uri.IdnHost);
        (HashSet<string> nodeHosts, HashSet<string> nodeAddresses) = FleetNodeDestinations(config);
        if (nodeHosts.Contains(host) || (IPAddress.TryParse(host, out IPAddress? literal) && nodeAddresses.Contains(CanonicalHost(literal.ToString()))))
        {
            return host;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return null;
        }

        bool explicitlyAllowed = IsAllowedInternalHost(host, config.WebFetchAllowedInternalHosts);
        string name = host.TrimEnd('.');
        if (!explicitlyAllowed && (!name.Contains('.') || LocalSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))))
        {
            return host;
        }

        return null;
    }

    public static bool IsBlockedAddress(IPAddress address, Uri uri, FleetConfig config, IReadOnlySet<string> fleetNodeAddresses)
    {
        if (IsBlockedTarget(address, address, uri, config, fleetNodeAddresses))
        {
            return true;
        }

        // An IPv6 form that carries an IPv4 address reaches that IPv4 address, so it is judged by it too.
        IPAddress? embedded = IpAddressPolicy.EmbeddedIPv4(address);
        return embedded is not null && IsBlockedTarget(embedded, address, uri, config, fleetNodeAddresses);
    }

    // The allow-list may name the host, the requested address or the embedded IPv4 address.
    private static bool IsBlockedTarget(
        IPAddress target,
        IPAddress requested,
        Uri uri,
        FleetConfig config,
        IReadOnlySet<string> fleetNodeAddresses)
    {
        if (fleetNodeAddresses.Contains(CanonicalHost(target.ToString())) || IpAddressPolicy.IsUnspecifiedMulticastOrBroadcast(target))
        {
            return true;
        }

        return IpAddressPolicy.IsPrivate(target) &&
               !IsAllowedInternalHost(uri.IdnHost, config.WebFetchAllowedInternalHosts) &&
               !IsAllowedInternalHost(requested.ToString(), config.WebFetchAllowedInternalHosts) &&
               !IsAllowedInternalHost(target.ToString(), config.WebFetchAllowedInternalHosts);
    }

    // A fleet node configured by host name is blocked by the addresses that name resolves to, so an alias
    // for the node cannot be fetched. A lookup that fails is ignored, so an unresolvable node name never
    // blocks an ordinary page; the cancellation token bounds how long the lookups can take.
    public static async Task<IReadOnlySet<string>> FleetNodeAddressesAsync(
        FleetConfig config,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        (HashSet<string> hosts, HashSet<string> addresses) = FleetNodeDestinations(config);
        foreach (string host in hosts)
        {
            if (IPAddress.TryParse(host, out _))
            {
                continue;
            }

            try
            {
                foreach (IPAddress address in await resolve(host, cancellationToken))
                {
                    addresses.Add(CanonicalHost(address.ToString()));
                }
            }
            catch (Exception)
            {
                // A node name that cannot be resolved has no address to block.
            }
        }

        return addresses;
    }

    public static Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
            ? Task.FromResult(new[] { literal })
            : Dns.GetHostAddressesAsync(host, cancellationToken);

    public static SocketsHttpHandler CreateHandler(Uri uri, IReadOnlyList<IPAddress> checkedAddresses) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = (context, cancellationToken) =>
        {
            if (!string.Equals(CanonicalHost(context.DnsEndPoint.Host), CanonicalHost(uri.IdnHost), StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("The connection host did not match the checked web_fetch URL.");
            }

            return ConnectToCheckedAddressesAsync(context.DnsEndPoint, checkedAddresses, ConnectSocketAsync, cancellationToken);
        }
    };

    internal static async ValueTask<Stream> ConnectToCheckedAddressesAsync(
        DnsEndPoint endpoint,
        IReadOnlyList<IPAddress> checkedAddresses,
        Func<string, int, CancellationToken, ValueTask<Stream>> connect,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (IPAddress address in checkedAddresses)
        {
            try
            {
                return await connect(address.ToString(), endpoint.Port, cancellationToken);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastError = exception;
            }
        }

        throw new HttpRequestException("Could not connect to a checked address.", lastError);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(string checkedAddress, int port, CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(checkedAddress);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static (HashSet<string> Hosts, HashSet<string> Addresses) FleetNodeDestinations(FleetConfig config)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FleetNodeConfig node in config.Nodes)
        {
            if (!Uri.TryCreate(node.Url, UriKind.Absolute, out Uri? uri))
            {
                continue;
            }

            string host = CanonicalHost(uri.IdnHost);
            hosts.Add(host);
            if (IPAddress.TryParse(host, out IPAddress? address))
            {
                addresses.Add(CanonicalHost(address.ToString()));
            }
        }

        return (hosts, addresses);
    }

    private static string CanonicalHost(string host)
    {
        string trimmed = host.Trim().Trim('[', ']').TrimEnd('.');
        return IPAddress.TryParse(trimmed, out IPAddress? address)
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString()
            : trimmed.ToLowerInvariant();
    }
}
