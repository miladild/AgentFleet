using System.Text.Json;

namespace AgentFleet;

internal static class FleetContextEventKind
{
    public const string Message = "message";
    public const string MessageUpdated = "message-updated";
    public const string RunInput = "run-input";
    public const string AssistantOutput = "assistant-output";
    public const string ToolCall = "tool-call";
    public const string ToolResult = "tool-result";
    public const string Route = "route";
    public const string Decision = "decision";
    public const string Approval = "approval";
    public const string Verification = "verification";
    public const string Error = "error";
    public const string PlanTransition = "plan-transition";
    public const string ArtifactPublished = "artifact-published";
    public const string ArtifactDiverged = "artifact-diverged";
    public const string Checkpoint = "checkpoint";
    public const string Compaction = "compaction";
    public const string ContextAssembled = "context-assembled";
    public const string Feedback = "feedback";
}

internal sealed record FleetContextSummary(
    string Id,
    string Title,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount,
    int EventCount,
    long? LastCheckpointSequence);

/// <summary>How big the record is on disk and how much is in it.</summary>
internal sealed record FleetContextStorage(
    long Bytes,
    int Contexts,
    int Chats,
    long Events,
    DateTimeOffset? OldestActivityUtc);

internal sealed record FleetTaskRecord(
    string Id,
    string ContextId,
    string Kind,
    string? ParentTaskId,
    string Status,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record FleetContextEvent(
    long Id,
    string ContextId,
    string? TaskId,
    string Kind,
    string? Actor,
    string? Agent,
    string? Node,
    DateTimeOffset AtUtc,
    JsonElement Payload,
    bool Pinned);

internal sealed record FleetArtifact(
    string Id,
    string ContextId,
    string? TaskId,
    long? ProducingEventId,
    string Path,
    string MediaType,
    string? Sha256,
    long? Length,
    int Version,
    DateTimeOffset CreatedAtUtc,
    bool Exists,
    bool MatchesRecordedHash,
    JsonElement? Metadata);

internal sealed record HandoffEnvelope(
    int Version,
    string ContextId,
    string TaskId,
    string? FromAgent,
    string? ToAgent,
    DateTimeOffset CreatedAtUtc,
    string Goal,
    string CurrentTask,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> CompletedWork,
    IReadOnlyList<string> ArtifactIds,
    IReadOnlyList<string> VerificationEvidence,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> Risks,
    string NextAction,
    string CompletionCondition,
    IReadOnlyList<long> SourceEventIds);

internal sealed record FleetHandoffRecord(
    string Id,
    string ContextId,
    string TaskId,
    DateTimeOffset CreatedAtUtc,
    HandoffEnvelope Envelope);

internal sealed record FleetCheckpoint(
    string Id,
    string ContextId,
    string? TaskId,
    long Sequence,
    string Kind,
    DateTimeOffset CreatedAtUtc,
    JsonElement State);

internal sealed record FleetContextSummaryRecord(
    string Id,
    string ContextId,
    long StartSequence,
    long EndSequence,
    int Version,
    DateTimeOffset CreatedAtUtc,
    string Text,
    IReadOnlyList<long> SourceEventIds);

internal sealed record FleetContextInspection(
    FleetContextSummary Context,
    IReadOnlyList<FleetContextEvent> RecentEvents,
    IReadOnlyList<FleetHandoffRecord> Handoffs,
    IReadOnlyList<FleetArtifact> Artifacts,
    FleetCheckpoint? LatestCheckpoint,
    FleetContextSummaryRecord? LatestSummary,
    ContextAssemblyPreview Preview,
    IReadOnlyList<FleetContextEvent>? Pinned = null);

internal sealed record ContextAssemblyPreview(
    string ContextId,
    string? TaskId,
    string Text,
    IReadOnlyList<long> EventIds,
    IReadOnlyList<string> ArtifactIds,
    bool WasTruncated);

internal sealed record ArtifactVerification(
    string ArtifactId,
    string Path,
    bool Exists,
    bool MatchesRecordedHash,
    string? RecordedSha256,
    string? CurrentSha256);
