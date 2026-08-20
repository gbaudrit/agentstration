using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Infrastructure.Runtime;

public sealed class MemoryReadAuthorization(IAuthorizationService authorization) : IMemoryReadAuthorization
{
    public Task EnsureReadAsync(RuntimeRunScope scope, CancellationToken cancellationToken) =>
        authorization.EnsurePermissionAsync(new RequestContext(scope.PrincipalId, scope.TenantId, scope.WorkspaceId.Value), AuthorizationPermissions.MemoryRead, cancellationToken);
}
