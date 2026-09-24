using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

/// <summary>A fleet context store, journal and request scope on a scratch folder.</summary>
public abstract class ContextTestBase : IDisposable
{
    protected ContextTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "fleet-context-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Store = OpenStore();
        RequestContext = new FleetRequestContext();
        Journal = new FleetContextJournal(Store, RequestContext, NullLogger.Instance);
    }

    protected string Root { get; }

    internal FleetContextStore Store { get; private set; }

    internal FleetRequestContext RequestContext { get; }

    internal FleetContextJournal Journal { get; private set; }

    protected IConfiguration Configuration =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FLEET_CONTEXTS_DIR"] = Path.Combine(Root, "contexts"),
                ["FLEET_SESSIONS_DIR"] = Path.Combine(Root, "sessions"),
                ["FLEET_PLANS_DIR"] = Path.Combine(Root, "plans")
            })
            .Build();

    internal FleetContextStore OpenStore() => new(Configuration);

    /// <summary>Simulates the backend being stopped and started again: a brand new store on the same files.</summary>
    internal void Restart()
    {
        SqliteConnection.ClearAllPools();
        Store = OpenStore();
        Journal = new FleetContextJournal(Store, RequestContext, NullLogger.Instance);
    }

    internal static string NewContextId() => Guid.NewGuid().ToString("D");

    internal IDisposable Scope(string contextId, string? taskId = null) =>
        RequestContext.Push(new FleetRequestIdentity(contextId, "run-1", "test", taskId));

    internal static JsonElement Messages(params (string Id, string Role, string Text)[] messages) =>
        JsonSerializer.SerializeToElement(messages.Select(m => new { id = m.Id, role = m.Role, content = m.Text }).ToArray());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle on the scratch database is not worth failing a test over.
        }
    }
}

public sealed class FleetContextStoreTests : ContextTestBase
{
    [Fact]
    public void The_same_transcript_saved_twice_is_journaled_once()
    {
        string id = NewContextId();
        JsonElement messages = Messages(("m1", "user", "add a cache"), ("m2", "assistant", "done"));

        Store.UpsertMessages(id, "Cache", messages);
        Store.UpsertMessages(id, "Cache", messages);

        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(id).Where(e => e.Kind == FleetContextEventKind.Message).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(2, Store.GetContext(id)!.MessageCount);
    }

    [Fact]
    public void A_huge_attachment_is_shortened_in_the_event_but_kept_whole_in_the_resumable_transcript()
    {
        string id = NewContextId();
        string image = "[[FLEET_IMAGE:image/png:" + new string('A', 300_000) + "]]";

        Store.UpsertMessages(id, "Screenshot", Messages(("m1", "user", "look at this " + image)));

        FleetContextEvent recorded = Assert.Single(Store.AllEvents(id), e => e.Kind == FleetContextEventKind.Message);
        Assert.True(recorded.Payload.GetRawText().Length < 25_000);
        Assert.Contains("left out of the record", recorded.Payload.GetRawText());
        Assert.Contains(image, Store.GetSessionRecord(id)!.Messages.GetRawText());
    }

    [Fact]
    public void Only_the_newest_version_of_a_file_counts_as_the_files_state_and_older_versions_are_history_not_drift()
    {
        string id = NewContextId();
        string file = Path.Combine(Root, "notes.txt");
        File.WriteAllText(file, "first draft");
        Store.RecordArtifact(id, "t1", file);
        File.WriteAllText(file, "second draft, longer");
        Store.RecordArtifact(id, "t2", file);

        Assert.Equal(2, Store.ListArtifacts(id).Count);
        FleetArtifact latest = Assert.Single(Store.LatestArtifacts(id));
        Assert.Equal(2, latest.Version);
        Assert.True(latest.MatchesRecordedHash);
        Assert.Empty(Store.RecordArtifactDivergences(id));
        Assert.Single(Store.VerifyArtifacts(id));
    }

