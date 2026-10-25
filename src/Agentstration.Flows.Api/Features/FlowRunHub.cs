using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Microsoft.AspNetCore.SignalR;

namespace Agentstration.Web.Features.Flows;

public sealed class FlowRunHub(ICurrentRequestContext requestContext, FlowRunService runs, IFlowRunExecutionScope executionScopes) : Hub
{
    public async Task SubscribeAsync(string runId, string? scopeRef = null)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 160)
            throw new HubException("A valid Flow Run identifier is required.");
        var scope = CurrentScope(scopeRef);
        await executionScopes.ValidateAsync(scope, Context.ConnectionAborted);
        if (await runs.GetAsync(runId, scope, Context.ConnectionAborted) is null)
            throw new HubException("The Flow Run was not found in the requested execution scope.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(scope.ScopeRef, runId), Context.ConnectionAborted);
    }

    public Task UnsubscribeAsync(string runId, string? scopeRef = null) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(CurrentScope(scopeRef).ScopeRef, runId), Context.ConnectionAborted);

    private FlowRunScope CurrentScope(string? scopeRef)
    {
        try
        {
            ResourceScopeRef? requested = string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef);
            return FlowRunHttp.CurrentScope(requestContext, requested);
        }
        catch (FlowValidationException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    internal static string Group(ResourceScopeRef scopeRef, string runId) => $"scope:{scopeRef.Value}:flow-run:{runId}";
    internal static string Group(WorkspaceId workspaceId, string runId) => Group(ResourceScopeRef.Workspace(workspaceId.Value), runId);
}

public sealed class SignalRFlowRunEventSink(IHubContext<FlowRunHub> hub) : IFlowRunEventSink
{
    public Task PublishAsync(FlowRunEvent runEvent, CancellationToken cancellationToken) =>
        hub.Clients.Group(FlowRunHub.Group(runEvent.ScopeRef, runEvent.RunId)).SendAsync("FlowRunEvent", runEvent, cancellationToken);
}
