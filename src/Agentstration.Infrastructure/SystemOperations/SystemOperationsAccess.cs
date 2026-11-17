using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations;
using Agentstration.SystemOperations.Contracts;

namespace Agentstration.Infrastructure.SystemOperations;

public sealed class SystemOperationsAccess(ICurrentRequestContext requests, IIdentityStore identities,
    ISystemGraphAuthorization graphAuthorization, ISystemGraphReadScope metadataScope,
    IRuntimeRunExecutionScope runtimeScopes, IFlowRunExecutionScope flowScopes,
    IPlatformAuthorizationService platform) : ISystemOperationsAccess
{
    public async Task<SystemOperationsReadContext> GetAsync(CancellationToken ct)
    {
        var authorized = await graphAuthorization.GetContextAsync(ct);
        if (requests.ActorPrincipalId is not { } actor || (await identities.GetPrincipalAsync(actor, ct))?.Status != PrincipalStatus.Active)
            throw new SystemOperationsException("system_operations_forbidden");
        var current = requests.Current;
        var logical = requests.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.TenantId),
            ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(current.WorkspaceId),
            _ => throw new SystemOperationsException("system_operations_forbidden")
        };
        // Preserve the current family's stored physical scope and explicit logical invocation
        // coordinate. Never substitute the selected definition's owner or infer a name join.
        var data = current.WorkspaceId == Guid.Empty ? logical : ResourceScopeRef.Workspace(current.WorkspaceId);
        var runtime = RuntimeScope(actor, current.TenantId, current.WorkspaceId, logical, data);
        var canRead = true;
        try { await runtimeScopes.ValidateReadAsync(runtime, ct); }
        catch (RuntimeRunValidationException) { canRead = false; }
        var canReadWork = canRead;
        if (canReadWork)
        {
            try { await flowScopes.ValidateAsync(FlowScope(actor, current.TenantId, current.WorkspaceId, logical, data), ct); }
            catch (FlowValidationException) { canReadWork = false; }
        }
        var workers = await platform.IsPlatformAdministratorAsync(actor, ct);
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { authorized.AuthorizationKey, Actor = actor, Logical = logical, Data = data, Read = canRead, Work = canReadWork, Workers = workers })));
        return new(actor, logical, data, current.TenantId, current.WorkspaceId, canRead, canReadWork, workers, key);
    }
    public async Task<bool> CanDeliverAsync(SystemGraphIdentity identity, SystemOperationsReadContext expected, bool inspectBindings, CancellationToken ct)
    {
        var current = await GetAsync(ct);
        if (current.AuthorizationKey != expected.AuthorizationKey) return false;
        var graphContext = await graphAuthorization.GetContextAsync(ct);
        using var trusted = metadataScope.Enter();
        var access = await graphAuthorization.EvaluateAsync(identity, graphContext, ct);
        return access.Discover && (!inspectBindings || access.InspectBindings);
    }
    internal static RuntimeRunScope RuntimeScope(SystemOperationsReadContext context) => RuntimeScope(context.ActorId, context.TenantId, context.WorkspaceId, context.InvocationScope, context.DataScope);
    private static RuntimeRunScope RuntimeScope(Guid actor, Guid tenant, Guid workspace, ResourceScopeRef logical, ResourceScopeRef data) =>
        data.Kind == ResourceScopeKind.Workspace ? RuntimeRunScope.Workspace(tenant, new(workspace), actor) with { LogicalScopeRef = logical == data ? null : logical }
        : data.Kind == ResourceScopeKind.Tenant ? RuntimeRunScope.Tenant(tenant, actor) : RuntimeRunScope.Instance(actor);
    internal static FlowRunScope FlowScope(SystemOperationsReadContext context) => FlowScope(context.ActorId, context.TenantId, context.WorkspaceId, context.InvocationScope, context.DataScope);
    private static FlowRunScope FlowScope(Guid actor, Guid tenant, Guid workspace, ResourceScopeRef logical, ResourceScopeRef data) =>
        data.Kind == ResourceScopeKind.Workspace ? FlowRunScope.Workspace(tenant, new(workspace), actor) with { LogicalScopeRef = logical == data ? null : logical }
        : data.Kind == ResourceScopeKind.Tenant ? FlowRunScope.Tenant(tenant, actor) : FlowRunScope.Instance(actor);
}
