using System.Collections.Concurrent;

namespace AgentFleet;

internal sealed record FleetActivityEntry(DateTimeOffset TimestampUtc, string Node, string Reason);

/// <summary>
/// Small in-memory ring buffer of recent routing decisions, for the status panel.
/// Not persisted - restarting the backend clears it, which is fine for a "what's it
/// been doing lately" view.
/// </summary>
internal sealed class FleetActivityLog
{
    private const int MaxEntries = 25;
    private readonly ConcurrentQueue<FleetActivityEntry> _entries = new();

    public void Record(string node, string reason)
    {
        _entries.Enqueue(new FleetActivityEntry(DateTimeOffset.UtcNow, node, reason));
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<FleetActivityEntry> Recent() => _entries.Reverse().ToList();
}
