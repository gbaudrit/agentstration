using Agentstration.Management.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class ResourceScopeOperationService(
    ICurrentRequestContext requestContext,
    IRequestContextScopeFactory scopeFactory,
    IAuthorizationService authorization,
    IPlatformAuthorizationService platformAuthorization,
    IIdentityStore identities,
    IResourceScopeResolver scopes) : IResourceScopeOperations
{
    public ResourceScopeRef DefaultScopeRef(string kind)
    {
        var allowed = ResourceScopePolicy.AllowedScopes(kind);
        if (requestContext.AccessMode == ControlPlaneAccessMode.Tenant)
        {
            ResourceScopePolicy.EnsureAllowed(kind, ResourceScopeKind.Tenant);
            return ResourceScopeRef.Tenant(requestContext.Current.TenantId);
        }
        if (requestContext.AccessMode == ControlPlaneAccessMode.System)
        {
            ResourceScopePolicy.EnsureAllowed(kind, ResourceScopeKind.Instance);
            return ResourceScopeRef.Instance;
        }
        var current = RequireInteractiveContext();
        if (allowed.SetEquals([ResourceScopeKind.Tenant])) return ResourceScopeRef.Tenant(current.TenantId);
        if (allowed.Contains(ResourceScopeKind.Workspace)) return ResourceScopeRef.Workspace(current.WorkspaceId);
        if (allowed.Contains(ResourceScopeKind.Tenant)) return ResourceScopeRef.Tenant(current.TenantId);
        return ResourceScopeRef.Instance;
    }

    public ResourceScopeRef TargetScopeRef(ResourceScopeKind kind)
    {
        if (kind == ResourceScopeKind.Instance) return ResourceScopeRef.Instance;
        if (requestContext.AccessMode == ControlPlaneAccessMode.Tenant)
        {
            if (kind == ResourceScopeKind.Tenant)
                return ResourceScopeRef.Tenant(requestContext.Current.TenantId);
            throw new ResourceScopePolicyException($"A tenant Control Plane context cannot target a {kind.ToString().ToLowerInvariant()} scope.");
        }
        if (requestContext.AccessMode == ControlPlaneAccessMode.System)
            throw new ResourceScopePolicyException($"A system Control Plane context cannot infer a {kind.ToString().ToLowerInvariant()} scope.");
        var current = RequireInteractiveContext();
        return kind switch
        {
            ResourceScopeKind.Tenant => ResourceScopeRef.Tenant(current.TenantId),
            ResourceScopeKind.Workspace => ResourceScopeRef.Workspace(current.WorkspaceId),
            _ => throw new ResourceScopePolicyException($"Resource scope kind '{kind}' is not supported.")
        };
    }

    public async Task<IReadOnlyList<ResourceScopeTarget>> ListTargetsAsync(
        string kind,
        string permission,
        CancellationToken cancellationToken)
    {
        var current = RequireInteractiveContext();
        var allowed = ResourceScopePolicy.AllowedScopes(kind);
        var canWriteTenantOrWorkspace = current.Restriction is null
            && await authorization.HasPermissionAsync(current, permission, cancellationToken);
        var isPlatformAdministrator = current.Restriction is null
            && await platformAuthorization.IsPlatformAdministratorAsync(current.PrincipalId, cancellationToken);
        var targets = new List<ResourceScopeTarget>();

        if (allowed.Contains(ResourceScopeKind.Instance))
            targets.Add(new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", isPlatformAdministrator));
        if (allowed.Contains(ResourceScopeKind.Tenant))
        {
            var tenant = await identities.GetTenantAsync(current.TenantId, cancellationToken);
            if (tenant is not null)
                targets.Add(new(ResourceScopeRef.Tenant(tenant.Id), ResourceScopeKind.Tenant, tenant.DisplayName, canWriteTenantOrWorkspace));
        }
        if (allowed.Contains(ResourceScopeKind.Workspace))
        {
            var workspace = await identities.GetWorkspaceAsync(current.TenantId, current.WorkspaceId, cancellationToken);
            if (workspace is not null)
                targets.Add(new(ResourceScopeRef.Workspace(workspace.Id), ResourceScopeKind.Workspace, workspace.DisplayName, canWriteTenantOrWorkspace));
        }
        return targets;
    }

    public async Task<T> WriteAsync<T>(
        Resource resource,
        ResourceScopeRef scopeRef,
        string permission,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ResourceScopePolicy.EnsureAllowed(resource, scopeRef);
        _ = await scopes.ResolveAsync(scopeRef, cancellationToken)
            ?? throw new ResourceScopePolicyException($"Resource scope '{scopeRef}' does not exist.");
        return await WriteCoreAsync(scopeRef, permission, operation, cancellationToken);
    }

    public async Task<T> WriteAsync<T>(
        string kind,
        ResourceScopeRef scopeRef,
        string permission,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ResourceScopePolicy.EnsureAllowed(kind, scopeRef.Kind);
        _ = await scopes.ResolveAsync(scopeRef, cancellationToken)
            ?? throw new ResourceScopePolicyException($"Resource scope '{scopeRef}' does not exist.");
        return await WriteCoreAsync(scopeRef, permission, operation, cancellationToken);
    }

    private async Task<T> WriteCoreAsync<T>(
        ResourceScopeRef scopeRef,
        string permission,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (requestContext.AccessMode == ControlPlaneAccessMode.System)
            return await operation(cancellationToken);
        if (requestContext.AccessMode == ControlPlaneAccessMode.Tenant
            && scopeRef == ResourceScopeRef.Tenant(requestContext.Current.TenantId))
            return await operation(cancellationToken);

        var current = RequireInteractiveContext();
        if (current.Restriction is not null) throw new ResourceScopeAccessDeniedException(scopeRef);
        if (scopeRef == ResourceScopeRef.Workspace(current.WorkspaceId))
        {
            await authorization.EnsurePermissionAsync(current, permission, cancellationToken);
            return await operation(cancellationToken);
        }
        if (scopeRef == ResourceScopeRef.Tenant(current.TenantId))
        {
            await authorization.EnsurePermissionAsync(current, permission, cancellationToken);
            using var target = scopeFactory.PushTenant(current.PrincipalId, current.TenantId);
            return await operation(cancellationToken);
        }
        if (scopeRef == ResourceScopeRef.Instance
            && await platformAuthorization.IsPlatformAdministratorAsync(current.PrincipalId, cancellationToken))
        {
            using var target = scopeFactory.PushSystem();
            return await operation(cancellationToken);
        }
        throw new ResourceScopeAccessDeniedException(scopeRef);
    }

    private RequestContext RequireInteractiveContext()
    {
        if (requestContext.AccessMode != ControlPlaneAccessMode.Workspace)
            throw new InvalidOperationException("An interactive workspace context is required.");
        return requestContext.Current;
    }
}
