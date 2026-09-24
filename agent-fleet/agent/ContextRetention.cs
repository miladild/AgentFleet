namespace AgentFleet;

/// <summary>One chat a cleanup deleted, or would delete.</summary>
internal sealed record ContextCleanupItem(string Id, string Title, DateTimeOffset LastActivityUtc, int MessageCount);

/// <summary>What a cleanup did, or with <see cref="DryRun"/> what it would do.</summary>
internal sealed record ContextCleanupResult(
    bool DryRun,
    int? OlderThanDays,
    IReadOnlyList<ContextCleanupItem> Chats,
    int KeptForPlans,
    int DeliveriesTrimmed,
    long BytesBefore,
    long BytesAfter);

/// <summary>
/// Keeps the durable record from growing forever. Two rules:
/// <list type="number">
/// <item>Always: each chat keeps only its newest delivered context blocks. There is one per model call and they
/// are the bulkiest thing in the record, but they are only a copy of what a model was handed.</item>
/// <item>When history.deleteAfterDays is set: a chat in which nothing has happened for that long is deleted
/// with its whole record. A chat an unfinished plan runs in is kept, since the plan still needs it.</item>
/// </list>
/// </summary>
internal static class ContextRetention
{
    public const int KeepDeliveriesPerContext = 30;

    public static ContextCleanupResult Run(
        FleetContextStore store,
        int? olderThanDays,
        IReadOnlySet<string> unfinishedPlanContexts,
        DateTimeOffset now,
        bool dryRun,
        bool shrink = false)
    {
        long before = store.Storage().Bytes;
        var chats = new List<ContextCleanupItem>();
        int keptForPlans = 0;
        if (olderThanDays is int days && days > 0)
        {
            foreach (FleetContextSummary context in store.ContextsIdleSince(now.AddDays(-days)))
            {
                if (unfinishedPlanContexts.Contains(context.Id))
                {
                    keptForPlans++;
                    continue;
                }

                chats.Add(new ContextCleanupItem(context.Id, context.Title, context.UpdatedAtUtc, context.MessageCount));
            }
        }

        if (dryRun)
        {
            return new ContextCleanupResult(true, olderThanDays, chats, keptForPlans, 0, before, before);
        }

        if (chats.Count > 0)
        {
            store.DeleteContexts(chats.Select(chat => chat.Id).ToList());
        }

        int trimmed = store.TrimDeliveries(KeepDeliveriesPerContext);
        if (shrink && (chats.Count > 0 || trimmed > 0))
        {
            store.Shrink();
        }

        return new ContextCleanupResult(false, olderThanDays, chats, keptForPlans, trimmed, before, store.Storage().Bytes);
    }
}

/// <summary>
/// Runs <see cref="ContextRetention"/> a few minutes after startup and then every six hours, with the setting
/// read fresh each time so a change in the Config panel applies without a restart.
/// </summary>
internal sealed class ContextRetentionService(
    FleetContextStore store,
    FleetPlanStore plans,
    FleetConfigStore config,
    ILogger<ContextRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                RunOnce();
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal ContextCleanupResult? RunOnce()
    {
        try
        {
            int? days = config.Current.History?.DeleteAfterDays;
            ContextCleanupResult result = ContextRetention.Run(store, days, plans.UnfinishedContextIds(), DateTimeOffset.UtcNow, dryRun: false);
            if (result.Chats.Count > 0 || result.DeliveriesTrimmed > 0)
            {
                logger.LogInformation(
                    "History cleanup: deleted {Chats} chat(s) not used for {Days} days, kept {Kept} for unfinished plans, trimmed {Deliveries} old delivered context block(s).",
                    result.Chats.Count, days ?? 0, result.KeptForPlans, result.DeliveriesTrimmed);
            }

            return result;
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "History cleanup failed; it will try again in {Hours} hours.", Interval.TotalHours);
            return null;
        }
    }
}
