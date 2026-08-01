namespace Agentstration.Domain;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

public sealed record ItemReceived(WorkspaceId WorkspaceId, ItemId ItemId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record ItemNormalized(WorkspaceId WorkspaceId, ItemId ItemId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record ItemProcessingRequested(WorkspaceId WorkspaceId, ItemId ItemId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record ItemProcessed(WorkspaceId WorkspaceId, ItemId ItemId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record MissionCreated(WorkspaceId WorkspaceId, MissionId MissionId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record MissionTriggered(WorkspaceId WorkspaceId, MissionId MissionId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record MissionRunStarted(WorkspaceId WorkspaceId, MissionId MissionId, MissionRunId RunId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record MissionRunCompleted(WorkspaceId WorkspaceId, MissionId MissionId, MissionRunId RunId, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record MissionRunFailed(WorkspaceId WorkspaceId, MissionId MissionId, MissionRunId RunId, string Error, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record NotificationRequested(WorkspaceId WorkspaceId, MissionId MissionId, string Message, DateTimeOffset OccurredAt) : IDomainEvent;
