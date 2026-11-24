using Agentstration.Application.Work;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed class WorkProjectBindingResolver(IResourceIdentityCatalog resources, IResourceAccessPolicyService access,
    DescendantResourceUseAuthorizer ancestry, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
    Agentstration.Identity.Contracts.IAuthorizationService authorization) : IWorkProjectBindingResolver
{
    public async Task<Result<WorkProjectResolvedBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, CancellationToken token)
    {
        if (current.AccessMode != ControlPlaneAccessMode.Workspace) return Failure("work_project_binding_denied");
        var caller = current.Current;
        if (caller.WorkspaceId != workspaceId.Value || caller.PrincipalId != principalId)
            return Failure("work_project_binding_denied");
        if (!WorkProjectContextRules.Valid([binding])) return Failure("work_project_bindings_invalid");
        if (!await authorization.HasPermissionAsync(caller, AuthorizationPermissions.RunsRead, token)) return Failure("work_project_binding_denied");
        var target = binding.Target;
        var consumer = ResourceScopeRef.Workspace(workspaceId.Value);
        if (!await ancestry.IsSameOrDescendantAsync(target.Address.ScopeRef, consumer, token)) return Failure("work_project_binding_unavailable");
        // This reads identity metadata only, after checking the consumer and ownership tree. No configuration or Secret value is resolved.
        using var metadata = scopes.PushSystem();
        var identity = await resources.GetIdentityByUidAsync(target.Uid, token);
        if (identity is null || identity.ScopeRef != target.Address.ScopeRef || identity.Namespace != target.Address.Namespace
            || identity.Kind != target.Address.Kind || identity.Name != target.Address.Name) return Failure("work_project_binding_unavailable");
        var selected = new ResourceAccessIdentity(identity.Uid, identity.ScopeRef, identity.Namespace, identity.Kind, identity.Generation, identity.Version);
        var discover = await access.EvaluateAsync(selected, consumer, ResourceAccessAction.Discover, token);
        var use = await access.EvaluateAsync(selected, consumer, ResourceAccessAction.Use, token);
        if (!discover.Allowed) return Failure("work_project_binding_unavailable");
        if (!use.Allowed) return Failure("work_project_binding_denied");
        if (identity.Generation != target.Generation || identity.Version != target.Version) return Failure("work_project_binding_stale");
        return Result<WorkProjectResolvedBinding>.Success(new(binding, identity.ETag, discover.ReasonCode, use.ReasonCode));
    }

    private static Result<WorkProjectResolvedBinding> Failure(string code) => Result<WorkProjectResolvedBinding>.Failure(code);
}
