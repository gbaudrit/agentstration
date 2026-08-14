using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Contracts;

public sealed record WorkplaceWorkspaceResponse(string Id, string Name, string Type, string ApiVersion, string DisplayName, string? Description, IReadOnlyList<WorkspaceEntryReferenceResponse> Entries, int Version, DateTimeOffset PublishedAt)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record WorkplaceWorkspaceDraftResponse(WorkplaceWorkspaceDraft Value, WorkplaceWorkspace? Published);
public sealed record WorkspaceEntryReferenceResponse(string EntryResourceId, WorkspaceEntryRole Role, int Order)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record EntryResponse(string Id, string Name, string Type, string ApiVersion, string DisplayName, string? Description, EntryPresentation Presentation, EntryResolvedTarget ResolvedTarget, EntryBehavior Behavior, int Version, DateTimeOffset PublishedAt)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record EntryDraftResponse(EntryDraft Value, EntryResource? Published);
public sealed record EntryValidationResponse(bool IsValid, IReadOnlyList<EntryValidationIssueContract> Issues);
public sealed record EntryValidationIssueContract(string Code, string Message);
public sealed record EntryDependencyResponse(string ResourceId, string ResourceType, string Relationship);
public sealed record ResourcePickerItem(string ResourceId, string Name, string? Description, string Version, string State, string ResourceType, IReadOnlyDictionary<string, string>? Details = null)
{
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
}
public sealed record CreateInteractionRequest(string WorkspaceId, IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<WorkAttachmentRequest>? Attachments = null);
public sealed record EntrySubmissionResponse(InteractionResponse Interaction, WorkplaceAction Action, WorkTaskResponse? Task);
public sealed record InteractionResponse(Guid Id, string WorkspaceId, string EntryId, InteractionStatus Status, DateTimeOffset StartedAt, DateTimeOffset LastActivityAt, IReadOnlyDictionary<string, JsonElement> InputValues, IReadOnlyList<WorkAttachment> Attachments, IReadOnlyList<ConversationMessage> Messages, Guid? PendingActionId, Guid? TaskId, WorkplaceAction? ImmediateResult, long Version, string? LastFlowRunId = null, Guid? LastTriggerMessageId = null);
public sealed record AddConversationMessageRequest(string Content);
public sealed record AddConversationMessageResponse(ConversationMessage Message, InteractionResponse Interaction, WorkplaceAction Action, WorkTaskResponse? Task);
public sealed record PendingActionResponseRequest(string ResumeToken, IReadOnlyDictionary<string, JsonElement> Values);
public sealed record PendingActionContract(Guid Id, string WorkspaceId, Guid InteractionId, Guid? WorkTaskId, string? FlowRunId, PendingActionKind Kind, PendingActionStatus Status, string Title, string? Description, IReadOnlyList<EntryFieldDefinition> Fields, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? ResolvedAt, long Version);
public sealed record PendingActionResolutionResponse(PendingActionContract PendingAction, WorkplaceAction NextAction, InteractionResponse Interaction, WorkTaskResponse? Task);
public sealed record WorkTaskResponse(Guid Id, string WorkspaceId, string EntryId, Guid InteractionId, string Title, string? Description, WorkTaskStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? FlowRunId, IReadOnlyList<WorkMessage> Conversation, IReadOnlyList<WorkInteraction> Activities, IReadOnlyList<WorkArtifact> Artifacts, WorkResult? Result, WorkError? Error, WorkplaceAction CurrentAction, long Version);
public sealed record WorkTaskPageResponse(IReadOnlyList<WorkTaskResponse> Value);
public sealed record WorkNotificationPageResponse(IReadOnlyList<WorkNotification> Value);
public sealed record UnreadNotificationCountResponse(int Count);
public sealed record InteractionPageResponse(IReadOnlyList<InteractionResponse> Value);

public abstract record WorkplaceEventContract(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp);
public sealed record InteractionUpdatedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, Guid InteractionId, InteractionStatus Status) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record MessageAddedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, ConversationMessage Message) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record PendingActionCreatedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, PendingActionContract PendingAction) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record PendingActionResolvedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, Guid PendingActionId, Guid? TaskId = null) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record TaskCreatedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, Guid TaskId) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record FlowRunStartedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, Guid InteractionId, Guid TaskId, string? ParentFlowRunId) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record FlowRunCompletedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, Guid InteractionId, Guid TaskId, string FlowRunId) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record TaskStatusChangedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, Guid TaskId, WorkTaskStatus Status, long Version) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record TaskActivityAddedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, WorkTaskActivity Activity) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record TaskResultAddedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, WorkTaskResult Result) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record WorkTaskArtifactEventContract(Guid Id, Guid WorkTaskId, string? FlowRunId, string Name, string ContentType, long Length, DateTimeOffset CreatedAt, int Sequence);
public sealed record TaskArtifactAddedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, WorkTaskArtifactEventContract Artifact) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record NotificationCreatedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, WorkNotification Notification) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record NotificationUpdatedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, WorkNotification Notification) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
public sealed record UnreadNotificationCountChangedEvent(string EventId, string WorkspaceId, long Sequence, DateTimeOffset Timestamp, int Count) : WorkplaceEventContract(EventId, WorkspaceId, Sequence, Timestamp);
