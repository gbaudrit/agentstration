using Agentstration.Flow;
using Agentstration.Flow.Application;
using Microsoft.AspNetCore.SignalR;

namespace Agentstration.Web.Features.Flows;

public sealed class FlowRunHub : Hub
{
    public Task SubscribeAsync(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 160)
            throw new HubException("A valid Flow Run identifier is required.");
        return Groups.AddToGroupAsync(Context.ConnectionId, Group(runId), Context.ConnectionAborted);
    }

    public Task UnsubscribeAsync(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(runId), Context.ConnectionAborted);
    internal static string Group(string runId) => $"flow-run:{runId}";
}

public sealed class SignalRFlowRunEventSink(IHubContext<FlowRunHub> hub) : IFlowRunEventSink
{
    public Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken) =>
        hub.Clients.Group(FlowRunHub.Group(runEvent.RunId)).SendAsync("FlowRunEvent", runEvent, cancellationToken);
}
