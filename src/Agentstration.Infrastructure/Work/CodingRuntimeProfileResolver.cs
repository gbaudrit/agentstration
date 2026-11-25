using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed class CodingRuntimeProfileResolver(IResourceStore resources, ICurrentRequestContext current, IRequestContextScopeFactory scopes,
    IResourceAccessPolicyService access, IAuthorizationService authorization) : ICodingRuntimeProfileResolver
{
    public async Task<Result<CodingRuntimeProfileReference>> ResolveAsync(WorkspaceId workspaceId, CodingRuntimeProfileReference reference, CancellationToken token)
    {
        if (current.AccessMode != ControlPlaneAccessMode.Workspace || current.Current.WorkspaceId != workspaceId.Value || current.Current.PrincipalId == Guid.Empty
            || !await authorization.HasPermissionAsync(current.Current, AuthorizationPermissions.ResourcesRead, token)) return Failure("coding_runtime_profile_denied");
        using var metadata = scopes.PushSystem();
        var resource = (await resources.GetExactAsync<RuntimeProfileResource>(ScopedResourceAddress.Create(ResourceScopeRef.Instance,
            reference.Namespace, RuntimeProfileResourceKinds.RuntimeProfile, reference.Name), token))?.Value;
        if (resource is null || resource.Definition.RuntimeType != CodingExecutionProtocol.RuntimeCapability) return Failure("coding_runtime_profile_unavailable");
        var identity = new ResourceAccessIdentity(resource.Uid, ResourceScopeRef.Instance, resource.Namespace, resource.Kind, resource.Generation, null);
        var consumer = ResourceScopeRef.Workspace(workspaceId.Value);
        if (!(await access.EvaluateAsync(identity, consumer, ResourceAccessAction.Discover, token)).Allowed
            || !(await access.EvaluateAsync(identity, consumer, ResourceAccessAction.Use, token)).Allowed) return Failure("coding_runtime_profile_denied");
        if (reference.Uid is { } uid && uid != resource.Uid || reference.Generation is { } generation && generation != resource.Generation)
            return Failure("coding_runtime_profile_stale");
        return Result<CodingRuntimeProfileReference>.Success(reference with { Uid = resource.Uid, Generation = resource.Generation });
    }
    private static Result<CodingRuntimeProfileReference> Failure(string code) => Result<CodingRuntimeProfileReference>.Failure(code);
}
