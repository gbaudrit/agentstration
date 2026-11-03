using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.DataSources;

// Definitions are authorized in the selected Console scope; operational records stay in its Workspace.
internal sealed class DataSourceExecutionContext(
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory requestScopes)
{
    public RequestContext RequireContext(string errorCode)
    {
        if (requestContext.AccessMode is ControlPlaneAccessMode.Workspace
            or ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration)
        {
            var context = requestContext.Current;
            if (context.WorkspaceId != Guid.Empty && context.TenantId != Guid.Empty)
                return context;
        }
        throw new DataSourceAcquisitionException(errorCode,
            "The selected context has no governed operational Workspace.");
    }

    public ResourceScopeRef ConsumerScope => requestContext.AccessMode switch
    {
        ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
        ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(requestContext.Current.TenantId),
        ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(requestContext.Current.WorkspaceId),
        _ => throw new DataSourceAcquisitionException("data_source_context_required",
            "An explicit Data Source execution context is required.")
    };

    public void EnsureVisible(ResourceScopeRef scope)
    {
        var consumer = ConsumerScope;
        if (scope == consumer || scope.Kind == ResourceScopeKind.Instance) return;
        if (consumer.Kind == ResourceScopeKind.Workspace && scope == ResourceScopeRef.Tenant(requestContext.Current.TenantId)) return;
        throw new ResourceScopeAccessDeniedException(scope);
    }

    public async Task<T> InWorkspaceAsync<T>(Func<Task<T>> operation)
    {
        // Recovery workers already enter the trusted system scope explicitly.
        if (requestContext.AccessMode == ControlPlaneAccessMode.System) return await operation();
        using var workspace = requestScopes.Push(RequireContext("data_source_context_required"));
        return await operation();
    }
}
