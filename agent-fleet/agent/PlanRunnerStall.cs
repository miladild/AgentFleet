using System.Diagnostics;

namespace AgentFleet;

/// <summary>
/// Watches one model call for silence. Each tool call the model makes starts the count again, so a call that is working is
/// never cut short. A call silent for the whole limit is asked to stop; if it has not returned within the grace after that, the
/// runner stops waiting for it, so a call that ignores its cancellation cannot hold the plan for hours.
/// </summary>
internal sealed class ModelStallWatch(TimeSpan limit, TimeSpan grace)
{
    private long _lastProgress = Stopwatch.GetTimestamp();

    public TimeSpan Limit => limit;

    public void Progress() => Interlocked.Exchange(ref _lastProgress, Stopwatch.GetTimestamp());

    /// <summary>
    /// The call's result, or <see cref="ModelStalledException"/> when it was left behind. A cancellation of the caller is not a
    /// stall: it is rethrown as cancellation once the call has had its grace to stop.
    /// </summary>
    public async Task<T> WaitAsync<T>(Task<T> call, CancellationTokenSource attempt, CancellationToken cancellationToken)
    {
        while (!call.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            TimeSpan silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgress));
            if (silent >= limit)
            {
                break;
            }

            await Task.WhenAny(call, Task.Delay(limit - silent, cancellationToken));
        }

        if (call.IsCompleted)
        {
            return await call;
        }

        attempt.Cancel();
        await Task.WhenAny(call, Task.Delay(grace));
        if (call.Status == TaskStatus.RanToCompletion)
        {
            return call.Result;
        }

        // Nothing waits for a call that is left behind; a fault it reports later is observed here, so it is not lost.
        _ = call.ContinueWith(static finished => _ = finished.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw new ModelStalledException(limit);
    }
}

/// <summary>A model call that stayed silent for the whole stall limit and was left behind.</summary>
internal sealed class ModelStalledException(TimeSpan limit) : Exception("The model call stopped answering.")
{
    public TimeSpan Limit { get; } = limit;
}
