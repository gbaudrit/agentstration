using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Work.Storage.Abstractions;

public interface IWorkplaceRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task UpsertDashboardAsync(WorkplaceDashboard dashboard, CancellationToken cancellationToken);
    Task ReplaceDefaultDashboardAsync(WorkplaceDashboard dashboard, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceDashboard>> ListDashboardsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken);
    Task<WorkplaceDashboard?> GetDashboardAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken);
    Task DeleteDashboardAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken);
    Task UpsertDashboardDraftAsync(WorkplaceDashboardDraft draft, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceDashboardDraft>> ListDashboardDraftsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken);
    Task<WorkplaceDashboardDraft?> GetDashboardDraftAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken);
    Task DeleteDashboardDraftAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken);
    Task UpsertEntryAsync(EntryResource entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntryResource>> ListEntriesAsync(WorkspaceId workspaceId, CancellationToken cancellationToken);
    Task<EntryResource?> GetEntryAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken);
    Task DeleteEntryAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken);
    Task UpsertEntryDraftAsync(EntryDraft draft, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntryDraft>> ListEntryDraftsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken);
    Task<EntryDraft?> GetEntryDraftAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken);
    Task DeleteEntryDraftAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceInteraction>> ListEntryInteractionsAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken);
    Task CreateInteractionAsync(WorkplaceInteraction interaction, CancellationToken cancellationToken);
    Task<WorkplaceInteraction?> GetInteractionAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkplaceInteraction>> ListInteractionsAsync(WorkspaceId workspaceId, int take, CancellationToken cancellationToken);
    Task SaveInteractionAsync(WorkplaceInteraction interaction, long expectedVersion, CancellationToken cancellationToken);
    Task AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationMessage>> ListMessagesAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken);
    Task CreatePendingActionAsync(PendingAction action, CancellationToken cancellationToken);
    Task<PendingAction?> GetPendingActionAsync(WorkspaceId workspaceId, PendingActionId actionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingAction>> ListPendingActionsAsync(WorkspaceId workspaceId, InteractionId interactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingAction>> ListPendingActionsForTaskAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken);
    Task SavePendingActionAsync(PendingAction action, long expectedVersion, CancellationToken cancellationToken);
    Task AddActivityAsync(WorkTaskActivity activity, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskActivity>> ListActivitiesAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken);
    Task AddResultAsync(WorkTaskResult result, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskResult>> ListResultsAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken);
    Task AddArtifactAsync(WorkTaskArtifact artifact, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkTaskArtifact>> ListArtifactsAsync(WorkspaceId workspaceId, WorkTaskId taskId, CancellationToken cancellationToken);
    Task<WorkTaskArtifact?> GetArtifactAsync(WorkspaceId workspaceId, WorkTaskId taskId, WorkTaskArtifactId artifactId, CancellationToken cancellationToken);
    Task CreateNotificationAsync(WorkNotification notification, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(WorkspaceId workspaceId, bool? unreadOnly, CancellationToken cancellationToken);
    Task<WorkNotification?> GetNotificationAsync(WorkspaceId workspaceId, WorkNotificationId notificationId, CancellationToken cancellationToken);
    Task SaveNotificationAsync(WorkNotification notification, long expectedVersion, CancellationToken cancellationToken);
}

public interface IArtifactStore
{
    Task<ArtifactReference> SaveAsync(WorkspaceId workspaceId, ArtifactContent content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(WorkspaceId workspaceId, ArtifactReference reference, CancellationToken cancellationToken);
    Task DeleteAsync(WorkspaceId workspaceId, ArtifactReference reference, CancellationToken cancellationToken);
}

public sealed class WorkplaceConcurrencyException(string message) : Exception(message);
