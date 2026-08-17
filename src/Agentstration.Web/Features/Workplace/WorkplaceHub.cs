using Agentstration.Application.Work;
using Agentstration.Management.Abstractions;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Agentstration.Web.Features.Workplace;

public sealed class WorkplaceHub(ICurrentRequestContext requestContext, IIdentityStore identityStore) : Hub
{
    public async Task SubscribeAsync(string workspaceId, long afterSequence)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 512 || afterSequence < 0) throw new HubException("A valid Workspace identifier and sequence are required.");
        var canonicalId = await ResolveCurrentWorkspaceAsync(workspaceId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(canonicalId), Context.ConnectionAborted);
    }
    public async Task UnsubscribeAsync(string workspaceId)
    {
        var canonicalId = await ResolveCurrentWorkspaceAsync(workspaceId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(canonicalId), Context.ConnectionAborted);
    }
    private async Task<string> ResolveCurrentWorkspaceAsync(string value)
    {
        var current = requestContext.Current;
        var workspace = await identityStore.GetWorkspaceAsync(current.TenantId, current.WorkspaceId, Context.ConnectionAborted);
        if (workspace is null ||
            (!string.Equals(value, workspace.Name, StringComparison.OrdinalIgnoreCase) &&
             (!Guid.TryParse(value, out var id) || id != workspace.Id)))
            throw new HubException("The requested Workspace is not available to the current context.");
        return workspace.Id.ToString("D");
    }
    internal static string Group(string workspaceId) => $"workspace:{workspaceId}";
}

public sealed class SignalRWorkplaceEventSink(IHubContext<WorkplaceHub> hub) : IWorkplaceEventSink
{
    public Task PublishAsync(WorkplaceEventContract workplaceEvent, CancellationToken cancellationToken) =>
        hub.Clients.Group(WorkplaceHub.Group(workplaceEvent.WorkspaceId.ToString("D"))).SendAsync(EventName(workplaceEvent), workplaceEvent, cancellationToken);

    private static string EventName(WorkplaceEventContract value) => value switch
    {
        InteractionUpdatedEvent => "InteractionUpdated",
        MessageAddedEvent => "MessageAdded",
        PendingActionCreatedEvent => "PendingActionCreated",
        PendingActionResolvedEvent => "PendingActionResolved",
        TaskCreatedEvent => "TaskCreated",
        FlowRunStartedEvent => "FlowRunStarted",
        FlowRunCompletedEvent => "FlowRunCompleted",
        TaskStatusChangedEvent => "TaskStatusChanged",
        TaskActivityAddedEvent => "TaskActivityAdded",
        TaskResultAddedEvent => "TaskResultAdded",
        TaskArtifactAddedEvent => "TaskArtifactAdded",
        NotificationCreatedEvent => "NotificationCreated",
        NotificationUpdatedEvent => "NotificationUpdated",
        UnreadNotificationCountChangedEvent => "UnreadNotificationCountChanged",
        _ => throw new NotSupportedException($"Workplace event '{value.GetType().Name}' is not supported.")
    };
}
