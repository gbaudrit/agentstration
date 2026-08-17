using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Runtime;

public sealed class WorkspaceRuntimeRunExecutionScope(
    IIdentityStore identities,
    Agentstration.Management.Abstractions.IAuthorizationService authorization,
    IRequestContextScopeFactory scopeFactory) : IRuntimeRunExecutionScope
{
    public async ValueTask ValidateAsync(RuntimeRunScope scope, CancellationToken cancellationToken)
    {
        var principal = await identities.GetPrincipalAsync(scope.PrincipalId, cancellationToken);
        var workspace = await identities.GetWorkspaceAsync(scope.TenantId, scope.WorkspaceId.Value, cancellationToken);
        if (principal?.Status != PrincipalStatus.Active || workspace?.Status != WorkspaceStatus.Active)
            throw Denied();

        var requestContext = new RequestContext(scope.PrincipalId, scope.TenantId, scope.WorkspaceId.Value);
        try
        {
            await authorization.EnsurePermissionAsync(requestContext, AuthorizationPermissions.RunsExecute, cancellationToken);
        }
        catch (AuthorizationDeniedException)
        {
            throw Denied();
        }
    }

    public IDisposable Enter(RuntimeRunScope scope) =>
        scopeFactory.Push(new RequestContext(scope.PrincipalId, scope.TenantId, scope.WorkspaceId.Value));

    private static RuntimeRunValidationException Denied() =>
        new("runtime_run_authorization_denied", "The Principal is no longer authorized to execute this Runtime Run in its Workspace.");
}
