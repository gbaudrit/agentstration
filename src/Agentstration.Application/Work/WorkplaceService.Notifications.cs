using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed partial class WorkplaceService
{
    public Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(WorkspaceId workspaceId, bool? unreadOnly, CancellationToken token) => repository.ListNotificationsAsync(workspaceId, unreadOnly, token);

    public async Task<int> UnreadCountAsync(WorkspaceId workspaceId, CancellationToken token) => (await repository.ListNotificationsAsync(workspaceId, true, token)).Count;

    public async Task<WorkNotification> MarkNotificationReadAsync(WorkspaceId workspaceId, WorkNotificationId id, CancellationToken token) { var value = await repository.GetNotificationAsync(workspaceId, id, token) ?? throw new KeyNotFoundException($"Notification '{id}' was not found."); if (value.ReadAt is not null) return value; var updated = value with { ReadAt = timeProvider.GetUtcNow(), Version = value.Version + 1 }; await repository.SaveNotificationAsync(updated, value.Version, token); await PublishAsync(new NotificationUpdatedEvent(EventId(), workspaceId.Value, Sequence(), updated.ReadAt.Value, updated), token); return updated; }

    public async Task MarkAllNotificationsReadAsync(WorkspaceId workspaceId, CancellationToken token) { foreach (var value in await repository.ListNotificationsAsync(workspaceId, true, token)) await MarkNotificationReadAsync(workspaceId, value.Id, token); await PublishAsync(new UnreadNotificationCountChangedEvent(EventId(), workspaceId.Value, Sequence(), timeProvider.GetUtcNow(), 0), token); }

    private async Task CreateNotificationAsync(WorkspaceId workspaceId, WorkNotificationKind kind, string title, string message, InteractionId? interactionId, WorkTaskId? taskId, PendingActionId? actionId, string? url, CancellationToken token)
    {
        var notification = new WorkNotification { Id = WorkNotificationId.New(), WorkspaceId = workspaceId, Kind = kind, Title = title, Message = message, CreatedAt = timeProvider.GetUtcNow(), InteractionId = interactionId, WorkTaskId = taskId, PendingActionId = actionId, ActionUrl = url }; await repository.CreateNotificationAsync(notification, token); await PublishAsync(new NotificationCreatedEvent(EventId(), workspaceId.Value, Sequence(), notification.CreatedAt, notification), token); await PublishAsync(new UnreadNotificationCountChangedEvent(EventId(), workspaceId.Value, Sequence(), notification.CreatedAt, await UnreadCountAsync(workspaceId, token)), token);
    }

    private async Task PublishAsync(WorkplaceEventContract value, CancellationToken token) { foreach (var sink in eventSinks) await sink.PublishAsync(value, token); }

    private long Sequence() => Interlocked.Increment(ref eventSequence);

    private static string EventId() => Guid.NewGuid().ToString("N");
}

