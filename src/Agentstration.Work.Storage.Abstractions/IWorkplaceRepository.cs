using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public interface IWorkplaceRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task UpsertWorkspaceAsync(WorkplaceWorkspace workspace, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceWorkspace>> ListWorkspacesAsync(CancellationToken cancellationToken);
    Task<WorkplaceWorkspace?> GetWorkspaceAsync(WorkplaceWorkspaceId workspaceId, CancellationToken cancellationToken);
    Task UpsertEntryAsync(EntryResource entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntryResource>> ListEntriesAsync(CancellationToken cancellationToken);
    Task<EntryResource?> GetEntryAsync(EntryId entryId, CancellationToken cancellationToken);
    Task CreateInteractionAsync(WorkplaceInteraction interaction, CancellationToken cancellationToken);
    Task<WorkplaceInteraction?> GetInteractionAsync(WorkplaceWorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceInteraction>> ListInteractionsAsync(WorkplaceWorkspaceId workspaceId, int take, CancellationToken cancellationToken);
    Task SaveInteractionAsync(WorkplaceInteraction interaction, long expectedVersion, CancellationToken cancellationToken);
    Task AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(WorkplaceWorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken);
    Task CreatePendingActionAsync(PendingAction action, CancellationToken cancellationToken);
    Task<PendingAction?> GetPendingActionAsync(WorkplaceWorkspaceId workspaceId, PendingActionId actionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingAction>> ListPendingActionsAsync(WorkplaceWorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken);
    Task SavePendingActionAsync(PendingAction action, long expectedVersion, CancellationToken cancellationToken);
    Task AddActivityAsync(WorkTaskActivity activity, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(WorkplaceWorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken);
    Task AddResultAsync(WorkTaskResult result, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(WorkplaceWorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken);
    Task AddArtifactAsync(WorkTaskArtifact artifact, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(WorkplaceWorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken);
    Task<WorkTaskArtifact?> GetArtifactAsync(WorkplaceWorkspaceId workspaceId, WorkTaskId taskId, WorkTaskArtifactId artifactId, CancellationToken cancellationToken);
    Task CreateNotificationAsync(WorkNotification notification, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(WorkplaceWorkspaceId workspaceId, bool? unreadOnly, CancellationToken cancellationToken);
    Task<WorkNotification?> GetNotificationAsync(WorkplaceWorkspaceId workspaceId, WorkNotificationId notificationId, CancellationToken cancellationToken);
    Task SaveNotificationAsync(WorkNotification notification, long expectedVersion, CancellationToken cancellationToken);
}

public interface IArtifactStore
{
    Task<ArtifactReference> SaveAsync(ArtifactContent content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(ArtifactReference reference, CancellationToken cancellationToken);
    Task DeleteAsync(ArtifactReference reference, CancellationToken cancellationToken);
}

public sealed class WorkplaceConcurrencyException(string message) : Exception(message);
