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
    public sealed record DeliverNotificationCommand(
        WorkspaceId WorkspaceId,
        string DeliveryKey,
        string Title,
        string Message,
        string? ActionUrl = null,
        string? CorrelationId = null,
        string? SourceRunId = null,
        string? SourceStepId = null,
        string? SourceToolCallId = null);

    public sealed record NotificationDelivery(WorkNotification Notification, bool Recovered);

    public Task<IReadOnlyList<WorkNotification>> ListNotificationsAsync(WorkspaceId workspaceId, bool? unreadOnly, CancellationToken token) => repository.ListNotificationsAsync(workspaceId, unreadOnly, token);

    public async Task<int> UnreadCountAsync(WorkspaceId workspaceId, CancellationToken token) => (await repository.ListNotificationsAsync(workspaceId, true, token)).Count;

    public async Task<WorkNotification> MarkNotificationReadAsync(WorkspaceId workspaceId, WorkNotificationId id, CancellationToken token) { var value = await repository.GetNotificationAsync(workspaceId, id, token) ?? throw new KeyNotFoundException($"Notification '{id}' was not found."); if (value.ReadAt is not null) return value; var updated = value with { ReadAt = timeProvider.GetUtcNow(), Version = value.Version + 1 }; await repository.SaveNotificationAsync(updated, value.Version, token); await PublishAsync(new NotificationUpdatedEvent(EventId(), workspaceId.Value, Sequence(), updated.ReadAt.Value, updated), token); return updated; }

    public async Task MarkAllNotificationsReadAsync(WorkspaceId workspaceId, CancellationToken token) { foreach (var value in await repository.ListNotificationsAsync(workspaceId, true, token)) await MarkNotificationReadAsync(workspaceId, value.Id, token); await PublishAsync(new UnreadNotificationCountChangedEvent(EventId(), workspaceId.Value, Sequence(), timeProvider.GetUtcNow(), 0), token); }

    public async Task<NotificationDelivery> DeliverNotificationAsync(DeliverNotificationCommand command, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        var deliveryKey = Required(command.DeliveryKey, nameof(command.DeliveryKey), 256);
        var title = Required(command.Title, nameof(command.Title), 200);
        var message = Required(command.Message, nameof(command.Message), 4_000);
        if (command.ActionUrl is { Length: > 2_048 }) throw new WorkValidationException("notification_action_url_too_long", "Notification actionUrl cannot exceed 2048 characters.");
        if (command.ActionUrl is { } actionUrl && (actionUrl.Length == 0 || actionUrl[0] != '/' || actionUrl.StartsWith("//", StringComparison.Ordinal) || actionUrl.Contains('\\')))
            throw new WorkValidationException("notification_action_url_invalid", "Notification actionUrl must be a local absolute path.");
        var id = NotificationId(command.WorkspaceId, deliveryKey);
        var existing = await repository.GetNotificationAsync(command.WorkspaceId, id, token);
        if (existing is not null) return new(existing, true);

        var notification = new WorkNotification
        {
            Id = id,
            WorkspaceId = command.WorkspaceId,
            Kind = WorkNotificationKind.Information,
            Title = title,
            Message = message,
            CreatedAt = timeProvider.GetUtcNow(),
            ActionUrl = command.ActionUrl,
            DeliveryKey = deliveryKey,
            CorrelationId = command.CorrelationId,
            SourceRunId = command.SourceRunId,
            SourceStepId = command.SourceStepId,
            SourceToolCallId = command.SourceToolCallId
        };
        try
        {
            await repository.CreateNotificationAsync(notification, token);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            existing = await repository.GetNotificationAsync(command.WorkspaceId, id, token);
            if (existing is not null) return new(existing, true);
            throw;
        }
        await PublishAsync(new NotificationCreatedEvent(EventId(), command.WorkspaceId.Value, Sequence(), notification.CreatedAt, notification), token);
        await PublishAsync(new UnreadNotificationCountChangedEvent(EventId(), command.WorkspaceId.Value, Sequence(), notification.CreatedAt, await UnreadCountAsync(command.WorkspaceId, token)), token);
        return new(notification, false);
    }

    private async Task CreateNotificationAsync(WorkspaceId workspaceId, WorkNotificationKind kind, string title, string message, InteractionId? interactionId, WorkTaskId? taskId, PendingActionId? actionId, string? url, CancellationToken token)
    {
        var notification = new WorkNotification { Id = WorkNotificationId.New(), WorkspaceId = workspaceId, Kind = kind, Title = title, Message = message, CreatedAt = timeProvider.GetUtcNow(), InteractionId = interactionId, WorkTaskId = taskId, PendingActionId = actionId, ActionUrl = url }; await repository.CreateNotificationAsync(notification, token); await PublishAsync(new NotificationCreatedEvent(EventId(), workspaceId.Value, Sequence(), notification.CreatedAt, notification), token); await PublishAsync(new UnreadNotificationCountChangedEvent(EventId(), workspaceId.Value, Sequence(), notification.CreatedAt, await UnreadCountAsync(workspaceId, token)), token);
    }

    private async Task PublishAsync(WorkplaceEventContract value, CancellationToken token) { foreach (var sink in eventSinks) await sink.PublishAsync(value, token); }

    private long Sequence() => Interlocked.Increment(ref eventSequence);

    private static string EventId() => Guid.NewGuid().ToString("N");

    private static string Required(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new WorkValidationException("notification_field_required", $"Notification {name} is required.");
        value = value.Trim();
        if (value.Length > maximumLength) throw new WorkValidationException("notification_field_too_long", $"Notification {name} cannot exceed {maximumLength} characters.");
        return value;
    }

    private static WorkNotificationId NotificationId(WorkspaceId workspaceId, string deliveryKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceId.Value:D}\n{deliveryKey}"));
        return new(new Guid(hash.AsSpan(0, 16)));
    }
}

