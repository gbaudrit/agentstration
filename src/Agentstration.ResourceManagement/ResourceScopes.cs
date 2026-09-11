using Agentstration.Resources;

namespace Agentstration.ResourceManagement;

public sealed record ResourceScope(
    long Id,
    ResourceScopeRef Ref,
    ResourceScopeKind Kind,
    string TargetKey,
    long? ParentScopeId);

public sealed record ResolvedResourceScope(
    ResourceScope Scope,
    IReadOnlyList<ResourceScope> Ancestors)
{
    public IReadOnlyList<long> VisibleScopeIds => [Scope.Id, .. Ancestors.Select(value => value.Id)];
}

public interface IResourceScopeResolver
{
    Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scopeRef, CancellationToken cancellationToken);
}

public sealed record ResourceScopeTarget(
    ResourceScopeRef ScopeRef,
    ResourceScopeKind Kind,
    string DisplayName,
    bool CanWrite);

public sealed class ResourceScopeAccessDeniedException(ResourceScopeRef scopeRef)
    : Exception($"The current principal cannot administer resource scope '{scopeRef}'.");

public interface IResourceScopeOperations
{
    ResourceScopeRef DefaultScopeRef(string kind);
    ResourceScopeRef TargetScopeRef(ResourceScopeKind kind);
    Task<IReadOnlyList<ResourceScopeTarget>> ListTargetsAsync(string kind, string permission, CancellationToken cancellationToken);
    Task<T> WriteAsync<T>(Resource resource, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
    Task<T> WriteAsync<T>(string kind, ResourceScopeRef scopeRef, string permission, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

public sealed class ResourceScopePolicyException(string message) : Exception(message);

public static class ResourceScopePolicy
{
    private static readonly IReadOnlySet<ResourceScopeKind> InstanceTenantWorkspace =
        new HashSet<ResourceScopeKind>([ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace]);
    private static readonly IReadOnlySet<ResourceScopeKind> InstanceOnly =
        new HashSet<ResourceScopeKind>([ResourceScopeKind.Instance]);
    private static readonly IReadOnlySet<ResourceScopeKind> TenantOnly =
        new HashSet<ResourceScopeKind>([ResourceScopeKind.Tenant]);
    private static readonly IReadOnlySet<ResourceScopeKind> WorkspaceOnly =
        new HashSet<ResourceScopeKind>([ResourceScopeKind.Workspace]);

    public static IReadOnlySet<ResourceScopeKind> AllowedScopes(string kind) => kind switch
    {
        "ModelProvider" or "ModelProfile" or "RuntimeProfile" => TenantOnly,
        "SourceProvider" => InstanceTenantWorkspace,
        "SourceRegistryRegistration" or "SourceRegistryObservedState" or "SourceRegistryRefreshRecord" => InstanceOnly,
        "Source" or "SourceVersion" or "SourceConfiguration" or "SourceObservedState" or "SourceImportRecord"
            or "SourceChannelSnapshot" or "SourceChannelObservedState" or "SourceChannelRefreshRecord" => InstanceTenantWorkspace,
        "Vault" or "Secret" => InstanceTenantWorkspace,
        "ToolProvider" or "Tool" or "ToolDefinition" or "ToolExecutionHook" => WorkspaceOnly,
        "Agent" or "AgentRevision" or "AgentDeployment" or "Trigger" => WorkspaceOnly,
        "InstalledPack" or "ExtensionRegistration" => InstanceTenantWorkspace,
        "PackProject" or "PackProjectBuild" => WorkspaceOnly,
        _ => WorkspaceOnly
    };

    public static void EnsureAllowed(Resource resource, ResourceScopeRef scopeRef)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureAllowed(resource.Kind, scopeRef.Kind);
    }

    public static void EnsureAllowed(string kind, ResourceScopeKind scopeKind)
    {
        if (!AllowedScopes(kind).Contains(scopeKind))
            throw new ResourceScopePolicyException(
                $"Resource kind '{kind}' cannot be owned by a {scopeKind.ToString().ToLowerInvariant()} scope.");
    }
}

public static class ResourceScopeOwnership
{
    public static ResourceScopeRef RequireScope(this Resource resource, ResourceScopeKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.ScopeRef is not { } scopeRef || scopeRef.Kind != expectedKind)
            throw new InvalidOperationException($"Resource '{resource.Address}' must belong to a {expectedKind.ToString().ToLowerInvariant()} scope.");
        return scopeRef;
    }

    public static Guid RequireScopeTargetId(this Resource resource, ResourceScopeKind expectedKind) =>
        resource.RequireScope(expectedKind).TargetId
        ?? throw new InvalidOperationException($"A {expectedKind.ToString().ToLowerInvariant()} scope must have a target ID.");
}
