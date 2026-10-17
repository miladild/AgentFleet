using System.Net;
using System.Net.Sockets;

namespace AgentFleet;

internal static class IpAddressPolicy
{
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

    // IPv6 forms that carry an IPv4 address and reach it: IPv4-compatible (::/96), 6to4 (2002::/16) and
    // NAT64 (64:ff9b::/96). Plain :: and ::1 return null so they keep their IPv6 classification.
    // IsPrivate deliberately does not use this: the webhook notifier needs its answer unchanged.
    public static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6Loopback))
        {
            return null;
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes[..12].All(b => b == 0))
        {
            return new IPAddress(bytes[12..16]);
        }

        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return new IPAddress(bytes[2..6]);
        }

        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4..12].All(b => b == 0))
        {
            return new IPAddress(bytes[12..16]);
        }

        return null;
    }

    public static bool IsUnspecifiedMulticastOrBroadcast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast;
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 0 ||
               (bytes[0] is >= 224 and <= 239) ||
               address.Equals(IPAddress.Broadcast);
    }
}
