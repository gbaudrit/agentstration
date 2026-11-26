using Agentstration.Application.Work;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed class WorkProjectBindingResolver(IResourceIdentityCatalog resources, IResourceAccessPolicyService access,
    DescendantResourceUseAuthorizer ancestry, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
    Agentstration.Identity.Contracts.IAuthorizationService authorization, WorkProjectContractResolver? contracts = null) : IWorkProjectBindingResolver
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
        var selected = await ResolveReferenceAsync(workspaceId, binding, token);
        if (!selected.IsSuccess || binding.Contract is null) return selected;
        if (!await authorization.HasPermissionAsync(caller, AuthorizationPermissions.RunsExecute, token)) return Failure("work_project_binding_denied");
        if (contracts is null) return Failure("work_project_contract_unavailable");
        foreach (var reference in new[] { binding.Contract.Provider, binding.Contract.Extension })
        {
            var allowed = await ResolveReferenceAsync(workspaceId, binding with { Target = reference, Contract = null }, token);
            if (!allowed.IsSuccess) return Failure(allowed.ErrorCode!);
        }
        var resolved = await contracts.ResolveAsync(binding, caller,
            (reference, cancellation) => ResolveReferenceAsync(workspaceId, binding with { Target = reference, Contract = null }, cancellation), token);
        return resolved.IsSuccess ? Result<WorkProjectResolvedBinding>.Success(selected.Value! with { Contract = resolved.Value }) : Failure(resolved.ErrorCode!);
    }

    private async Task<Result<WorkProjectResolvedBinding>> ResolveReferenceAsync(WorkspaceId workspaceId,
        WorkProjectContextBinding binding, CancellationToken token)
    {
        var target = binding.Target;
        var consumer = ResourceScopeRef.Workspace(workspaceId.Value);
        if (!await ancestry.IsSameOrDescendantAsync(target.Address.ScopeRef, consumer, token)) return Failure("work_project_binding_unavailable");
        // Only this identity/policy observation enters System mode; configuration and remote calls use the caller's context.
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
