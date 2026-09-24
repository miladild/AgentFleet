using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace AgentFleet.Tests;

public sealed class ContextRetentionTests : ContextTestBase
{
    private static readonly IReadOnlySet<string> NoPlans = new HashSet<string>();

    // Makes a context look as if nothing has happened in it for the given number of days.
    private void Age(string contextId, int days)
    {
        using var connection = new SqliteConnection($"Data Source={Store.DatabasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE contexts SET updated_utc=@at WHERE id=@id;";
        command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@id", contextId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private string Chat(string text)
    {
        string id = NewContextId();
        Store.UpsertMessages(id, text, Messages(("m1", "user", text)), "test");
        return id;
    }

    // Reads the database and its write-ahead log the way any other program could, while the store is open.
    private string RawFileText()
    {
        var text = new StringBuilder();
        foreach (string path in new[] { Store.DatabasePath, Store.DatabasePath + "-wal" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            text.Append(Encoding.UTF8.GetString(memory.ToArray()));
        }

        return text.ToString();
    }

    [Fact]
    public void A_chat_idle_longer_than_the_limit_is_deleted_with_its_record_and_a_recent_one_is_kept()
    {
        string old = Chat("an old question");
        Store.AppendEvent(old, FleetContextEventKind.Decision, new { text = "use tabs" }, actor: "user", pinned: true);
        string recent = Chat("a recent question");
        Age(old, 40);
        Age(recent, 3);

        ContextCleanupResult result = ContextRetention.Run(Store, 30, NoPlans, DateTimeOffset.UtcNow, dryRun: false);

        Assert.Equal([old], result.Chats.Select(chat => chat.Id));
        Assert.Null(Store.GetContext(old));
        Assert.Empty(Store.AllEvents(old));
        Assert.NotNull(Store.GetContext(recent));
    }

    [Fact]
    public void A_dry_run_lists_the_chats_it_would_delete_and_deletes_nothing()
    {
        string old = Chat("an old question");
        Age(old, 100);

        ContextCleanupResult preview = ContextRetention.Run(Store, 90, NoPlans, DateTimeOffset.UtcNow, dryRun: true);

        ContextCleanupItem item = Assert.Single(preview.Chats);
        Assert.Equal(old, item.Id);
        Assert.Equal("an old question", item.Title);
        Assert.True(preview.DryRun);
        Assert.NotNull(Store.GetContext(old));
    }

    [Fact]
    public void A_chat_an_unfinished_plan_runs_in_is_kept_however_old_it_is()
    {
        string planChat = Chat("plan a refactor");
        Age(planChat, 400);

        ContextCleanupResult result = ContextRetention.Run(Store, 30, new HashSet<string> { planChat }, DateTimeOffset.UtcNow, dryRun: false);

        Assert.Empty(result.Chats);
        Assert.Equal(1, result.KeptForPlans);
        Assert.NotNull(Store.GetContext(planChat));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void With_no_limit_set_no_chat_is_deleted(int? days)
    {
        string old = Chat("an old question");
        Age(old, 3000);

        ContextCleanupResult result = ContextRetention.Run(Store, days, NoPlans, DateTimeOffset.UtcNow, dryRun: false);

        Assert.Empty(result.Chats);
        Assert.NotNull(Store.GetContext(old));
    }

    [Fact]
    public void Only_the_newest_delivered_blocks_of_each_chat_are_kept_and_other_events_are_untouched()
    {
        string first = Chat("first");
        string second = Chat("second");
        for (int i = 0; i < ContextRetention.KeepDeliveriesPerContext + 5; i++)
        {
            Store.AppendEvent(first, FleetContextEventKind.ContextAssembled, new { block = $"block {i}" });
        }

        long pinned = Store.AppendEvent(first, FleetContextEventKind.ContextAssembled, new { block = "kept on purpose" });
        Store.PinEvent(first, pinned, true);
        for (int i = 0; i < ContextRetention.KeepDeliveriesPerContext + 5; i++)
        {
            Store.AppendEvent(first, FleetContextEventKind.ContextAssembled, new { block = $"later block {i}" });
        }

        Store.AppendEvent(second, FleetContextEventKind.ContextAssembled, new { block = "only one" });
        int messagesBefore = Store.EventsOfKinds(first, [FleetContextEventKind.Message], 500).Count;

        ContextCleanupResult result = ContextRetention.Run(Store, null, NoPlans, DateTimeOffset.UtcNow, dryRun: false);

        Assert.Equal(ContextRetention.KeepDeliveriesPerContext + 10, result.DeliveriesTrimmed);
        IReadOnlyList<FleetContextEvent> kept = Store.EventsOfKinds(first, [FleetContextEventKind.ContextAssembled], 500);
        Assert.Equal(ContextRetention.KeepDeliveriesPerContext + 1, kept.Count);
        Assert.Contains(kept, e => e.Id == pinned);
        Assert.Contains("later block " + (ContextRetention.KeepDeliveriesPerContext + 4), kept[^1].Payload.GetRawText());
        Assert.Single(Store.EventsOfKinds(second, [FleetContextEventKind.ContextAssembled], 500));
        Assert.Equal(messagesBefore, Store.EventsOfKinds(first, [FleetContextEventKind.Message], 500).Count);
    }

    [Fact]
    public void A_deleted_chat_is_gone_from_the_files_on_disk_not_only_from_the_lists()
    {
        const string marker = "SECRET-MARKER-7f3a9c";
        string id = Chat("a chat");
        Store.AppendEvent(id, FleetContextEventKind.ToolResult, new { tool = "read_file", result = marker + " from a file the assistant read" });
        string keep = Chat("another chat");
        Assert.Contains(marker, RawFileText());

        Assert.True(Store.DeleteContext(id));

        Assert.DoesNotContain(marker, RawFileText());
        Assert.NotNull(Store.GetContext(keep));
    }

    [Fact]
    public void A_cleanup_that_shrinks_gives_the_space_back()
    {
        string big = Chat("a long one");
        string filler = new('x', 4000);
        for (int i = 0; i < 300; i++)
        {
            Store.AppendEvent(big, FleetContextEventKind.ToolResult, new { result = filler + i });
        }

        Age(big, 60);
        long before = Store.Storage().Bytes;

        ContextCleanupResult result = ContextRetention.Run(Store, 30, NoPlans, DateTimeOffset.UtcNow, dryRun: false, shrink: true);

        Assert.Single(result.Chats);
        Assert.True(before > 1_000_000, $"expected a large file, got {before}");
        Assert.True(result.BytesAfter < before / 4, $"expected the file to shrink from {before}, got {result.BytesAfter}");
        Assert.Equal(0, Store.Storage().Contexts);
    }

    [Fact]
    public void Storage_counts_chats_events_and_the_oldest_activity()
    {
        string a = Chat("a");
        Chat("b");
        Store.EnsureContext(NewContextId());
        Age(a, 12);

        FleetContextStorage storage = Store.Storage();

        Assert.Equal(3, storage.Contexts);
        Assert.Equal(2, storage.Chats);
        Assert.True(storage.Events >= 2);
        Assert.True(storage.Bytes > 0);
        Assert.InRange((DateTimeOffset.UtcNow - storage.OldestActivityUtc!.Value).TotalDays, 11.9, 12.1);
    }

    [Fact]
    public void The_plan_store_reports_the_contexts_of_unfinished_plans_only()
    {
        var plans = new FleetPlanStore(Configuration);
        PlanRecord waiting = plans.Create("t", "g", null, null, null, null, null, null, [new PlanStepInput("step", "do it")]);
        PlanRecord done = plans.Create("t", "g", null, null, null, null, null, null, [new PlanStepInput("step", "do it")]);
        string waitingContext = NewContextId();
        string doneContext = NewContextId();
        plans.Update(waiting.Id, plan => plan with { ContextId = waitingContext });
        plans.Update(done.Id, plan => plan with { ContextId = doneContext, Status = PlanStatus.Done });

        IReadOnlySet<string> unfinished = plans.UnfinishedContextIds();

        Assert.Contains(waitingContext, unfinished);
        Assert.DoesNotContain(doneContext, unfinished);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(FleetConfig.MaxHistoryDays + 1, false)]
    public void The_history_setting_is_checked_when_saved_and_survives_a_reload(int days, bool valid)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = Path.Combine(Root, "fleet.config.json") })
            .Build();
        var config = new FleetConfigStore(configuration);

        FleetConfig next = config.Current with { History = new FleetHistoryConfig(days) };
        if (!valid)
        {
            Assert.Throws<InvalidOperationException>(() => config.Save(next));
            return;
        }

        config.Save(next);
        Assert.Equal(days, new FleetConfigStore(configuration).Current.History?.DeleteAfterDays);
    }
}