    [Fact]
    public void A_file_rewritten_with_the_same_length_a_moment_later_is_still_noticed()
    {
        string id = NewContextId();
        string file = Path.Combine(Root, "same-length.txt");
        File.WriteAllText(file, "aaaa");
        Store.RecordArtifact(id, "t1", file);
        Store.LatestArtifacts(id);

        File.WriteAllText(file, "bbbb");

        FleetArtifact latest = Assert.Single(Store.LatestArtifacts(id));
        Assert.False(latest.MatchesRecordedHash);
        Assert.Single(Store.RecordArtifactDivergences(id));
    }

    [Fact]
    public void A_large_file_is_hashed_from_a_stream_and_a_locked_file_is_not_called_changed()
    {
        string id = NewContextId();
        string big = Path.Combine(Root, "big.bin");
        using (var stream = File.Create(big))
        {
            stream.SetLength(40L * 1024 * 1024);
        }

        FleetArtifact recorded = Store.RecordArtifact(id, "t1", big);

        Assert.Equal(40L * 1024 * 1024, recorded.Length);
        Assert.Equal(64, recorded.Sha256!.Length);

        // Another program holds the file open with no sharing: it cannot be read, but that is not a change.
        using var held = new FileStream(big, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(Assert.Single(Store.LatestArtifacts(id)).MatchesRecordedHash);
    }

    [Fact]
    public void An_edited_message_becomes_an_update_event_and_nothing_is_lost()
    {
        string id = NewContextId();
        Store.UpsertMessages(id, "t", Messages(("m1", "user", "first wording")));

        Store.UpsertMessages(id, "t", Messages(("m1", "user", "second wording")));

        IReadOnlyList<FleetContextEvent> kinds = Store.AllEvents(id);
        Assert.Contains(kinds, e => e.Kind == FleetContextEventKind.Message);
        Assert.Contains(kinds, e => e.Kind == FleetContextEventKind.MessageUpdated);
    }

    [Fact]
    public void An_id_that_is_not_a_hyphenated_guid_is_refused()
    {
        Assert.False(FleetRequestContext.IsValidContextId(Guid.NewGuid().ToString("N")));
        Assert.False(FleetRequestContext.IsValidContextId("not-a-guid"));
        Assert.True(FleetRequestContext.IsValidContextId(NewContextId()));
        Assert.Throws<ArgumentException>(() => Store.EnsureContext(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Everything_survives_the_backend_restarting()
    {
        string id = NewContextId();
        Store.UpsertMessages(id, "Long job", Messages(("m1", "user", "migrate the database")));
        using (Scope(id))
        {
            Journal.RecordDecision("Use PostgreSQL, not SQLite", "the user needs concurrent writers");
        }

        Store.SaveHandoff(new HandoffEnvelope(1, id, "task-2", "step 1", "step 2", DateTimeOffset.UtcNow, "goal", "current", [], [], [], [], [], [], [], "do it", "done", []));
        Store.SaveCheckpoint(id, null, "test", new { note = "before restart" });

        Restart();

        Assert.Equal("Long job", Store.GetContext(id)!.Title);
        Assert.Contains(Store.EventsOfKinds(id, [FleetContextEventKind.Decision], 10, pinnedOnly: true), e => ContextText.String(e.Payload, "text")!.Contains("PostgreSQL"));
        Assert.NotNull(Store.LatestHandoff(id, "task-2"));
        Assert.Equal("before restart", ContextText.String(Store.LatestCheckpoint(id)!.State, "note"));
    }

    [Fact]
    public void An_old_json_session_is_imported_once_and_never_overwrites_a_chat_continued_since()
    {
        string sessions = Path.Combine(Root, "sessions");
        Directory.CreateDirectory(sessions);
        string id = NewContextId();
        File.WriteAllText(Path.Combine(sessions, id + ".json"),
            JsonSerializer.Serialize(new { id, title = "Old chat", createdUtc = DateTimeOffset.UtcNow, updatedUtc = DateTimeOffset.UtcNow, messages = new[] { new { id = "a", role = "user", content = "hello" } } }));

        Restart();
        Assert.Equal("Old chat", Store.GetContext(id)!.Title);
        Store.UpsertMessages(id, "Old chat", Messages(("a", "user", "hello"), ("b", "assistant", "hi"), ("c", "user", "and more")));

        Restart();

        Assert.Equal(3, Store.GetContext(id)!.MessageCount);
    }

    [Fact]
    public void Sessions_list_only_chats_that_have_messages()
    {
        string chat = NewContextId();
        string plan = NewContextId();
        Store.UpsertMessages(chat, "A chat", Messages(("m", "user", "hi")));
        Store.EnsureContext(plan, "Plan: something");

        Assert.Equal([chat], Store.ListSessions().Select(s => s.Id));
        Assert.Equal(2, Store.ListContexts().Count);
    }

    [Fact]
    public void Tool_calls_and_results_are_recorded_with_what_was_asked_and_what_came_back()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            Journal.RecordToolExecution("call-1", "read_file", "{\"path\":\"C:\\\\p\\\\a.cs\"}", "class A {}", isError: false);
        }

        IReadOnlyList<FleetContextEvent> events = Store.AllEvents(id);
        FleetContextEvent call = Assert.Single(events, e => e.Kind == FleetContextEventKind.ToolCall);
        FleetContextEvent result = Assert.Single(events, e => e.Kind == FleetContextEventKind.ToolResult);
        Assert.Equal("read_file", ContextText.String(call.Payload, "name"));
        Assert.Contains("a.cs", ContextText.String(call.Payload, "arguments"));
        Assert.Equal("call-1", ContextText.String(result.Payload, "callId"));
        Assert.Equal(64, ContextText.String(result.Payload, "sha256")!.Length);
        Assert.False(ContextText.Bool(result.Payload, "isError"));
    }

    [Fact]
    public void Nothing_is_written_when_no_context_is_in_scope()
    {
        Journal.RecordToolExecution("c", "read_file", "{}", "x", false);
        Journal.RecordRoute("worker1", "standard", "Off", "routed");
        Assert.Null(Journal.RecordDecision("nobody will hear this", null));
        Assert.Null(Journal.BuildInjection("worker1"));
        Assert.Empty(Store.ListContexts());
    }
}

public sealed class FleetContextStoreRecoveryTests : ContextTestBase
{
    [Fact]
    public void A_damaged_database_is_set_aside_and_the_backend_still_starts_with_a_fresh_one()
    {
        string id = NewContextId();
        Store.UpsertMessages(id, "Before the damage", Messages(("m", "user", "hello")));
        SqliteConnection.ClearAllPools();
        string database = Store.DatabasePath;
        File.WriteAllText(database, "this is not a database, something overwrote it");
        File.Delete(database + "-wal");
        File.Delete(database + "-shm");

        FleetContextStore reopened = OpenStore();

        Assert.NotNull(reopened.RecoveredFrom);
        Assert.True(File.Exists(reopened.RecoveredFrom));
        Assert.Contains("something overwrote it", File.ReadAllText(reopened.RecoveredFrom!));
        Assert.Empty(reopened.ListContexts());
        reopened.UpsertMessages(id, "After", Messages(("m", "user", "still works")));
        Assert.Equal("After", reopened.GetContext(id)!.Title);
    }

    [Fact]
    public void A_healthy_database_is_never_moved()
    {
        Store.EnsureContext(NewContextId(), "fine");
        Restart();

        Assert.Null(Store.RecoveredFrom);
        Assert.Single(Store.ListContexts());
    }
}

public sealed class FleetContextStoreConcurrencyTests : ContextTestBase
{
    [Fact]
    public async Task Writers_and_readers_at_the_same_time_never_fail_or_lose_an_event()
    {
        string id = NewContextId();
        Store.EnsureContext(id, "busy");
        const int writers = 6;
        const int eventsEach = 150;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Store.ListContexts();
                    Store.RecentEvents(id, 50);
                    ContextAssembler.Assemble(Store, new ContextAssemblyRequest(id));
                    Store.ListSessions();
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }
            }
        })).ToArray();

        Task[] writing = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < eventsEach; i++)
            {
                try
                {
                    Store.AppendEvent(id, FleetContextEventKind.ToolCall, new { name = "w" + w, i });
                    if (i % 25 == 0)
                    {
                        Store.UpsertMessages(id, "busy", Messages(($"m{w}-{i}", "user", $"message {w} {i}")));
                    }
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }
            }
        })).ToArray();

        await Task.WhenAll(writing);
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(errors);
        Assert.Equal(writers * eventsEach, Store.AllEvents(id).Count(e => e.Kind == FleetContextEventKind.ToolCall));
    }
}

