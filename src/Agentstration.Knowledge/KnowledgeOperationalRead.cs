using Agentstration.Identity.Contracts;
using Agentstration.Resources;

namespace Agentstration.Knowledge;

internal static class KnowledgeOperationalRead
{
    public static ResourceScopeRef? AdministrativeScope(ICurrentRequestContext current) =>
        current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.Current.TenantId),
            _ => null
        };

    public static RequestContext? AdministrativeContext(ICurrentRequestContext current) =>
        current.AccessMode is ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration
        && current.Current is { } context && context.WorkspaceId != Guid.Empty
        && context.TenantId != Guid.Empty && context.PrincipalId != Guid.Empty ? context : null;
}
