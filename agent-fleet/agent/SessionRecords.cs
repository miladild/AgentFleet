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
