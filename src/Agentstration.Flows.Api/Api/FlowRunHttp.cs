using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Web;

internal static class FlowRunHttp
{
    public static FlowRunScope CurrentScope(
        ICurrentRequestContext context,
        ResourceScopeRef? requestedScopeRef = null)
    {
        var current = context.Current;
        var logicalCurrent = context.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.TenantAdministration =>
                ResourceScopeRef.Tenant(current.TenantId),
            _ => ResourceScopeRef.Workspace(current.WorkspaceId)
        };
        var physicalCurrent = current.WorkspaceId == Guid.Empty
            ? logicalCurrent
            : ResourceScopeRef.Workspace(current.WorkspaceId);
        var requested = requestedScopeRef ?? logicalCurrent;
        if (requested != logicalCurrent && requested != physicalCurrent)
            throw new FlowValidationException("flow_invocation_scope_mismatch",
                "The requested Flow execution scope is not the current Instance, Tenant, or Workspace context.");

        if (current.WorkspaceId != Guid.Empty)
            return FlowRunScope.Workspace(current.TenantId, new WorkspaceId(current.WorkspaceId), current.PrincipalId) with
            {
                LogicalScopeRef = logicalCurrent == physicalCurrent ? null : logicalCurrent
            };

        return requested.Kind switch
        {
            ResourceScopeKind.Instance => FlowRunScope.Instance(current.PrincipalId),
            ResourceScopeKind.Tenant => FlowRunScope.Tenant(current.TenantId, current.PrincipalId),
            _ => throw new FlowValidationException("flow_invocation_scope_mismatch",
                "The requested Flow execution scope is not the current Instance, Tenant, or Workspace context.")
        };
    }
}
