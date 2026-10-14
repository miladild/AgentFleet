namespace AgentFleet.Tests;

/// <summary>
/// A clock that tests move by hand. A timer fires when the clock is advanced past its due time, so a test that depends
/// on a timeout runs at once instead of waiting for the time to pass. Timers and GetTimestamp run on a monotonic count
/// that only Advance moves; SetUtcNow moves the wall clock alone, as a machine's clock correction does.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private long _elapsedTicks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _elapsedTicks;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that has come due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            _elapsedTicks += by.Ticks;
            due = _timers.Where(timer => timer.DueAt is { } dueAt && dueAt <= _elapsedTicks).ToList();
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>Sets the wall clock only. Timers that are running keep their due times.</summary>
    public void SetUtcNow(DateTimeOffset now)
    {
        lock (_gate)
        {
            _now = now;
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        // Read and written only under the clock's lock.
        public long? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._elapsedTicks + dueTime.Ticks;
                if (DueAt is not null && !clock._timers.Contains(this))
                {
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire()
        {
            lock (clock._gate)
            {
                DueAt = null;
                clock._timers.Remove(this);
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                DueAt = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
