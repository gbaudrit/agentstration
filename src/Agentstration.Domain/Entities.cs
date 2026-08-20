namespace Agentstration.Domain;

public enum ItemStatus { Queued, Processing, Processed, Failed }
public enum MissionStatus { Active, Paused, Archived }
public enum MissionRunStatus { Running, Completed, Failed }

public sealed record Workspace(WorkspaceId Id, string Name, DateTimeOffset CreatedAt);

public sealed record Inbox(
    InboxId Id,
    WorkspaceId WorkspaceId,
    string Name,
    string Slug,
    string Description,
    string ApiKeyHash,
    DateTimeOffset CreatedAt);

public sealed record Item(
    ItemId Id,
    WorkspaceId WorkspaceId,
    InboxId InboxId,
    string ContentType,
    string ContentHash,
    string? ExternalId,
    ItemStatus Status,
    DateTimeOffset CreatedAt,
    string? Error = null);

public sealed record RawContent(
    ItemId ItemId,
    WorkspaceId WorkspaceId,
    string Value,
    string MediaType,
    string? SourceUrl,
    DateTimeOffset CreatedAt);

public sealed record NormalizedContent(
    ItemId ItemId,
    WorkspaceId WorkspaceId,
    string Value,
    DateTimeOffset CreatedAt);

public sealed record ItemAnalysis(
    Guid Id,
    WorkspaceId WorkspaceId,
    ItemId ItemId,
    string Summary,
    IReadOnlyList<string> Categories,
    DateTimeOffset CreatedAt);

public sealed record Mission(
    MissionId Id,
    WorkspaceId WorkspaceId,
    string Name,
    string Objective,
    Uri Source,
    TimeSpan Frequency,
    decimal? Threshold,
    MissionStatus Status,
    DateTimeOffset NextRunAt,
    DateTimeOffset CreatedAt);

public sealed record MissionRun(
    MissionRunId Id,
    WorkspaceId WorkspaceId,
    MissionId MissionId,
    MissionRunStatus Status,
    decimal? Observation,
    bool Changed,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record Notification(
    Guid Id,
    WorkspaceId WorkspaceId,
    MissionId MissionId,
    string Message,
    DateTimeOffset CreatedAt);

public sealed record AuditEntry(
    Guid Id,
    WorkspaceId WorkspaceId,
    string Action,
    string SubjectId,
    DateTimeOffset CreatedAt);
