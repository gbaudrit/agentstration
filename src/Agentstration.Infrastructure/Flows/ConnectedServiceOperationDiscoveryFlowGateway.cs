using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Flows;

public sealed class ConnectedServiceOperationDiscoveryFlowGateway(
    FlowService flows,
    FlowRunService runs,
    IRequestContextScopeFactory requestScopes) : IConnectedServiceOperationDiscoveryFlowGateway
{
    public async Task<ConnectedServiceDiscoveryFlowRunSnapshot> StartAsync(
        ConnectedServiceDiscoveryFlowRunRequest request,
        CancellationToken cancellationToken)
    {
        var caller = new RequestContext(request.PrincipalId, request.TenantId, request.WorkspaceId);
        using var selected = request.LogicalScopeRef?.Kind switch
        {
            ResourceScopeKind.Instance => requestScopes.PushInstance(caller),
            ResourceScopeKind.Tenant => requestScopes.PushTenantAdministration(caller),
            _ => requestScopes.Push(caller)
        };
        var target = request.Flow.Flow;
        var scope = target.ScopeRef
            ?? throw Error("connected_service_discovery_flow_scope_required", "The discovery Flow reference must be exact.");
        var flowId = new FlowId(target.Name, target.Namespace ?? request.OwnerNamespace);
        var current = await flows.GetExactAsync(scope, flowId, cancellationToken)
            ?? throw Error("connected_service_discovery_flow_missing", $"Flow '{flowId}' was not found.");
        if (current.Value.Uid != request.Flow.FlowUid)
            throw Error("connected_service_discovery_flow_changed", "The pinned discovery Flow UID no longer matches.");
        var version = await flows.GetVersionExactAsync(scope, flowId, request.Flow.Version, cancellationToken)
            ?? throw Error("connected_service_discovery_flow_version_missing",
                $"Flow version '{flowId}:{request.Flow.Version}' was not found.");
        if (!string.Equals(version.Value.DefinitionHash, request.Flow.DefinitionHash, StringComparison.Ordinal))
            throw Error("connected_service_discovery_flow_changed", "The pinned discovery Flow definition hash no longer matches.");
        if (!version.Value.Metadata.TryGetValue(FlowMetadataKeys.Contract, out var contract)
            || !string.Equals(contract, request.Flow.Contract, StringComparison.Ordinal))
            throw Error("connected_service_discovery_flow_contract_changed", "The discovery Flow contract no longer matches.");
        try
        {
            var stored = await runs.EnsureRootAsync(new EnsureRootFlowRunCommand(
                request.RunId,
                flowId,
                request.Flow.Version,
                "local",
                FlowRunTrigger.Api,
                FlowInvocationOrigin.Api,
                request.CallerId,
                null,
                request.IdempotencyKey,
                request.CorrelationId,
                request.Input,
                $"connected-service-operation-discovery:{request.RunId}",
                false,
                null,
                null,
                null,
                null,
                new FlowRunScope(request.TenantId, new WorkspaceId(request.WorkspaceId), request.PrincipalId)
                { LogicalScopeRef = request.LogicalScopeRef })
            {
                DefinitionScopeRef = scope
            }, cancellationToken);
            return Snapshot(stored.Value);
        }
        catch (FlowValidationException exception)
        {
            throw Error("connected_service_discovery_flow_rejected", exception.Message);
        }
    }

    public async Task<ConnectedServiceDiscoveryFlowRunSnapshot?> GetAsync(
        Guid workspaceId, string runId, Guid tenantId, Guid principalId,
        CancellationToken cancellationToken)
    {
        var stored = await runs.GetAsync(runId, Scope(workspaceId, tenantId, principalId), cancellationToken);
        return stored is null ? null : Snapshot(stored.Value);
    }

    public async Task<ConnectedServiceDiscoveryFlowRunSnapshot> CancelAsync(
        Guid workspaceId, string runId, Guid tenantId, Guid principalId,
        CancellationToken cancellationToken) => Snapshot((await runs.CancelAsync(
            runId, Scope(workspaceId, tenantId, principalId), cancellationToken)).Value);

    private static FlowRunScope Scope(Guid workspaceId, Guid tenantId, Guid principalId) =>
        new(tenantId, new WorkspaceId(workspaceId), principalId);

    private static ConnectedServiceDiscoveryFlowRunSnapshot Snapshot(FlowRun run) => new(
        run.Id,
        run.Status switch
        {
            FlowRunStatus.Pending => ConnectedServiceOperationDiscoveryState.Pending,
            FlowRunStatus.Running => ConnectedServiceOperationDiscoveryState.Running,
            FlowRunStatus.WaitingForInput => ConnectedServiceOperationDiscoveryState.WaitingForInput,
            FlowRunStatus.WaitingForChild => ConnectedServiceOperationDiscoveryState.WaitingForChild,
            FlowRunStatus.Succeeded => ConnectedServiceOperationDiscoveryState.Succeeded,
            FlowRunStatus.Cancelled => ConnectedServiceOperationDiscoveryState.Cancelled,
            FlowRunStatus.TimedOut => ConnectedServiceOperationDiscoveryState.TimedOut,
            _ => ConnectedServiceOperationDiscoveryState.Failed
        },
        run.Output?.Clone(),
        run.Error?.Code,
        run.Error?.Message,
        run.CompletedAt);

    private static ConnectedServiceOperationDiscoveryException Error(string code, string message) => new(code, message);
}
