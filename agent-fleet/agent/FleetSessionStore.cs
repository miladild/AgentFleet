using System.Text.Json;

namespace AgentFleet;

internal sealed record SessionSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount);

internal sealed record SessionRecord(
    string Id,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    JsonElement Messages);

/// <summary>
/// Durable, file-backed conversation history: one JSON file per session under
/// AppContext.BaseDirectory/sessions (next to the exe, dev or deployed -
/// deliberately not inside anything a "dotnet publish" would delete). The
/// backend owns storage; the frontend owns session identity/lifecycle
/// (creating ids, deciding when to switch or start a new one) and calls this
/// through plain CRUD endpoints, since the AG-UI protocol layer between them
/// doesn't expose a usable thread identifier to this process.
/// </summary>
internal sealed class FleetSessionStore
{
    private readonly string _directory;

    public FleetSessionStore(IConfiguration configuration)
    {
        _directory = configuration["FLEET_SESSIONS_DIR"]
            ?? Path.Combine(AppContext.BaseDirectory, "sessions");
        Directory.CreateDirectory(_directory);
    }

    public IReadOnlyList<SessionSummary> List()
    {
        var summaries = new List<SessionSummary>();
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                summaries.Add(new SessionSummary(
                    root.GetProperty("id").GetString()!,
                    root.GetProperty("title").GetString()!,
                    root.GetProperty("createdAtUtc").GetDateTimeOffset(),
                    root.GetProperty("updatedAtUtc").GetDateTimeOffset(),
                    root.GetProperty("messages").GetArrayLength()));
            }
            catch (JsonException)
            {
                // A partially-written or corrupt file shouldn't break the whole list.
            }
        }

        return summaries.OrderByDescending(summary => summary.UpdatedAtUtc).ToList();
    }

    public SessionRecord? Get(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        string path = PathFor(id);
        if (!File.Exists(path))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        return new SessionRecord(
            root.GetProperty("id").GetString()!,
            root.GetProperty("title").GetString()!,
            root.GetProperty("createdAtUtc").GetDateTimeOffset(),
            root.GetProperty("updatedAtUtc").GetDateTimeOffset(),
            root.GetProperty("messages").Clone());
    }

    public SessionSummary Upsert(string id, string? title, JsonElement messages)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException("Session id must be a GUID.", nameof(id));
        }

        string path = PathFor(id);
        DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
        if (File.Exists(path))
        {
            using FileStream existingStream = File.OpenRead(path);
            using JsonDocument existingDocument = JsonDocument.Parse(existingStream);
            createdAtUtc = existingDocument.RootElement.GetProperty("createdAtUtc").GetDateTimeOffset();
        }

        DateTimeOffset updatedAtUtc = DateTimeOffset.UtcNow;
        string resolvedTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title;

        string json = JsonSerializer.Serialize(new
        {
            id,
            title = resolvedTitle,
            createdAtUtc,
            updatedAtUtc,
            messages
        });
        File.WriteAllText(path, json);

        return new SessionSummary(id, resolvedTitle, createdAtUtc, updatedAtUtc, messages.GetArrayLength());
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id))
        {
            return false;
        }

        string path = PathFor(id);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string PathFor(string id) => Path.Combine(_directory, $"{id}.json");

    // GUID-only ids close off path traversal (no "../") without needing separate
    // sanitization, since the frontend already mints ids via crypto.randomUUID().
    private static bool IsValidId(string id) => Guid.TryParse(id, out _);
}