public sealed class ContextContinuityTests : ContextTestBase
{
    private static string TextOf(IReadOnlyList<ChatMessage> messages) => string.Join("\n", messages.Select(m => m.Text));

    [Fact]
    public void A_pinned_decision_is_recalled_when_the_work_moves_heavy_then_light_then_heavy()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            Journal.RecordDecision("The public API stays backwards compatible", "other teams depend on it", "constraint");
            Journal.RecordDecision("Use the token bucket algorithm for rate limiting", null);
        }

        foreach (string node in new[] { "hub", "worker1", "hub" })
        {
            using (Scope(id))
            {
                string? injection = Journal.BuildInjection(node);
                Assert.NotNull(injection);
                Assert.Contains("token bucket", injection);
                Assert.Contains("backwards compatible", injection);
                Assert.Contains("[constraint, noted by the assistant]", injection);
            }
        }
    }

    [Fact]
    public void What_each_agent_was_handed_is_recorded_so_it_can_be_inspected()
    {
        string id = NewContextId();
        long pinned;
        using (Scope(id))
        {
            pinned = Journal.RecordDecision("Use tabs, not spaces", null)!.Value;
            Journal.BuildInjection("hub");
            Journal.BuildInjection("worker1");
        }

        IReadOnlyList<FleetContextEvent> deliveries = Store.EventsOfKinds(id, [FleetContextEventKind.ContextAssembled], 10);
        Assert.Equal(["hub", "worker1"], deliveries.Select(e => e.Node));
        Assert.All(deliveries, e => Assert.Contains("Use tabs", ContextText.String(e.Payload, "text")));
        Assert.All(deliveries, e => Assert.Contains(pinned, e.Payload.GetProperty("eventIds").EnumerateArray().Select(x => x.GetInt64())));
    }

    [Fact]
    public void The_same_block_sent_again_in_a_tool_loop_is_not_recorded_again()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            Journal.RecordDecision("One fact", null);
            Journal.BuildInjection("hub");
            Journal.BuildInjection("hub");
            Journal.BuildInjection("hub");
        }

        Assert.Single(Store.EventsOfKinds(id, [FleetContextEventKind.ContextAssembled], 10));
    }

    [Fact]
    public void A_pinned_decision_says_who_pinned_it_so_a_model_noted_one_is_not_mistaken_for_the_users()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            Journal.RecordDecision("Use tabs", null);
        }

        Store.AppendEvent(id, FleetContextEventKind.Decision, new { text = "Never touch the billing module", category = "constraint" }, actor: "user", pinned: true);

        string text = ContextAssembler.Assemble(Store, new ContextAssemblyRequest(id)).Text;

        Assert.Contains("[decision, noted by the assistant] Use tabs", text);
        Assert.Contains("[constraint, from the user] Never touch the billing module", text);
    }

    [Fact]
    public void An_unpinned_decision_stops_being_sent_but_stays_in_the_record()
    {
        string id = NewContextId();
        long? pinned;
        using (Scope(id))
        {
            pinned = Journal.RecordDecision("Temporary rule", null);
        }

        Assert.True(Store.PinEvent(id, pinned!.Value, false));

        Assert.DoesNotContain("Temporary rule", ContextAssembler.Assemble(Store, new ContextAssemblyRequest(id)).Text);
        Assert.NotNull(Store.GetEvent(id, pinned.Value));
    }

    [Fact]
    public void A_context_with_nothing_worth_sending_adds_no_message_and_no_cost()
    {
        string id = NewContextId();
        Store.UpsertMessages(id, "chat", Messages(("m", "user", "hello")));
        using (Scope(id))
        {
            Assert.Null(Journal.BuildInjection("hub"));
        }
    }

    [Fact]
    public void The_context_goes_after_the_leading_system_messages_and_before_the_conversation()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "rules"),
            new(ChatRole.System, "more rules"),
            new(ChatRole.User, "hello")
        ];

        IReadOnlyList<ChatMessage> result = ContextInjection.Apply(messages, "DURABLE");

        Assert.Equal(["rules", "more rules", "DURABLE", "hello"], result.Select(m => m.Text));
        Assert.Same(messages, ContextInjection.Apply(messages, null));
    }

    [Fact]
    public void A_step_is_told_about_its_own_failed_checks_not_the_ones_earlier_steps_already_got_past()
    {
        string id = NewContextId();
        Store.AppendEvent(id, FleetContextEventKind.Verification, new { command = "check one", passed = false, output = "old failure of step one" }, "task-1");
        Store.AppendEvent(id, FleetContextEventKind.Verification, new { command = "check two", passed = false, output = "failure of step two" }, "task-2");

        string forTwo = ContextAssembler.Assemble(Store, new ContextAssemblyRequest(id, "task-2")).Text;
        string forChat = ContextAssembler.Assemble(Store, new ContextAssemblyRequest(id)).Text;

        Assert.Contains("failure of step two", forTwo);
        Assert.DoesNotContain("old failure of step one", forTwo);
        Assert.Contains("old failure of step one", forChat);
    }

    [Fact]
    public void Pinned_facts_are_never_cut_even_when_the_budget_is_tiny()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            Journal.RecordDecision("Never delete the audit table", null, "constraint");
        }

        for (int i = 0; i < 10; i++)
        {
            Store.AppendEvent(id, FleetContextEventKind.Verification, new { command = "dotnet test", passed = false, output = new string('x', 500) });
        }

        ContextAssemblyPreview preview = ContextAssembler.Assemble(Store, new ContextAssemblyRequest(id, MaxCharacters: 700));

        Assert.Contains("Never delete the audit table", preview.Text);
        Assert.True(preview.WasTruncated);
    }

    [Fact]
    public void Compaction_keeps_the_goal_the_pending_request_and_tool_provenance_and_deletes_nothing()
    {
        string id = NewContextId();
        var conversation = new List<(string, string, string)>();
        for (int i = 0; i < 12; i++)
        {
            conversation.Add(($"u{i}", "user", i == 0 ? "Build a billing export for finance" : $"request number {i}"));
            conversation.Add(($"a{i}", "assistant", $"answer {i}"));
        }

        using (Scope(id))
        {
            Journal.RecordDecision("Exports are CSV, UTF-8", null);
            for (int i = 0; i < 8; i++)
            {
                Journal.RecordToolExecution($"c{i}", "read_file", $"{{\"path\":\"src/file{i}.cs\"}}", "content " + i, false);
            }
        }

        Store.UpsertMessages(id, "Billing", Messages(conversation.ToArray()));
        int before = Store.AllEvents(id).Count;

        FleetContextSummaryRecord? summary = ContextCompactor.Compact(Store, id, keepRecentMessages: 4, minimumEvents: 5);

        Assert.NotNull(summary);
        Assert.Contains("Build a billing export for finance", summary.Text);
        Assert.Contains("read_file", summary.Text);
        Assert.Contains("file3.cs", summary.Text);
        Assert.True(Store.AllEvents(id).Count >= before, "compaction adds a record, it never removes one");
        Assert.NotEmpty(summary.SourceEventIds);

        ContextAssemblyPreview preview = ContextAssembler.Assemble(Store, new ContextAssemblyRequest(id));
        Assert.Contains("Exports are CSV", preview.Text);
        Assert.Contains("Earlier work, summarised", preview.Text);
    }

    [Fact]
    public void Searching_the_record_finds_earlier_tool_output_and_hides_the_fleets_own_context_blocks()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            Journal.RecordDecision("Retry three times", null);
            Journal.BuildInjection("hub");
            Journal.RecordToolExecution("c1", "run_command", "{\"command\":\"dotnet test\"}", "Failed: LoginTests.Rejects_the_sixth_attempt", true);
            var tools = new ContextTools(Journal);

            string found = tools.SearchContext("sixth_attempt");
            string none = tools.SearchContext("nothing like this");

            Assert.Contains("run_command", found);
            Assert.DoesNotContain("context-assembled", found);
            Assert.Contains("Nothing in this conversation's record", none);
        }
    }

    [Fact]
    public void The_same_decision_pinned_again_is_not_added_twice_even_when_worded_with_different_spacing_and_punctuation()
    {
        string id = NewContextId();
        using (Scope(id))
        {
            long? first = Journal.RecordDecision("Use the token bucket algorithm.", null);
            long? again = Journal.RecordDecision("  use the TOKEN bucket   algorithm ", "a different reason");
            long? other = Journal.RecordDecision("Refill once a second", null);

            Assert.Equal(first, again);
            Assert.NotEqual(first, other);
        }

        Assert.Equal(2, Store.EventsOfKinds(id, [FleetContextEventKind.Decision], 10, pinnedOnly: true).Count);
    }

    [Fact]
    public void The_decision_tool_pins_and_says_so_and_refuses_without_a_context()
    {
        var tools = new ContextTools(Journal);
        Assert.Contains("nothing was saved", tools.RecordDecision("x"));

        string id = NewContextId();
        using (Scope(id))
        {
            string reply = tools.RecordDecision("Store money as integer cents", "avoids rounding", "constraint");
            Assert.Contains("Pinned as #", reply);
            Assert.Contains("(constraint)", reply);
            Assert.StartsWith("Error", tools.RecordDecision("  "));
        }
    }
}

