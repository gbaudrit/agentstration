using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Tools;

public sealed class ToolAccessService(IResourceOperationalStateStore states,
    IResourceAccessPolicyService policies, ICurrentRequestContext current)
{
    public ResourceScopeRef ConsumerScope => current.AccessMode switch
    {
        ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
        ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.Current.TenantId),
        ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(current.Current.WorkspaceId),
        _ => throw new ResourceAccessPolicyException("consumer_scope_required", "An explicit consumer scope is required.")
    };

    public async Task<bool> IsEnabledAsync(ToolResource tool, CancellationToken cancellationToken) =>
        (await states.GetAsync(Identity(tool), cancellationToken))?.Enabled ?? tool.Definition.Enabled;

    public Task<ResourceAccessDecision> CanUseAsync(ResourceAccessIdentity identity, ResourceScopeRef consumer,
        CancellationToken cancellationToken) => policies.EvaluateAsync(identity, consumer, ResourceAccessAction.Use, cancellationToken);

    public Task<ResourceAccessDecision> CanUseAsync(ToolResource tool, ResourceScopeRef consumer,
        CancellationToken cancellationToken) => policies.EvaluateAsync(Identity(tool), consumer, ResourceAccessAction.Use, cancellationToken);

    public static ResourceAccessIdentity Identity(ToolResource tool) => new(tool.Uid,
        tool.ScopeRef ?? throw new ToolResourceValidationException("The Tool has no ownership scope."),
        tool.Namespace, tool.Kind);
}
