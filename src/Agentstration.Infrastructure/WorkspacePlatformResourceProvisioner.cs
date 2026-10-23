using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Infrastructure.Notifications;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Profiles;

namespace Agentstration.Infrastructure;

public interface IWorkspacePlatformResourceProvisioner
{
    Task EnsureAllAsync(CancellationToken cancellationToken);
    Task EnsureAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken);
}

internal sealed class WorkspacePlatformResourceProvisioner(
    IIdentityStore identities,
    IRequestContextScopeFactory scopes,
    IResourceStore resources,
    IResourceGovernanceContext governance,
    IResourceAccessPolicyService accessPolicies,
    InternalMcpToolProjectionService internalTools,
    ArtifactPlatformResourceProvisioner artifacts,
    KnowledgePlatformResourceProvisioner knowledge) : IWorkspacePlatformResourceProvisioner, IWorkspaceProvisioner
{
    public async Task EnsureAllAsync(CancellationToken cancellationToken)
    {
        var tenants = await identities.ListTenantsAsync(cancellationToken);
        foreach (var tenant in tenants.Where(value => value.Status == TenantStatus.Active))
        {
            var workspaces = await identities.ListWorkspacesAsync(tenant.Id, cancellationToken);
            foreach (var workspace in workspaces.Where(value => value.Status is WorkspaceStatus.Initializing or WorkspaceStatus.Active))
                await EnsureAsync(tenant.Id, workspace.Id, cancellationToken);
        }
    }

    public async Task EnsureAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken)
    {
        using var systemScope = scopes.PushSystem();
        var runtimeProfile = await EnsureStandardRuntimeProfileAsync(cancellationToken);
        await EnsureWorkspaceAccessAsync(runtimeProfile.Value, ResourceScopeRef.Workspace(workspaceId), cancellationToken);
        await internalTools.EnsureAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, cancellationToken);
        await artifacts.EnsureAsync(ResourceScopeRef.Instance, cancellationToken);
        await internalTools.EnsureAsync(
            ResourceScopeRef.Workspace(workspaceId),
            ResourceNamespace.Default,
            cancellationToken);
        await artifacts.EnsureAsync(ResourceScopeRef.Workspace(workspaceId), cancellationToken);
        await knowledge.EnsureAsync(ResourceScopeRef.Tenant(tenantId),
            ResourceScopeRef.Workspace(workspaceId), cancellationToken);
        var workspace = await identities.GetWorkspaceAsync(tenantId, workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace '{workspaceId:D}' no longer exists.");
        if (workspace.Status == WorkspaceStatus.Initializing)
            await identities.UpdateWorkspaceAsync(workspace with { Status = WorkspaceStatus.Active }, cancellationToken);
    }

    public Task ProvisionAsync(Workspace workspace, CancellationToken cancellationToken) =>
        EnsureAsync(workspace.TenantId, workspace.Id, cancellationToken);

    private async Task<StoredResource<RuntimeProfileResource>> EnsureStandardRuntimeProfileAsync(CancellationToken cancellationToken)
    {
        using var governanceScope = governance.Push(ResourceGovernance.Core("runtime.standard-profile"));
        var instanceScope = ResourceScopeRef.Instance;
        var address = ScopedResourceAddress.Create(
            instanceScope,
            ResourceNamespace.Default,
            RuntimeProfileResourceKinds.RuntimeProfile,
            "maf-builtin");
        if (await resources.GetExactAsync<RuntimeProfileResource>(address, cancellationToken) is { } existing) return existing;
        var profile = new RuntimeProfileResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = RuntimeProfileResourceKinds.RuntimeProfile,
            Metadata = new ResourceMetadata
            {
                Name = "maf-builtin",
                Annotations = new Dictionary<string, string>
                {
                    [ResourceProvenanceAnnotations.BuiltIn] = "true"
                }
            },
            ScopeRef = instanceScope,
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new RuntimeProfileProperties
            {
                DisplayName = "Microsoft Agent Framework · Built-in",
                RuntimeType = "microsoft-agent-framework",
                Execution = new RuntimeExecutionDefaults
                {
                    SessionMode = RuntimeSessionMode.Transient,
                    ToolInvocation = RuntimeToolInvocationMode.Automatic,
                    Streaming = StreamingMode.Automatic
                }
            }
        };
        try
        {
            return await resources.PutExactAsync(instanceScope, profile, null, true, cancellationToken);
        }
        catch (ResourceConcurrencyException)
        {
            var recovered = await resources.GetExactAsync<RuntimeProfileResource>(address, cancellationToken);
            if (recovered is null) throw;
            return recovered;
        }
    }

    private async Task EnsureWorkspaceAccessAsync(
        Resource resource,
        ResourceScopeRef workspaceScope,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await accessPolicies.GetAsync(resource.Uid, cancellationToken);
            if (current?.Policy.Grants.Any(grant => grant.TargetScopeRef == workspaceScope
                    && grant.Actions.Contains(ResourceAccessAction.Discover)
                    && grant.Actions.Contains(ResourceAccessAction.Use)) == true)
                return;
            var grants = (current?.Policy.Grants ?? [])
                .Where(grant => grant.TargetScopeRef != workspaceScope)
                .Append(new ResourceAccessGrant(workspaceScope,
                    [ResourceAccessAction.Discover, ResourceAccessAction.Use]))
                .ToArray();
            try
            {
                _ = await accessPolicies.PutAsync(new ResourceAccessPolicy(
                    new(resource.Uid, resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Generation),
                    grants), current?.ETag, cancellationToken);
                return;
            }
            catch (ResourceConcurrencyException) when (attempt < 2)
            {
            }
        }
    }
}
