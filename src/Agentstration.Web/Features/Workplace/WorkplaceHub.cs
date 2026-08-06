using Agentstration.Application.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Agentstration.Web.Features.Workplace;

public sealed class WorkplaceHub : Hub
{
    public Task SubscribeAsync(string workspaceId, long afterSequence)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 512 || afterSequence < 0) throw new HubException("A valid Workspace identifier and sequence are required.");
        return Groups.AddToGroupAsync(Context.ConnectionId, Group(workspaceId), Context.ConnectionAborted);
    }
    public Task UnsubscribeAsync(string workspaceId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(workspaceId), Context.ConnectionAborted);
    internal static string Group(string workspaceId) => $"workspace:{workspaceId}";
}

public sealed class SignalRWorkplaceEventSink(IHubContext<WorkplaceHub> hub) : IWorkplaceEventSink
{
    public Task PublishAsync(WorkplaceEventContract workplaceEvent, CancellationToken cancellationToken) =>
        hub.Clients.Group(WorkplaceHub.Group(workplaceEvent.WorkspaceId)).SendAsync(EventName(workplaceEvent), workplaceEvent, cancellationToken);

    private static string EventName(WorkplaceEventContract value) => value switch
    {
        InteractionUpdatedEvent => "InteractionUpdated", MessageAddedEvent => "MessageAdded",
        PendingActionCreatedEvent => "PendingActionCreated", PendingActionResolvedEvent => "PendingActionResolved",
        TaskCreatedEvent => "TaskCreated", FlowRunStartedEvent => "FlowRunStarted", FlowRunCompletedEvent => "FlowRunCompleted", TaskStatusChangedEvent => "TaskStatusChanged",
        TaskActivityAddedEvent => "TaskActivityAdded", TaskResultAddedEvent => "TaskResultAdded",
        TaskArtifactAddedEvent => "TaskArtifactAdded", NotificationCreatedEvent => "NotificationCreated",
        NotificationUpdatedEvent => "NotificationUpdated", UnreadNotificationCountChangedEvent => "UnreadNotificationCountChanged",
        _ => throw new NotSupportedException($"Workplace event '{value.GetType().Name}' is not supported.")
    };
}
