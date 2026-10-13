using System.Collections.Concurrent;
using System.Globalization;

namespace AgentFleet;

/// <summary>Tracks the machines whose model load is currently being measured.</summary>
internal sealed class MeasuringMachines
{
    private readonly ConcurrentDictionary<string, byte> _addresses = new(StringComparer.OrdinalIgnoreCase);

    public void Begin(Uri root) => _addresses[NormalizeAddress(root)] = 0;

    public void End(Uri root) => _addresses.TryRemove(NormalizeAddress(root), out _);

    public bool IsMeasuring(Uri root) => _addresses.ContainsKey(NormalizeAddress(root));

    internal static string NormalizeAddress(Uri root) =>
        $"{root.IdnHost.Trim('[', ']').ToLowerInvariant()}:{root.Port.ToString(CultureInfo.InvariantCulture)}";
}