public sealed class ContextMiddlewareTests : ContextTestBase
{
    private async Task<(HttpResponseMessage Response, FleetRequestIdentity? Seen, string Body)> Post(string json)
    {
        FleetRequestIdentity? seen = null;
        string body = string.Empty;
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    FleetContextMiddleware.Use(app, Store, RequestContext, NullLogger.Instance);
                    app.Run(async context =>
                    {
                        seen = RequestContext.Current;
                        body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                        await context.Response.WriteAsync("ok");
                    });
                }))
            .StartAsync();

        HttpResponseMessage response = await host.GetTestClient().PostAsync("/", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        return (response, seen, body);
    }

    [Fact]
    public async Task A_run_joins_its_context_is_journaled_and_the_framework_still_gets_the_body()
    {
        string id = NewContextId();
        string json = JsonSerializer.Serialize(new
        {
            threadId = id,
            runId = "r1",
            forwardedProps = new { fleetSurface = "vscode" },
            messages = new[] { new { id = "m1", role = "user", content = "Refactor the invoice service to be async" } }
        });

        (HttpResponseMessage response, FleetRequestIdentity? seen, string body) = await Post(json);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, seen?.ContextId);
        Assert.Equal("vscode", seen?.Surface);
        Assert.Equal(json, body);
        FleetContextSummary context = Store.GetContext(id)!;
        Assert.StartsWith("Refactor the invoice service", context.Title);
        Assert.Equal(1, context.MessageCount);
        Assert.Contains(Store.AllEvents(id), e => e.Kind == FleetContextEventKind.RunInput);
    }

    [Fact]
    public async Task A_second_run_of_the_same_chat_does_not_duplicate_the_messages_or_rename_it()
    {
        string id = NewContextId();
        string first = JsonSerializer.Serialize(new { threadId = id, runId = "r1", messages = new[] { new { id = "m1", role = "user", content = "Original question" } } });
        string second = JsonSerializer.Serialize(new
        {
            threadId = id,
            runId = "r2",
            messages = new object[]
            {
                new { id = "m1", role = "user", content = "Original question" },
                new { id = "m2", role = "assistant", content = "An answer" },
                new { id = "m3", role = "user", content = "A follow-up" }
            }
        });

        await Post(first);
        await Post(second);

        Assert.Equal(3, Store.AllEvents(id).Count(e => e.Kind == FleetContextEventKind.Message));
        Assert.StartsWith("Original question", Store.GetContext(id)!.Title);
    }

    [Fact]
    public async Task A_surface_that_joins_a_chat_is_journaled_without_taking_over_its_transcript()
    {
        // A web chat, then VS Code's model picker linked to it: the picker sends its own transcript.
        string id = NewContextId();
        Store.UpsertMessages(id, "Rate limiter design", Messages(("w1", "user", "Design a rate limiter"), ("w2", "assistant", "Use a token bucket")), "ag-ui");
        string picker = JsonSerializer.Serialize(new
        {
            threadId = id,
            runId = "p1",
            forwardedProps = new { fleetSurface = "vscode-model", fleetTranscript = "journal" },
            messages = new[] { new { id = "vp-aaa", role = "user", content = "Now add tests for it" } }
        });

        (_, FleetRequestIdentity? seen, _) = await Post(picker);
        await Post(picker);

        Assert.Equal(id, seen?.ContextId);
        Assert.True(seen?.Joined);
        SessionRecord transcript = Store.GetSessionRecord(id)!;
        Assert.Equal(2, transcript.Messages.GetArrayLength());
        Assert.Equal("Rate limiter design", Store.GetContext(id)!.Title);
        FleetContextEvent joined = Assert.Single(Store.AllEvents(id), e => e.Kind == FleetContextEventKind.Message && e.Payload.GetRawText().Contains("Now add tests"));
        Assert.Contains("vscode-model", joined.Payload.GetRawText());
    }

    [Fact]
    public void A_joined_surface_is_handed_the_latest_turns_and_an_ordinary_client_is_not()
    {
        string id = NewContextId();
        Store.UpsertMessages(id, "t", Messages(
            ("w1", "user", "Design a rate limiter for the login endpoint"),
            ("w2", "assistant", "Use a token bucket, capacity 5, refill 1 per second"),
            ("w3", "system", "internal instructions")), "ag-ui");

        string? ordinary;
        using (RequestContext.Push(new FleetRequestIdentity(id, "r1", "ag-ui")))
        {
            ordinary = Journal.BuildInjection("hub");
        }

        string? joined;
        using (RequestContext.Push(new FleetRequestIdentity(id, "r2", "vscode-model", Joined: true)))
        {
            joined = Journal.BuildInjection("hub");
        }

        Assert.Null(ordinary);
        Assert.NotNull(joined);
        Assert.Contains("assistant: Use a token bucket, capacity 5", joined);
        Assert.Contains("user: Design a rate limiter", joined);
        Assert.DoesNotContain("internal instructions", joined);
    }

    [Theory]
    [InlineData("\"journal\"", true)]
    [InlineData("\"replace\"", false)]
    [InlineData("true", false)]
    public async Task Only_the_exact_journal_flag_keeps_the_transcript(string flag, bool journalOnly)
    {
        string id = NewContextId();
        Store.UpsertMessages(id, "t", Messages(("w1", "user", "first")), "ag-ui");
        string json = $$"""{"threadId":"{{id}}","runId":"r","forwardedProps":{"fleetTranscript":{{flag}}},"messages":[{"id":"x1","role":"user","content":"other"}]}""";

        await Post(json);

        string visible = Store.GetSessionRecord(id)!.Messages.GetRawText();
        Assert.Equal(journalOnly, visible.Contains("first") && !visible.Contains("other"));
    }

    [Fact]
    public async Task A_request_without_a_guid_thread_id_passes_through_with_no_context()
    {
        string json = JsonSerializer.Serialize(new { threadId = "t-123", runId = "r", messages = new[] { new { id = "m", role = "user", content = "hi" } } });

        (HttpResponseMessage response, FleetRequestIdentity? seen, string body) = await Post(json);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Null(seen);
        Assert.Equal(json, body);
        Assert.Empty(Store.ListContexts());
    }

    [Fact]
    public async Task Garbage_bodies_never_break_the_request()
    {
        (HttpResponseMessage response, FleetRequestIdentity? seen, _) = await Post("{ this is not json");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Null(seen);
    }
}
