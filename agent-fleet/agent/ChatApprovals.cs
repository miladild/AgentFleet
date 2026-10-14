using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>How a chat tool call that waited for the user ended.</summary>
internal enum ApprovalDecision
{
    Approved,
    Rejected,
    TimedOut
}

/// <summary>A chat tool call waiting for the user, as the web UI lists it.</summary>
/// <param name="Arguments">
/// The call's arguments as JSON, cut to <see cref="ChatApprovals.MaxArgumentCharacters"/> characters. The list is for
/// deciding; the call itself is still written into the durable record when it runs.
/// </param>
internal sealed record PendingApproval(
    string Id,
    string ContextId,
    string Tool,
    string Arguments,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    /// <summary>Whole seconds left before the call is refused, never negative.</summary>
    public int SecondsLeft(DateTimeOffset now) => Math.Max(0, (int)(ExpiresAtUtc - now).TotalSeconds);
}

/// <summary>
/// Chat tool calls that wait for the user's answer. The function invoker asks here only when chat approvals are on
/// (FleetChatApprovalConfig, off by default), so with the setting off nothing is listed and nothing waits. A waiting
/// call holds its chat request open. The web UI lists the calls with Pending() and answers one with Decide(); a call
/// nobody answers is refused when its time runs out. Every outcome removes the call from the list, and each call is
/// answered once.
/// </summary>
internal sealed class ChatApprovals
{
    public const int MaxArgumentCharacters = 2000;

    private readonly TimeProvider _time;
    private readonly Func<int> _timeoutSeconds;
    private readonly ILogger _logger;

    // Guards the list only. An outcome is completed after the lock is released, so no continuation runs under it.
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);

    /// <param name="time">
    /// The clock the timeout runs on. Tests pass a manual clock, so no test waits for real time.
    /// </param>
    /// <param name="timeoutSeconds">
    /// Read for each call, so a change to the setting applies to the next call that waits.
    /// </param>
    public ChatApprovals(TimeProvider time, Func<int> timeoutSeconds, ILogger logger)
    {
        _time = time;
        _timeoutSeconds = timeoutSeconds;
        _logger = logger;
    }

    /// <summary>
    /// Waits until the user answers, the time runs out, or the chat is cancelled. A cancelled wait throws
    /// OperationCanceledException like any other cancelled await, and still removes the call from the list.
    /// </summary>
    public async Task<ApprovalDecision> RequestAsync(string contextId, string tool, string argumentsJson, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan lifetime = TimeSpan.FromSeconds(_timeoutSeconds());
        var item = new PendingApproval(
            Guid.NewGuid().ToString("N"),
            contextId,
            tool,
            Shorten(argumentsJson),
            now,
            now + lifetime);
        var entry = new Entry(item, _time.GetTimestamp() + (long)(lifetime.TotalSeconds * _time.TimestampFrequency));

        // The timer is set before the call is listed, so every call the list shows can time out.
        using ITimer timer = _time.CreateTimer(_ => Complete(item.Id, ApprovalDecision.TimedOut), null, lifetime, Timeout.InfiniteTimeSpan);
        // Every path from here on takes the call off the list, including a log write that fails.
        try
        {
            lock (_gate)
            {
                _pending.Add(item.Id, entry);
            }

            _logger.LogInformation(
                "Chat tool {Tool} in chat {ContextId} waits for approval (request {Id}, up to {Seconds} s).",
                tool, contextId, item.Id, (int)lifetime.TotalSeconds);
            ApprovalDecision decision = await entry.Outcome.Task.WaitAsync(cancellationToken);
            _logger.LogInformation(
                "Chat tool {Tool} in chat {ContextId}: approval request {Id} ended as {Decision}.",
                tool, contextId, item.Id, decision);
            return decision;
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(item.Id);
            }
        }
    }

    /// <summary>
    /// The calls waiting now, oldest first. The time left comes from the monotonic clock, so ExpiresAtUtc is that time
    /// counted from the wall clock now: a change of the wall clock does not stretch or shorten the countdown.
    /// </summary>
    public IReadOnlyList<PendingApproval> Pending()
    {
        DateTimeOffset now = _time.GetUtcNow();
        long timestamp = _time.GetTimestamp();
        lock (_gate)
        {
            return _pending.Values
                .Select(entry => entry.Item with
                {
                    ExpiresAtUtc = now + _time.GetElapsedTime(timestamp, entry.Deadline)
                })
                .OrderBy(item => item.RequestedAtUtc)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// The user's answer to a waiting call. False when the call is not waiting: it is unknown, or it was already
    /// answered or has timed out.
    /// </summary>
    public bool Decide(string id, bool approve) =>
        Complete(id, approve ? ApprovalDecision.Approved : ApprovalDecision.Rejected);

    // Whoever removes the call from the list decides it. Every later answer, and the timeout if the user got there
    // first, finds nothing to remove.
    private bool Complete(string id, ApprovalDecision decision)
    {
        Entry? entry = null;
        lock (_gate)
        {
            _pending.Remove(id, out entry);
        }

        if (entry is null)
        {
            return false;
        }

        entry.Outcome.TrySetResult(decision);
        return true;
    }

    // A long file body is what makes an argument list long, and the list only has to show enough to decide.
    private static string Shorten(string text) =>
        text.Length <= MaxArgumentCharacters ? text : text[..(MaxArgumentCharacters - 3)] + "...";

    private sealed class Entry(PendingApproval item, long deadline)
    {
        public PendingApproval Item { get; } = item;

        // On the monotonic clock (TimeProvider.GetTimestamp), when the call is refused.
        public long Deadline { get; } = deadline;

        public TaskCompletionSource<ApprovalDecision> Outcome { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
