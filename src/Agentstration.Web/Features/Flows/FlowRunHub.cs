using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Microsoft.AspNetCore.SignalR;

namespace Agentstration.Web.Features.Flows;

public sealed class FlowRunHub(ICurrentRequestContext requestContext, FlowRunService runs) : Hub
{
    public async Task SubscribeAsync(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 160)
            throw new HubException("A valid Flow Run identifier is required.");
        var workspaceId = CurrentWorkspace();
        if (await runs.GetAsync(workspaceId, runId, Context.ConnectionAborted) is null)
            throw new HubException("The Flow Run was not found in the current Workspace.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(workspaceId, runId), Context.ConnectionAborted);
    }

    public Task UnsubscribeAsync(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(CurrentWorkspace(), runId), Context.ConnectionAborted);
    private WorkspaceId CurrentWorkspace() => new(requestContext.Current.WorkspaceId);
    internal static string Group(WorkspaceId workspaceId, string runId) => $"workspace:{workspaceId}:flow-run:{runId}";
}

public sealed class SignalRFlowRunEventSink(IHubContext<FlowRunHub> hub) : IFlowRunEventSink
{
    public Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken) =>
        hub.Clients.Group(FlowRunHub.Group(runEvent.WorkspaceId, runEvent.RunId)).SendAsync("FlowRunEvent", runEvent, cancellationToken);
}
