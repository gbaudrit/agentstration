using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class ResourceScopePolicyException(string message) : Exception(message);

public static class ResourceScopePolicy
{
    private static readonly IReadOnlySet<ResourceScopeKind> InstanceTenantWorkspace =
        new HashSet<ResourceScopeKind>([ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace]);
    private static readonly IReadOnlySet<ResourceScopeKind> TenantOnly =
        new HashSet<ResourceScopeKind>([ResourceScopeKind.Tenant]);
    private static readonly IReadOnlySet<ResourceScopeKind> WorkspaceOnly =
        new HashSet<ResourceScopeKind>([ResourceScopeKind.Workspace]);
    public static IReadOnlySet<ResourceScopeKind> AllowedScopes(string kind) => kind switch
    {
        ResourceKinds.ModelProvider or ResourceKinds.ModelProfile or ResourceKinds.RuntimeProfile => TenantOnly,
        ResourceKinds.Vault or ResourceKinds.Secret => InstanceTenantWorkspace,
        ResourceKinds.ToolProvider or ResourceKinds.Tool or ResourceKinds.ToolExecutionHook => WorkspaceOnly,
        ResourceKinds.Agent or ResourceKinds.AgentRevision or ResourceKinds.AgentDeployment or ResourceKinds.Trigger => WorkspaceOnly,
        ResourceKinds.InstalledPack => InstanceTenantWorkspace,
        PackAuthoringKinds.PackProject or PackAuthoringKinds.PackProjectBuild => WorkspaceOnly,
        ResourceKinds.ExtensionRegistration => new HashSet<ResourceScopeKind>([ResourceScopeKind.Instance, ResourceScopeKind.Tenant]),
        _ => WorkspaceOnly
    };

    public static void EnsureAllowed(Resource resource, ResourceScopeRef scopeRef)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureAllowed(resource.Kind, scopeRef.Kind);
        if (resource is ExtensionRegistrationResource registration)
        {
            var expected = registration.Definition.Source == ExtensionRegistrationSource.Manual
                ? ResourceScopeKind.Tenant
                : ResourceScopeKind.Instance;
            if (scopeRef.Kind != expected)
                throw new ResourceScopePolicyException(
                    $"Extension registration source '{registration.Definition.Source}' requires a {expected.ToString().ToLowerInvariant()} scope.");
        }
    }

    public static void EnsureAllowed(string kind, ResourceScopeKind scopeKind)
    {
        if (!AllowedScopes(kind).Contains(scopeKind))
            throw new ResourceScopePolicyException(
                $"Resource kind '{kind}' cannot be owned by a {scopeKind.ToString().ToLowerInvariant()} scope.");
    }
}
