using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Data.Sqlite;

namespace AgentFleet;

/// <summary>
/// The durable, application-owned context journal. SQLite is the authority for
/// conversations, observable events, handoffs, artifacts, checkpoints, and the
/// framework's provider-specific AgentSession state.
/// </summary>
internal sealed class FleetContextStore : AgentSessionStore
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly string _legacySessionsDirectory;
    private readonly object _writeLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public FleetContextStore(IConfiguration configuration)
    {
        string directory = configuration["FLEET_CONTEXTS_DIR"]
            ?? Path.Combine(AppContext.BaseDirectory, "contexts");
        Directory.CreateDirectory(directory);
        _databasePath = Path.Combine(directory, "fleet-context.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
        _legacySessionsDirectory = configuration["FLEET_SESSIONS_DIR"]
            ?? Path.Combine(AppContext.BaseDirectory, "sessions");

        try
        {
            Initialize();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26 && File.Exists(_databasePath))
        {
            // SQLITE_CORRUPT or SQLITE_NOTADB: the file is damaged (a power cut, a bad disk, something else
            // overwrote it). Keeping the backend up matters more than refusing to start, and the damaged file
            // is kept next to the new one so it can still be inspected or repaired by hand.
            RecoveredFrom = MoveDamagedDatabaseAside();
            Initialize();
        }

        MigrateLegacySessions();
    }

    internal string DatabasePath => _databasePath;

    /// <summary>Where a damaged database was moved to at startup, or null if it was healthy.</summary>
    internal string? RecoveredFrom { get; }

    private string MoveDamagedDatabaseAside()
    {
        SqliteConnection.ClearAllPools();
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string moved = $"{_databasePath}.damaged-{stamp}";
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string from = _databasePath + suffix;
            if (File.Exists(from))
            {
                File.Move(from, moved + suffix, overwrite: true);
            }
        }

        return moved;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        // secure_delete: the record holds file contents the assistant read, so a deleted chat is
        // overwritten in the file instead of lingering in free pages.
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;

                CREATE TABLE IF NOT EXISTS metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS contexts (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'active',
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    messages_json TEXT NOT NULL DEFAULT '[]',
                    message_count INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS tasks (
                    id TEXT PRIMARY KEY,
                    context_id TEXT NOT NULL REFERENCES contexts(id) ON DELETE CASCADE,
                    kind TEXT NOT NULL,
                    parent_task_id TEXT NULL,
                    status TEXT NOT NULL,
                    title TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    context_id TEXT NOT NULL REFERENCES contexts(id) ON DELETE CASCADE,
                    task_id TEXT NULL,
                    kind TEXT NOT NULL,
                    actor TEXT NULL,
                    agent TEXT NULL,
                    node TEXT NULL,
                    at_utc TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_events_context_id ON events(context_id, id);
                CREATE INDEX IF NOT EXISTS ix_events_task_id ON events(task_id, id);

                CREATE TABLE IF NOT EXISTS message_index (
                    context_id TEXT NOT NULL REFERENCES contexts(id) ON DELETE CASCADE,
                    external_id TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    event_id INTEGER NOT NULL REFERENCES events(id) ON DELETE CASCADE,
                    PRIMARY KEY(context_id, external_id)
                );

                CREATE TABLE IF NOT EXISTS artifacts (
                    id TEXT PRIMARY KEY,
                    context_id TEXT NOT NULL REFERENCES contexts(id) ON DELETE CASCADE,
                    task_id TEXT NULL,
                    producing_event_id INTEGER NULL,
                    path TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    sha256 TEXT NULL,
                    length INTEGER NULL,
                    version INTEGER NOT NULL,
                    created_utc TEXT NOT NULL,
                    metadata_json TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_artifacts_context_id ON artifacts(context_id, created_utc);

                CREATE TABLE IF NOT EXISTS handoffs (
                    id TEXT PRIMARY KEY,
                    context_id TEXT NOT NULL REFERENCES contexts(id) ON DELETE CASCADE,
                    task_id TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    envelope_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_handoffs_context_task ON handoffs(context_id, task_id, created_utc);

                CREATE TABLE IF NOT EXISTS checkpoints (
                    id TEXT PRIMARY KEY,
                    context_id TEXT NOT NULL REFERENCES contexts(id) ON DELETE CASCADE,
                    task_id TEXT NULL,
                    sequence INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    state_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_checkpoints_context ON checkpoints(context_id, sequence DESC);

                CREATE TABLE IF NOT EXISTS summaries (
                    id TEXT PRIMARY KEY,
                    context_id TEXT NOT NULL REFERENCES contexts(id) ON DELETE CASCADE,
                    start_sequence INTEGER NOT NULL,
                    end_sequence INTEGER NOT NULL,
                    version INTEGER NOT NULL,
                    created_utc TEXT NOT NULL,
                    text TEXT NOT NULL,
                    source_event_ids_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_summaries_context ON summaries(context_id, end_sequence DESC);

                CREATE TABLE IF NOT EXISTS agent_sessions (
                    agent_name TEXT NOT NULL,
                    context_id TEXT NOT NULL,
                    state_json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY(agent_name, context_id)
                );

                INSERT INTO metadata(key, value) VALUES('schema_version', @schema)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            command.Parameters.AddWithValue("@schema", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    public FleetContextSummary EnsureContext(string contextId, string? title = null)
    {
        ValidateContextId(contextId);
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO contexts(id, title, status, created_utc, updated_utc, messages_json, message_count)
                    VALUES(@id, @title, 'active', @now, @now, '[]', 0)
                    ON CONFLICT(id) DO UPDATE SET
                        title=CASE WHEN @replaceTitle = 1 THEN excluded.title ELSE contexts.title END,
                        updated_utc=excluded.updated_utc;
                    """;
                command.Parameters.AddWithValue("@id", contextId);
                command.Parameters.AddWithValue("@title", CleanTitle(title));
                command.Parameters.AddWithValue("@replaceTitle", string.IsNullOrWhiteSpace(title) ? 0 : 1);
                command.Parameters.AddWithValue("@now", Format(now));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return GetContext(contextId)!;
        }
    }

    public IReadOnlyList<FleetContextSummary> ListContexts()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, c.status, c.created_utc, c.updated_utc, c.message_count,
                   (SELECT COUNT(*) FROM events e WHERE e.context_id = c.id),
                   (SELECT MAX(sequence) FROM checkpoints p WHERE p.context_id = c.id)
            FROM contexts c
            ORDER BY c.updated_utc DESC;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var contexts = new List<FleetContextSummary>();
        while (reader.Read())
        {
            contexts.Add(ReadContextSummary(reader));
        }

        return contexts;
    }

    public FleetContextSummary? GetContext(string contextId)
    {
        if (!FleetRequestContext.IsValidContextId(contextId))
        {
            return null;
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, c.status, c.created_utc, c.updated_utc, c.message_count,
                   (SELECT COUNT(*) FROM events e WHERE e.context_id = c.id),
                   (SELECT MAX(sequence) FROM checkpoints p WHERE p.context_id = c.id)
            FROM contexts c WHERE c.id = @id;
            """;
        command.Parameters.AddWithValue("@id", contextId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadContextSummary(reader) : null;
    }

    public SessionRecord? GetSessionRecord(string contextId)
    {
        if (!FleetRequestContext.IsValidContextId(contextId))
        {
            return null;
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, created_utc, updated_utc, messages_json FROM contexts WHERE id=@id;";
        command.Parameters.AddWithValue("@id", contextId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(reader.GetString(4));
        return new SessionRecord(
            reader.GetString(0),
            reader.GetString(1),
            ParseTime(reader.GetString(2)),
            ParseTime(reader.GetString(3)),
            document.RootElement.Clone());
    }

    public SessionSummary UpsertMessages(string contextId, string? title, JsonElement messages, string surface = "unknown")
    {
        ValidateContextId(contextId);
        if (messages.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Messages must be a JSON array.", nameof(messages));
        }

        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string resolvedTitle = CleanTitle(title);
            DateTimeOffset createdAt = now;

            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT created_utc, title FROM contexts WHERE id=@id;";
                read.Parameters.AddWithValue("@id", contextId);
                using SqliteDataReader reader = read.ExecuteReader();
                if (reader.Read())
                {
                    createdAt = ParseTime(reader.GetString(0));
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        resolvedTitle = reader.GetString(1);
                    }
                }
            }

            using (SqliteCommand upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO contexts(id, title, status, created_utc, updated_utc, messages_json, message_count)
                    VALUES(@id, @title, 'active', @created, @updated, @messages, @count)
                    ON CONFLICT(id) DO UPDATE SET
                        title=excluded.title,
                        status='active',
                        updated_utc=excluded.updated_utc,
                        messages_json=excluded.messages_json,
                        message_count=excluded.message_count;
                    """;
                upsert.Parameters.AddWithValue("@id", contextId);
                upsert.Parameters.AddWithValue("@title", resolvedTitle);
                upsert.Parameters.AddWithValue("@created", Format(createdAt));
                upsert.Parameters.AddWithValue("@updated", Format(now));
                upsert.Parameters.AddWithValue("@messages", messages.GetRawText());
                upsert.Parameters.AddWithValue("@count", messages.GetArrayLength());
                upsert.ExecuteNonQuery();
            }

            int index = 0;
            foreach (JsonElement message in messages.EnumerateArray())
            {
                SyncMessage(connection, transaction, contextId, message, index++, surface, now);
            }

            transaction.Commit();
            return new SessionSummary(contextId, resolvedTitle, createdAt, now, messages.GetArrayLength());
        }
    }

    /// <summary>
    /// Records a client's messages in the journal, each once (by id and content), without replacing the
    /// visible transcript. For a surface that joins a conversation with a transcript of its own that must
    /// not take the conversation over (VS Code's model picker sends its own prompt scaffolding).
    /// </summary>
    public void JournalMessages(string contextId, JsonElement messages, string surface = "unknown")
    {
        ValidateContextId(contextId);
        if (messages.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Messages must be a JSON array.", nameof(messages));
        }

        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            EnsureContextRow(connection, transaction, contextId);
            int index = 0;
            foreach (JsonElement message in messages.EnumerateArray())
            {
                SyncMessage(connection, transaction, contextId, message, index++, surface, now);
            }

            TouchContext(connection, transaction, contextId);
            transaction.Commit();
        }
    }

    private static void SyncMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contextId,
        JsonElement message,
        int index,
        string surface,
        DateTimeOffset now)
    {
        string raw = message.GetRawText();
        string hash = Sha256(Encoding.UTF8.GetBytes(raw));
        string role = message.ValueKind == JsonValueKind.Object &&
                      message.TryGetProperty("role", out JsonElement roleElement) &&
                      roleElement.ValueKind == JsonValueKind.String
            ? roleElement.GetString() ?? "unknown"
            : "unknown";
        string externalId = message.ValueKind == JsonValueKind.Object &&
                            message.TryGetProperty("id", out JsonElement idElement) &&
                            idElement.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(idElement.GetString())
            ? idElement.GetString()!
            : $"{role}:{index}:{hash}";

        string? oldHash = null;
        using (SqliteCommand existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT content_hash FROM message_index WHERE context_id=@context AND external_id=@external;";
            existing.Parameters.AddWithValue("@context", contextId);
            existing.Parameters.AddWithValue("@external", externalId);
            oldHash = existing.ExecuteScalar() as string;
        }

        if (oldHash == hash)
        {
            return;
        }

        string kind = oldHash is null
            ? role == "tool" ? FleetContextEventKind.ToolResult : FleetContextEventKind.Message
            : FleetContextEventKind.MessageUpdated;
        // The visible transcript (messages_json) keeps a message whole so a chat can be resumed exactly;
        // the event copy shortens very long strings (an attached image is megabytes of base64) so the
        // journal stays small and readable.
        string payload = JsonSerializer.Serialize(new
        {
            surface,
            externalId,
            role,
            message = ContextText.ShortenLongStrings(JsonDocument.Parse(raw).RootElement)
        }, JsonOptions);
        long eventId = InsertEvent(connection, transaction, contextId, null, kind, role, null, null, now, payload, pinned: role == "system");

        using SqliteCommand indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText = """
            INSERT INTO message_index(context_id, external_id, content_hash, event_id)
            VALUES(@context, @external, @hash, @event)
            ON CONFLICT(context_id, external_id) DO UPDATE SET
                content_hash=excluded.content_hash,
                event_id=excluded.event_id;
            """;
        indexCommand.Parameters.AddWithValue("@context", contextId);
        indexCommand.Parameters.AddWithValue("@external", externalId);
        indexCommand.Parameters.AddWithValue("@hash", hash);
        indexCommand.Parameters.AddWithValue("@event", eventId);
        indexCommand.ExecuteNonQuery();

        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("toolCalls", out JsonElement calls) &&
            calls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement call in calls.EnumerateArray())
            {
                InsertEvent(connection, transaction, contextId, null, FleetContextEventKind.ToolCall,
                    "assistant", null, null, now, call.GetRawText(), pinned: false);
            }
        }
    }

    public long AppendEvent(
        string contextId,
        string kind,
        object? payload,
        string? taskId = null,
        string? actor = null,
        string? agent = null,
        string? node = null,
        bool pinned = false)
    {
        ValidateContextId(contextId);
        string json = payload is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(payload, JsonOptions);
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            EnsureContextRow(connection, transaction, contextId);
            long id = InsertEvent(connection, transaction, contextId, taskId, kind, actor, agent, node,
                DateTimeOffset.UtcNow, json, pinned);
            TouchContext(connection, transaction, contextId);
            transaction.Commit();
            return id;
        }
    }

    public IReadOnlyList<FleetContextEvent> RecentEvents(string contextId, int limit = 80)
    {
        limit = Math.Clamp(limit, 1, 500);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, context_id, task_id, kind, actor, agent, node, at_utc, payload_json, pinned
            FROM events WHERE context_id=@context ORDER BY id DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@context", contextId);
        command.Parameters.AddWithValue("@limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        var events = new List<FleetContextEvent>();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        events.Reverse();
        return events;
    }

    public IReadOnlyList<FleetContextEvent> SearchEvents(string contextId, string query, int limit = 40)
    {
        limit = Math.Clamp(limit, 1, 200);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, context_id, task_id, kind, actor, agent, node, at_utc, payload_json, pinned
            FROM events
            WHERE context_id=@context AND payload_json LIKE @query ESCAPE '\'
            ORDER BY id DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@context", contextId);
        command.Parameters.AddWithValue("@query", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("@limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        var events = new List<FleetContextEvent>();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        events.Reverse();
        return events;
    }

    public void UpsertTask(string contextId, string taskId, string kind, string status, string title, string? parentTaskId = null)
    {
        ValidateContextId(contextId);
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("Task id is required.", nameof(taskId));
        }

        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            EnsureContextRow(connection, transaction, contextId);
            string now = Format(DateTimeOffset.UtcNow);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tasks(id, context_id, kind, parent_task_id, status, title, created_utc, updated_utc)
                VALUES(@id, @context, @kind, @parent, @status, @title, @now, @now)
                ON CONFLICT(id) DO UPDATE SET
                    context_id=excluded.context_id,
                    kind=excluded.kind,
                    parent_task_id=excluded.parent_task_id,
                    status=excluded.status,
                    title=excluded.title,
                    updated_utc=excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("@id", taskId);
            command.Parameters.AddWithValue("@context", contextId);
            command.Parameters.AddWithValue("@kind", kind);
            command.Parameters.AddWithValue("@parent", (object?)parentTaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@now", now);
            command.ExecuteNonQuery();
            TouchContext(connection, transaction, contextId);
            transaction.Commit();
        }
    }

    public FleetHandoffRecord SaveHandoff(HandoffEnvelope envelope)
    {
        ValidateContextId(envelope.ContextId);
        string id = Guid.NewGuid().ToString("N");
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            EnsureContextRow(connection, transaction, envelope.ContextId);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO handoffs(id, context_id, task_id, created_utc, envelope_json) VALUES(@id,@context,@task,@created,@json);";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@context", envelope.ContextId);
            command.Parameters.AddWithValue("@task", envelope.TaskId);
            command.Parameters.AddWithValue("@created", Format(envelope.CreatedAtUtc));
            command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(envelope, JsonOptions));
            command.ExecuteNonQuery();
            TouchContext(connection, transaction, envelope.ContextId);
            transaction.Commit();
        }

        return new FleetHandoffRecord(id, envelope.ContextId, envelope.TaskId, envelope.CreatedAtUtc, envelope);
    }

    public IReadOnlyList<FleetHandoffRecord> ListHandoffs(string contextId, int limit = 20)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, context_id, task_id, created_utc, envelope_json FROM handoffs WHERE context_id=@context ORDER BY created_utc DESC LIMIT @limit;";
        command.Parameters.AddWithValue("@context", contextId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));
        using SqliteDataReader reader = command.ExecuteReader();
        var records = new List<FleetHandoffRecord>();
        while (reader.Read())
        {
            HandoffEnvelope envelope = JsonSerializer.Deserialize<HandoffEnvelope>(reader.GetString(4), JsonOptions)!;
            records.Add(new FleetHandoffRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)), envelope));
        }

        return records;
    }

    public FleetArtifact RecordArtifact(string contextId, string? taskId, string path, string mediaType = "text/plain", object? metadata = null)
    {
        ValidateContextId(contextId);
        string fullPath = Path.GetFullPath(path);
        bool exists = File.Exists(fullPath);
        string? hash = exists ? HashFile(fullPath) : null;
        long? length = exists ? new FileInfo(fullPath).Length : null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = Guid.NewGuid().ToString("N");
        int version;
        long eventId;

        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            EnsureContextRow(connection, transaction, contextId);
            using (SqliteCommand versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM artifacts WHERE context_id=@context AND path=@path;";
                versionCommand.Parameters.AddWithValue("@context", contextId);
                versionCommand.Parameters.AddWithValue("@path", fullPath);
                version = Convert.ToInt32((long)versionCommand.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
            }

            string kind = exists ? FleetContextEventKind.ArtifactPublished : FleetContextEventKind.ArtifactDiverged;
            eventId = InsertEvent(connection, transaction, contextId, taskId, kind, "runner", "fleet", null, now,
                JsonSerializer.Serialize(new { artifactId = id, path = fullPath, sha256 = hash, length, exists, version }, JsonOptions),
                pinned: true);

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO artifacts(id, context_id, task_id, producing_event_id, path, media_type, sha256, length, version, created_utc, metadata_json)
                VALUES(@id,@context,@task,@event,@path,@media,@hash,@length,@version,@created,@metadata);
                """;
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@context", contextId);
            command.Parameters.AddWithValue("@task", (object?)taskId ?? DBNull.Value);
            command.Parameters.AddWithValue("@event", eventId);
            command.Parameters.AddWithValue("@path", fullPath);
            command.Parameters.AddWithValue("@media", mediaType);
            command.Parameters.AddWithValue("@hash", (object?)hash ?? DBNull.Value);
            command.Parameters.AddWithValue("@length", (object?)length ?? DBNull.Value);
            command.Parameters.AddWithValue("@version", version);
            command.Parameters.AddWithValue("@created", Format(now));
            command.Parameters.AddWithValue("@metadata", metadata is null ? DBNull.Value : JsonSerializer.Serialize(metadata, JsonOptions));
            command.ExecuteNonQuery();
            TouchContext(connection, transaction, contextId);
            transaction.Commit();
        }

        JsonElement? metadataElement = metadata is null ? null : JsonSerializer.SerializeToElement(metadata, JsonOptions);
        return new FleetArtifact(id, contextId, taskId, eventId, fullPath, mediaType, hash, length, version, now,
            exists, MatchesRecordedHash: true, metadataElement);
    }

    public IReadOnlyList<FleetArtifact> ListArtifacts(string contextId, int limit = 100)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, context_id, task_id, producing_event_id, path, media_type, sha256, length, version, created_utc, metadata_json
            FROM artifacts WHERE context_id=@context ORDER BY created_utc DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@context", contextId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        using SqliteDataReader reader = command.ExecuteReader();
        var artifacts = new List<FleetArtifact>();
        while (reader.Read())
        {
            string path = reader.GetString(4);
            string? recordedHash = reader.IsDBNull(6) ? null : reader.GetString(6);
            bool exists = File.Exists(path);
            string? currentHash = exists ? HashFile(path) : null;
            JsonElement? metadata = null;
            if (!reader.IsDBNull(10))
            {
                using JsonDocument document = JsonDocument.Parse(reader.GetString(10));
                metadata = document.RootElement.Clone();
            }

            artifacts.Add(new FleetArtifact(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), path, reader.GetString(5), recordedHash,
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.GetInt32(8), ParseTime(reader.GetString(9)),
                exists, Matches(exists, recordedHash, currentHash), metadata));
        }

        return artifacts;
    }

    /// <summary>The newest recorded version of each file, checked against the disk. Older versions are history, not drift.</summary>
    public IReadOnlyList<FleetArtifact> LatestArtifacts(string contextId, int limit = 500)
    {
        StringComparer paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return ListArtifacts(contextId, limit)
            .GroupBy(artifact => artifact.Path, paths)
            .Select(group => group.OrderByDescending(artifact => artifact.Version).First())
            .OrderBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<ArtifactVerification> VerifyArtifacts(string contextId, string? taskId = null)
    {
        return LatestArtifacts(contextId)
            .Where(artifact => taskId is null || artifact.TaskId == taskId)
            .Select(artifact =>
            {
                string? currentHash = artifact.Exists ? HashFile(artifact.Path) : null;
                return new ArtifactVerification(artifact.Id, artifact.Path, artifact.Exists,
                    Matches(artifact.Exists, artifact.Sha256, currentHash),
                    artifact.Sha256, currentHash);
            })
            .ToList();
    }

    /// <summary>
    /// Compares the newest recorded version of every file with what is on disk now. A file that changed or
    /// vanished is reported (all of them, every time) and noted in the journal once per distinct state, so
    /// the next agent is told the record no longer matches instead of assuming it does.
    /// </summary>
    public IReadOnlyList<ArtifactVerification> RecordArtifactDivergences(string contextId, string? taskId = null)
    {
        IEnumerable<FleetArtifact> latest = LatestArtifacts(contextId);

        var diverged = new List<ArtifactVerification>();
        IReadOnlyList<FleetContextEvent> known = EventsOfKinds(contextId, [FleetContextEventKind.ArtifactDiverged], 500);
        foreach (FleetArtifact artifact in latest)
        {
            if (artifact.Exists && artifact.MatchesRecordedHash)
            {
                continue;
            }

            string? currentHash = artifact.Exists ? HashFile(artifact.Path) : null;
            diverged.Add(new ArtifactVerification(artifact.Id, artifact.Path, artifact.Exists, false, artifact.Sha256, currentHash));

            bool alreadyNoted = known.Any(e =>
                ContextText.String(e.Payload, "artifactId") == artifact.Id &&
                ContextText.String(e.Payload, "currentSha256") == currentHash);
            if (!alreadyNoted)
            {
                AppendEvent(
                    contextId,
                    FleetContextEventKind.ArtifactDiverged,
                    new
                    {
                        artifactId = artifact.Id,
                        path = artifact.Path,
                        recordedSha256 = artifact.Sha256,
                        currentSha256 = currentHash,
                        exists = artifact.Exists,
                        sha256 = currentHash
                    },
                    taskId, actor: "runner", agent: "fleet", pinned: true);
            }
        }

        return diverged;
    }

    public FleetCheckpoint SaveCheckpoint(string contextId, string? taskId, string kind, object state)
    {
        ValidateContextId(contextId);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string json = state is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(state, JsonOptions);
        long sequence;
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            EnsureContextRow(connection, transaction, contextId);
            using (SqliteCommand sequenceCommand = connection.CreateCommand())
            {
                sequenceCommand.Transaction = transaction;
                sequenceCommand.CommandText = "SELECT COALESCE(MAX(sequence), 0) + 1 FROM checkpoints WHERE context_id=@context;";
                sequenceCommand.Parameters.AddWithValue("@context", contextId);
                sequence = (long)sequenceCommand.ExecuteScalar()!;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO checkpoints(id, context_id, task_id, sequence, kind, created_utc, state_json) VALUES(@id,@context,@task,@sequence,@kind,@created,@state);";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@context", contextId);
            command.Parameters.AddWithValue("@task", (object?)taskId ?? DBNull.Value);
            command.Parameters.AddWithValue("@sequence", sequence);
            command.Parameters.AddWithValue("@kind", kind);
            command.Parameters.AddWithValue("@created", Format(now));
            command.Parameters.AddWithValue("@state", json);
            command.ExecuteNonQuery();
            InsertEvent(connection, transaction, contextId, taskId, FleetContextEventKind.Checkpoint, "system", "fleet", null, now,
                JsonSerializer.Serialize(new { checkpointId = id, sequence, kind }, JsonOptions), pinned: false);
            TouchContext(connection, transaction, contextId);
            transaction.Commit();
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return new FleetCheckpoint(id, contextId, taskId, sequence, kind, now, document.RootElement.Clone());
    }

    public FleetCheckpoint? LatestCheckpoint(string contextId)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, context_id, task_id, sequence, kind, created_utc, state_json FROM checkpoints WHERE context_id=@context ORDER BY sequence DESC LIMIT 1;";
        command.Parameters.AddWithValue("@context", contextId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(reader.GetString(6));
        return new FleetCheckpoint(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3), reader.GetString(4), ParseTime(reader.GetString(5)), document.RootElement.Clone());
    }

    public FleetContextSummaryRecord SaveSummary(string contextId, long startSequence, long endSequence, string text, IReadOnlyList<long> sourceEventIds)
    {
        ValidateContextId(contextId);
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int version;
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            EnsureContextRow(connection, transaction, contextId);
            using (SqliteCommand versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM summaries WHERE context_id=@context;";
                versionCommand.Parameters.AddWithValue("@context", contextId);
                version = Convert.ToInt32((long)versionCommand.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
            }

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO summaries(id, context_id, start_sequence, end_sequence, version, created_utc, text, source_event_ids_json) VALUES(@id,@context,@start,@end,@version,@created,@text,@sources);";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@context", contextId);
            command.Parameters.AddWithValue("@start", startSequence);
            command.Parameters.AddWithValue("@end", endSequence);
            command.Parameters.AddWithValue("@version", version);
            command.Parameters.AddWithValue("@created", Format(now));
            command.Parameters.AddWithValue("@text", text);
            command.Parameters.AddWithValue("@sources", JsonSerializer.Serialize(sourceEventIds, JsonOptions));
            command.ExecuteNonQuery();
            InsertEvent(connection, transaction, contextId, null, FleetContextEventKind.Compaction, "system", "fleet", null, now,
                JsonSerializer.Serialize(new { summaryId = id, startSequence, endSequence, version, sourceEventIds }, JsonOptions), pinned: false);
            transaction.Commit();
        }

        return new FleetContextSummaryRecord(id, contextId, startSequence, endSequence, version, now, text, sourceEventIds);
    }

    public FleetContextSummaryRecord? LatestSummary(string contextId)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, context_id, start_sequence, end_sequence, version, created_utc, text, source_event_ids_json FROM summaries WHERE context_id=@context ORDER BY version DESC LIMIT 1;";
        command.Parameters.AddWithValue("@context", contextId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        IReadOnlyList<long> ids = JsonSerializer.Deserialize<List<long>>(reader.GetString(7), JsonOptions) ?? [];
        return new FleetContextSummaryRecord(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt32(4), ParseTime(reader.GetString(5)), reader.GetString(6), ids);
    }

    public bool DeleteContext(string contextId)
    {
        if (!FleetRequestContext.IsValidContextId(contextId))
        {
            return false;
        }

        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM contexts WHERE id=@id; DELETE FROM agent_sessions WHERE context_id=@id;";
            command.Parameters.AddWithValue("@id", contextId);
            bool deleted = command.ExecuteNonQuery() > 0;
            FlushDeletes(connection);
            return deleted;
        }
    }

    // secure_delete zeroes what was deleted, but the pages as they were before still sit in the write-ahead
    // log until it is checkpointed. Checkpointing and truncating it makes a delete a real one. Best effort:
    // a reader in the middle of a long query can hold it back, and the next delete tries again.
    private static void FlushDeletes(SqliteConnection connection)
    {
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    public FleetContextStorage Storage()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM contexts),
                   (SELECT COUNT(*) FROM contexts WHERE message_count > 0),
                   (SELECT COUNT(*) FROM events),
                   (SELECT MIN(updated_utc) FROM contexts);
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        return new FleetContextStorage(
            FileLength(_databasePath) + FileLength(_databasePath + "-wal"),
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : ParseTime(reader.GetString(3)));
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>Contexts in which nothing has happened since <paramref name="cutoff"/>, oldest first.</summary>
    public IReadOnlyList<FleetContextSummary> ContextsIdleSince(DateTimeOffset cutoff)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, c.status, c.created_utc, c.updated_utc, c.message_count,
                   (SELECT COUNT(*) FROM events e WHERE e.context_id = c.id),
                   (SELECT MAX(sequence) FROM checkpoints p WHERE p.context_id = c.id)
            FROM contexts c
            WHERE c.updated_utc < @cutoff
            ORDER BY c.updated_utc ASC;
            """;
        command.Parameters.AddWithValue("@cutoff", Format(cutoff));
        using SqliteDataReader reader = command.ExecuteReader();
        var contexts = new List<FleetContextSummary>();
        while (reader.Read())
        {
            contexts.Add(ReadContextSummary(reader));
        }

        return contexts;
    }

    /// <summary>Deletes several contexts, and everything recorded under them, in one transaction.</summary>
    public int DeleteContexts(IReadOnlyCollection<string> contextIds)
    {
        int deleted = 0;
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (string contextId in contextIds.Where(FleetRequestContext.IsValidContextId))
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM agent_sessions WHERE context_id=@id; DELETE FROM contexts WHERE id=@id;";
                command.Parameters.AddWithValue("@id", contextId);
                deleted += command.ExecuteNonQuery() > 0 ? 1 : 0;
            }

            transaction.Commit();
            FlushDeletes(connection);
        }

        return deleted;
    }

    /// <summary>
    /// Keeps the newest <paramref name="keepPerContext"/> delivered context blocks of each context and deletes the
    /// older ones. There is one per model call, each up to a few thousand characters, and they are only a copy
    /// of what a model was handed, kept so recent behaviour can be inspected. A pinned one is kept.
    /// </summary>
    public int TrimDeliveries(int keepPerContext)
    {
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM events WHERE id IN (
                    SELECT id FROM (
                        SELECT id, ROW_NUMBER() OVER (PARTITION BY context_id ORDER BY id DESC) AS newest
                        FROM events WHERE kind = @kind AND pinned = 0)
                    WHERE newest > @keep);
                """;
            command.Parameters.AddWithValue("@kind", FleetContextEventKind.ContextAssembled);
            command.Parameters.AddWithValue("@keep", Math.Max(1, keepPerContext));
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Hands the space of deleted records back to the disk (the file does not shrink by itself). Rewrites the
    /// whole file, so it runs after a cleanup, never on a request.
    /// </summary>
    public void Shrink()
    {
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
    {
        JsonElement serialized = await agent.SerializeSessionAsync(session, JsonOptions, cancellationToken);
        string agentName = agent.Name ?? agent.GetType().Name;
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_sessions(agent_name, context_id, state_json, updated_utc)
                VALUES(@agent,@context,@state,@updated)
                ON CONFLICT(agent_name, context_id) DO UPDATE SET state_json=excluded.state_json, updated_utc=excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("@agent", agentName);
            command.Parameters.AddWithValue("@context", sessionStoreId);
            command.Parameters.AddWithValue("@state", serialized.GetRawText());
            command.Parameters.AddWithValue("@updated", Format(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }

        if (FleetRequestContext.IsValidContextId(sessionStoreId))
        {
            EnsureContext(sessionStoreId);
            SaveCheckpoint(sessionStoreId, null, "agent-session", new { agent = agentName, serialized });
        }
    }

    // Like the framework's own stores, a session that was never saved comes back as a fresh one.
    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        string agentName = agent.Name ?? agent.GetType().Name;
        string? state;
        using (SqliteConnection connection = OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT state_json FROM agent_sessions WHERE agent_name=@agent AND context_id=@context;";
            command.Parameters.AddWithValue("@agent", agentName);
            command.Parameters.AddWithValue("@context", sessionStoreId);
            state = command.ExecuteScalar() as string;
        }

        if (state is null)
        {
            return await agent.CreateSessionAsync(cancellationToken);
        }

        using JsonDocument document = JsonDocument.Parse(state);
        return await agent.DeserializeSessionAsync(document.RootElement, JsonOptions, cancellationToken);
    }

    public override ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        string agentName = agent.Name ?? agent.GetType().Name;
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_sessions WHERE agent_name=@agent AND context_id=@context;";
            command.Parameters.AddWithValue("@agent", agentName);
            command.Parameters.AddWithValue("@context", sessionStoreId);
            command.ExecuteNonQuery();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The chats that have messages, newest first: what the Sessions list shows.</summary>
    public IReadOnlyList<SessionSummary> ListSessions()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, created_utc, updated_utc, message_count
            FROM contexts WHERE message_count > 0 ORDER BY updated_utc DESC;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var sessions = new List<SessionSummary>();
        while (reader.Read())
        {
            sessions.Add(new SessionSummary(
                reader.GetString(0), reader.GetString(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)), reader.GetInt32(4)));
        }

        return sessions;
    }

    public FleetContextEvent? GetEvent(string contextId, long eventId)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, context_id, task_id, kind, actor, agent, node, at_utc, payload_json, pinned
            FROM events WHERE context_id=@context AND id=@id;
            """;
        command.Parameters.AddWithValue("@context", contextId);
        command.Parameters.AddWithValue("@id", eventId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadEvent(reader) : null;
    }

    public IReadOnlyList<FleetContextEvent> EventsOfKinds(string contextId, IReadOnlyCollection<string> kinds, int limit = 40, bool pinnedOnly = false)
    {
        if (kinds.Count == 0)
        {
            return [];
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        string[] names = kinds.Select((_, index) => $"@k{index}").ToArray();
        command.CommandText = $"""
            SELECT id, context_id, task_id, kind, actor, agent, node, at_utc, payload_json, pinned
            FROM events
            WHERE context_id=@context AND kind IN ({string.Join(',', names)}){(pinnedOnly ? " AND pinned=1" : string.Empty)}
            ORDER BY id DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@context", contextId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        int index = 0;
        foreach (string kind in kinds)
        {
            command.Parameters.AddWithValue(names[index++], kind);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        var events = new List<FleetContextEvent>();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        events.Reverse();
        return events;
    }

    /// <summary>Every event of a context, oldest first. For compaction and export, not for prompts.</summary>
    public IReadOnlyList<FleetContextEvent> AllEvents(string contextId, long afterEventId = 0, int limit = 20000)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, context_id, task_id, kind, actor, agent, node, at_utc, payload_json, pinned
            FROM events WHERE context_id=@context AND id>@after ORDER BY id ASC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@context", contextId);
        command.Parameters.AddWithValue("@after", afterEventId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100000));
        using SqliteDataReader reader = command.ExecuteReader();
        var events = new List<FleetContextEvent>();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    /// <summary>Removes a chat from the sessions list without touching the record behind it (plans, handoffs, files).</summary>
    public bool ClearMessages(string contextId)
    {
        if (!FleetRequestContext.IsValidContextId(contextId))
        {
            return false;
        }

        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE contexts SET messages_json='[]', message_count=0, updated_utc=@now WHERE id=@id;";
            command.Parameters.AddWithValue("@id", contextId);
            command.Parameters.AddWithValue("@now", Format(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool PinEvent(string contextId, long eventId, bool pinned)
    {
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE events SET pinned=@pinned WHERE context_id=@context AND id=@id;";
            command.Parameters.AddWithValue("@pinned", pinned ? 1 : 0);
            command.Parameters.AddWithValue("@context", contextId);
            command.Parameters.AddWithValue("@id", eventId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<FleetTaskRecord> ListTasks(string contextId)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, context_id, kind, parent_task_id, status, title, created_utc, updated_utc
            FROM tasks WHERE context_id=@context ORDER BY created_utc ASC, id ASC;
            """;
        command.Parameters.AddWithValue("@context", contextId);
        using SqliteDataReader reader = command.ExecuteReader();
        var tasks = new List<FleetTaskRecord>();
        while (reader.Read())
        {
            tasks.Add(new FleetTaskRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetString(5), ParseTime(reader.GetString(6)), ParseTime(reader.GetString(7))));
        }

        return tasks;
    }

    /// <summary>The newest handoff addressed to <paramref name="taskId"/>, else the newest of the context.</summary>
    public FleetHandoffRecord? LatestHandoff(string contextId, string? taskId)
    {
        IReadOnlyList<FleetHandoffRecord> all = ListHandoffs(contextId, 100);
        return (taskId is null ? null : all.FirstOrDefault(record => record.TaskId == taskId)) ?? all.FirstOrDefault();
    }

    private bool IsMigrated(string id)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM metadata WHERE key=@key;";
        command.Parameters.AddWithValue("@key", "migrated:" + id);
        return command.ExecuteScalar() is not null;
    }

    private void MarkMigrated(string id)
    {
        lock (_writeLock)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO metadata(key, value) VALUES(@key, @value) ON CONFLICT(key) DO NOTHING;";
            command.Parameters.AddWithValue("@key", "migrated:" + id);
            command.Parameters.AddWithValue("@value", Format(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    // Each old JSON session is imported exactly once. Importing again on every start would overwrite
    // a chat continued since with the stale file, so a marker records which ones are done. The JSON
    // files stay where they are as a backup.
    private void MigrateLegacySessions()
    {
        if (!Directory.Exists(_legacySessionsDirectory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(_legacySessionsDirectory, "*.json"))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = document.RootElement;
                string? id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
                if (!FleetRequestContext.IsValidContextId(id) || !root.TryGetProperty("messages", out JsonElement messages))
                {
                    continue;
                }

                if (IsMigrated(id!))
                {
                    continue;
                }

                MarkMigrated(id!);
                if (GetContext(id!) is not null)
                {
                    // The journal already has this chat (created since the file was written): keep it.
                    continue;
                }

                string? title = root.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() : null;
                UpsertMessages(id!, title, messages, "legacy-json");
            }
            catch (Exception exception) when (exception is JsonException or IOException or SqliteException)
            {
                // A corrupt legacy file is left in place for manual recovery and does
                // not prevent the durable store from starting.
            }
        }
    }

    private static FleetContextSummary ReadContextSummary(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)), ParseTime(reader.GetString(4)),
        reader.GetInt32(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt64(7));

    private static FleetContextEvent ReadEvent(SqliteDataReader reader)
    {
        using JsonDocument document = JsonDocument.Parse(reader.GetString(8));
        return new FleetContextEvent(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), ParseTime(reader.GetString(7)), document.RootElement.Clone(), reader.GetInt32(9) != 0);
    }

    private static long InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contextId,
        string? taskId,
        string kind,
        string? actor,
        string? agent,
        string? node,
        DateTimeOffset atUtc,
        string payloadJson,
        bool pinned)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events(context_id, task_id, kind, actor, agent, node, at_utc, payload_json, pinned)
            VALUES(@context,@task,@kind,@actor,@agent,@node,@at,@payload,@pinned);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@context", contextId);
        command.Parameters.AddWithValue("@task", (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@actor", (object?)actor ?? DBNull.Value);
        command.Parameters.AddWithValue("@agent", (object?)agent ?? DBNull.Value);
        command.Parameters.AddWithValue("@node", (object?)node ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", Format(atUtc));
        command.Parameters.AddWithValue("@payload", payloadJson);
        command.Parameters.AddWithValue("@pinned", pinned ? 1 : 0);
        return (long)command.ExecuteScalar()!;
    }

    private static void EnsureContextRow(SqliteConnection connection, SqliteTransaction transaction, string contextId)
    {
        string now = Format(DateTimeOffset.UtcNow);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO contexts(id, title, status, created_utc, updated_utc, messages_json, message_count)
            VALUES(@id, 'Untitled', 'active', @now, @now, '[]', 0)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@id", contextId);
        command.Parameters.AddWithValue("@now", now);
        command.ExecuteNonQuery();
    }

    private static void TouchContext(SqliteConnection connection, SqliteTransaction transaction, string contextId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE contexts SET updated_utc=@updated WHERE id=@id;";
        command.Parameters.AddWithValue("@updated", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@id", contextId);
        command.ExecuteNonQuery();
    }

    private static string CleanTitle(string? title)
    {
        string cleaned = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim().ReplaceLineEndings(" ");
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // A file is "the same" when it exists and matches what was recorded. A file recorded as absent is the
    // same while it stays absent. A file that exists but cannot be read right now (another program holds it
    // open) is given the benefit of the doubt: an unreadable file is not evidence that it changed.
    private static bool Matches(bool exists, string? recordedHash, string? currentHash) =>
        recordedHash is null
            ? !exists
            : exists && (currentHash is null || string.Equals(recordedHash, currentHash, StringComparison.OrdinalIgnoreCase));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, string Hash)> HashCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // Streams the file instead of loading it (a step can name a large file), and remembers the answer while
    // the file's size and time are unchanged, because the inspection view asks every few seconds. A file
    // written in the last couple of seconds is never served from the cache: two quick writes can share a timestamp.
    private static string? HashFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            bool settled = DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(2);
            if (settled && HashCache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.WrittenUtc == info.LastWriteTimeUtc)
            {
                return cached.Hash;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (settled)
            {
                if (HashCache.Count > 5000)
                {
                    HashCache.Clear();
                }

                HashCache[path] = (info.Length, info.LastWriteTimeUtc, hash);
            }

            return hash;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static void ValidateContextId(string contextId)
    {
        if (!FleetRequestContext.IsValidContextId(contextId))
        {
            throw new ArgumentException("Context id must be a GUID.", nameof(contextId));
        }
    }
}
