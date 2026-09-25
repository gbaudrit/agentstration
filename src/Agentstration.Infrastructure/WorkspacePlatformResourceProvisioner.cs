using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Notifications;
using Agentstration.ResourceManagement;
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
    InternalMcpToolProjectionService internalTools) : IWorkspacePlatformResourceProvisioner, IWorkspaceProvisioner
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
        await EnsureStandardRuntimeProfileAsync(ResourceScopeRef.Tenant(tenantId), cancellationToken);
        await internalTools.EnsureAsync(
            ResourceScopeRef.Workspace(workspaceId),
            ResourceNamespace.Default,
            cancellationToken);
        var workspace = await identities.GetWorkspaceAsync(tenantId, workspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace '{workspaceId:D}' no longer exists.");
        if (workspace.Status == WorkspaceStatus.Initializing)
            await identities.UpdateWorkspaceAsync(workspace with { Status = WorkspaceStatus.Active }, cancellationToken);
    }

    public Task ProvisionAsync(Workspace workspace, CancellationToken cancellationToken) =>
        EnsureAsync(workspace.TenantId, workspace.Id, cancellationToken);

    private async Task EnsureStandardRuntimeProfileAsync(ResourceScopeRef tenantScope, CancellationToken cancellationToken)
    {
        var address = ScopedResourceAddress.Create(
            tenantScope,
            ResourceNamespace.Default,
            RuntimeProfileResourceKinds.RuntimeProfile,
            "maf-builtin");
        if (await resources.GetExactAsync<RuntimeProfileResource>(address, cancellationToken) is not null) return;
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
            ScopeRef = tenantScope,
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
            await resources.PutExactAsync(tenantScope, profile, null, true, cancellationToken);
        }
        catch (ResourceConcurrencyException)
        {
            if (await resources.GetExactAsync<RuntimeProfileResource>(address, cancellationToken) is null)
                throw;
        }
    }
}
