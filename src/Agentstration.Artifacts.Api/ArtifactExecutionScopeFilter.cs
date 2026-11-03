using Agentstration.Identity.Contracts;

namespace Agentstration.Artifacts.Api.Internal;

internal sealed class ArtifactExecutionScopeFilter(
    ICurrentRequestContext current,
    IRequestContextScopeFactory scopes) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.TenantAdministration))
            return await next(invocation);
        var context = current.Current;
        if (context.PrincipalId == Guid.Empty || context.TenantId == Guid.Empty || context.WorkspaceId == Guid.Empty)
            return await next(invocation);

        using var scope = scopes.Push(context);
        var result = await next(invocation);
        return result is IResult response ? new WorkspaceResult(response, context, scopes) : result;
    }

    private sealed class WorkspaceResult(IResult response, RequestContext context, IRequestContextScopeFactory scopes) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            using var scope = scopes.Push(context);
            await response.ExecuteAsync(httpContext);
        }
    }
}
