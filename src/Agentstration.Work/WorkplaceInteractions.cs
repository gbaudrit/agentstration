using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Flow;
using Agentstration.Resources;

namespace Agentstration.Work;

public sealed record ConversationMessage(
    Guid Id, WorkspaceId WorkspaceId, InteractionId InteractionId, WorkTaskId? WorkTaskId,
    ConversationRole Role, string Content, DateTimeOffset CreatedAt, string? AgentResourceId = null,
    IReadOnlyList<WorkAttachment>? Attachments = null, PendingActionId? PendingActionId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record PendingActionResponse(IReadOnlyDictionary<string, JsonElement> Values, DateTimeOffset SubmittedAt);

public sealed record PendingAction
{
    public required PendingActionId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public InteractionId? InteractionId { get; init; }
    public WorkTaskId? WorkTaskId { get; init; }
    public string? FlowRunId { get; init; }
    public string? ExternalInputRequestId { get; init; }
    public required PendingActionKind Kind { get; init; }
    public PendingActionStatus Status { get; init; } = PendingActionStatus.Pending;
    public required string Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<EntryFieldDefinition> Fields { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public PendingActionResponse? Response { get; init; }
    public required string ResumeTokenHash { get; init; }
    public int ResumeStep { get; init; }
    public long Version { get; init; } = 1;
}

public sealed record WorkTaskActivity(
    WorkTaskActivityId Id, WorkspaceId WorkspaceId, WorkTaskId WorkTaskId,
    WorkTaskActivityType Type, string Title, string? Description, DateTimeOffset CreatedAt,
    WorkActorKind ActorKind, string? FlowRunId = null, IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record WorkTaskResult(
    WorkTaskResultId Id, WorkspaceId WorkspaceId, WorkTaskId WorkTaskId, string? FlowRunId,
    WorkTaskResultKind Kind, string Title, JsonElement Content, DateTimeOffset CreatedAt, int Sequence = 1);

public sealed record ArtifactReference(string StorageKey, string ContentType, long Length);
public sealed record ArtifactContent(string Name, string ContentType, Stream Content);
public sealed record WorkTaskArtifact(
    WorkTaskArtifactId Id, WorkspaceId WorkspaceId, WorkTaskId WorkTaskId, string? FlowRunId,
    string Name, string ContentType, long Length, string StorageKey, DateTimeOffset CreatedAt, int Sequence = 1);

public sealed record WorkNotification
{
    public required WorkNotificationId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required WorkNotificationKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReadAt { get; init; }
    public WorkTaskId? WorkTaskId { get; init; }
    public InteractionId? InteractionId { get; init; }
    public PendingActionId? PendingActionId { get; init; }
    public string? ActionUrl { get; init; }
    public long Version { get; init; } = 1;
}

public sealed record WorkplaceInteraction
{
    public required InteractionId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required EntryId EntryId { get; init; }
    public EntryResource? EntrySnapshot { get; init; }
    public InteractionStatus Status { get; init; } = InteractionStatus.Active;
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastActivityAt { get; init; }
    public IReadOnlyDictionary<string, JsonElement> InputValues { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<WorkAttachment> Attachments { get; init; } = [];
    public IReadOnlyList<ConversationMessage> Messages { get; init; } = [];
    public PendingActionId? PendingActionId { get; init; }
    public WorkTaskId? TaskId { get; init; }
    public string? LastFlowRunId { get; init; }
    public Guid? LastTriggerMessageId { get; init; }
    public WorkplaceAction? ImmediateResult { get; init; }
    public string? ClosedReason { get; init; }
    public long Version { get; init; } = 1;
}
