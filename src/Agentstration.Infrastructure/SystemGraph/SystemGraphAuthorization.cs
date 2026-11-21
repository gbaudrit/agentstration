using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Infrastructure.SystemGraph;

public sealed class SystemGraphAuthorization(
    ICurrentRequestContext requests,
    ReadableResourceScopeService readableScopes,
    IAuthorizationService permissions,
    IPlatformAuthorizationService platformPermissions,
    IResourceScopeResolver scopes,
    IResourceIdentityCatalog identities,
    IResourceStore resources,
    IResourceAccessPolicyService accessPolicies) : ISystemGraphAuthorization
{
    public async Task<SystemGraphReadContext> GetContextAsync(CancellationToken cancellationToken)
    {
        if (requests.ActorPrincipalId is not { } actor || requests.AccessMode is ControlPlaneAccessMode.System or ControlPlaneAccessMode.Unavailable)
            throw new SystemGraphException("system_graph_forbidden");
        if (requests.AccessMode == ControlPlaneAccessMode.Instance)
        {
            if (!await platformPermissions.IsPlatformAdministratorAsync(actor, cancellationToken))
                throw new SystemGraphException("system_graph_forbidden");
        }
        else
        {
            var current = requests.Current;
            if (requests.AccessMode == ControlPlaneAccessMode.TenantAdministration) current = current with { WorkspaceId = Guid.Empty };
            if (!await permissions.HasPermissionAsync(current, AuthorizationPermissions.ResourcesRead, cancellationToken))
                throw new SystemGraphException("system_graph_forbidden");
        }
        var readable = (await readableScopes.GetAsync(cancellationToken)).Select(value => value.ScopeRef).ToArray();
        var owners = new HashSet<ResourceScopeRef>(readable);
        var hierarchyNodes = new Dictionary<ResourceScopeRef, SystemGraphScope>();
        foreach (var consumer in readable)
        {
            var hierarchy = await scopes.ResolveAsync(consumer, cancellationToken);
            if (hierarchy is null) throw new SystemGraphException("system_graph_changed");
            foreach (var node in new[] { hierarchy.Scope }.Concat(hierarchy.Ancestors))
            {
                owners.Add(node.Ref);
                hierarchyNodes[node.Ref] = new(node.Ref, hierarchy.Ancestors.FirstOrDefault(value => value.Id == node.ParentScopeId)?.Ref);
            }
        }
        var context = requests.AccessMode == ControlPlaneAccessMode.Instance ? null : requests.Current;
        return new(JsonSerializer.Serialize(new { Actor = actor, Mode = requests.AccessMode, Context = context }),
            owners.OrderBy(value => value.Value, StringComparer.Ordinal).ToArray(), readable, hierarchyNodes.Values.ToArray());
    }

    public async Task<SystemGraphAccess> EvaluateAsync(SystemGraphIdentity identity, SystemGraphReadContext context, CancellationToken cancellationToken)
    {
        var current = await identities.GetIdentityByUidAsync(identity.ResourceUid, cancellationToken);
        if (current is null || current.ScopeRef != identity.OwnerScope || current.Namespace != identity.Namespace
            || current.Kind != identity.Kind || current.Name != identity.Name) return new(false, false);
        if (identity.Kind == AgentResourceKinds.AgentRevision)
        {
            var revision = await resources.GetExactAsync<AgentRevision>(ScopedResourceAddress.Create(
                identity.OwnerScope, identity.Namespace, identity.Kind, identity.Name), cancellationToken);
            current = revision is null ? null : await identities.GetIdentityByUidAsync(revision.Value.AgentUid, cancellationToken);
            if (current is null || current.Kind != AgentResourceKinds.Agent || current.ScopeRef != identity.OwnerScope || current.Namespace != identity.Namespace)
                return new(false, false);
        }
        var readable = context.ReadableScopes ?? context.Scopes;
        if (readable.Contains(current.ScopeRef)) return new(true, true);
        var discover = false;
        var use = false;
        foreach (var consumer in readable)
        {
            var resource = new ResourceAccessIdentity(current.Uid, current.ScopeRef, current.Namespace, current.Kind);
            discover |= (await accessPolicies.EvaluateAsync(resource, consumer, ResourceAccessAction.Discover, cancellationToken)).Allowed;
            use |= (await accessPolicies.EvaluateAsync(resource, consumer, ResourceAccessAction.Use, cancellationToken)).Allowed;
        }
        return new(discover, discover && use);
    }
}

// Only the query service enters this scope, after selecting authorized source scopes.
public sealed class SystemGraphReadScope(IRequestContextScopeFactory requests) : ISystemGraphReadScope
{
    public IDisposable Enter() => requests.PushSystem();
}
