using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>
/// Keeps every node's health snapshot fresh proactively, in the background - rather
/// than only ever probing lazily when a chat request happens to route to that node, or
/// when someone has the web UI's status panel open polling /api/fleet-status. Without
/// this, a node coming back online (a Wi-Fi worker can flap between reachable/unreachable
/// throughout normal operation) might not be reflected anywhere until something else
/// incidentally triggers a probe - which could be indefinitely, if nothing does.
///
/// Polls at the same cadence as FleetOptions.HealthCacheDuration, so any consumer
/// reading a cached snapshot is never looking at data older than one poll interval -
/// the cache is always warm from this service's own writes, not from whoever happened
/// to ask last.
///
/// Also tracks which local address this machine uses to reach each node. Workers
/// commonly firewall Ollama to the hub's address only, and a DHCP change on the hub
/// then makes every probe time out while SSH (usually open to the whole subnet) keeps
/// working - a real incident here. Logging the address and any change to it makes that
/// failure name itself instead of looking like a dead node.
/// </summary>
internal sealed class FleetHealthBackgroundService : BackgroundService
{
    private readonly FleetHealthMonitor _healthMonitor;
    private readonly FleetOptions _options;
    private readonly ILogger<FleetHealthBackgroundService> _logger;
    private readonly Dictionary<string, bool> _lastReady = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastSourceAddress = new(StringComparer.Ordinal);

    public FleetHealthBackgroundService(
        FleetHealthMonitor healthMonitor,
        FleetOptions options,
        ILogger<FleetHealthBackgroundService> logger)
    {
        _healthMonitor = healthMonitor;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.HealthCacheDuration);
        do
        {
            await ProbeAllAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        foreach (FleetNodeDefinition node in _options.Nodes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                string? sourceAddress = ResolveSourceAddress(node);
                TrackSourceAddress(node.Name, sourceAddress);

                NodeHealthSnapshot snapshot = await _healthMonitor.GetNodeAsync(
                    node.Name,
                    cancellationToken,
                    forceProbe: true);
                LogTransition(node.Name, snapshot, sourceAddress);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Background health probe for {NodeName} failed unexpectedly.", node.Name);
            }
        }
    }

    // A UDP "connect" sends nothing: it only makes the OS pick the route and source
    // address it would use to reach the node, which is exactly what a firewall on the
    // node sees. Loopback nodes (the hub talking to itself) have nothing to report.
    private static string? ResolveSourceAddress(FleetNodeDefinition node)
    {
        try
        {
            IPAddress[] targets = Dns.GetHostAddresses(node.OpenAiEndpoint.Host);
            IPAddress? target = targets.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                ?? targets.FirstOrDefault();
            if (target is null || IPAddress.IsLoopback(target))
            {
                return null;
            }

            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(target, node.OpenAiEndpoint.Port);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private void TrackSourceAddress(string nodeName, string? sourceAddress)
    {
        if (sourceAddress is null)
        {
            return;
        }

        if (_lastSourceAddress.TryGetValue(nodeName, out string? previous) && previous != sourceAddress)
        {
            _logger.LogWarning(
                "This machine's address toward {NodeName} changed from {OldAddress} to {NewAddress}. " +
                "If that node's firewall only allows the old address, requests will time out until its rule is updated.",
                nodeName,
                previous,
                sourceAddress);
        }

        _lastSourceAddress[nodeName] = sourceAddress;
    }

    private void LogTransition(string nodeName, NodeHealthSnapshot snapshot, string? sourceAddress)
    {
        bool isFirstObservation = !_lastReady.TryGetValue(nodeName, out bool previouslyReady);
        _lastReady[nodeName] = snapshot.Ready;

        // The first probe of each node ever has no prior state to compare against -
        // treat that as a baseline, not a transition, so startup doesn't log every
        // node as if it just changed.
        if (isFirstObservation || previouslyReady == snapshot.Ready)
        {
            return;
        }

        if (snapshot.Ready)
        {
            _logger.LogInformation("Fleet node {NodeName} is back online.", nodeName);
        }
        else
        {
            _logger.LogWarning(
                "Fleet node {NodeName} went offline ({Failure}), probing from {SourceAddress}.",
                nodeName,
                snapshot.Failure ?? "unknown",
                sourceAddress ?? "local");
        }
    }
}
