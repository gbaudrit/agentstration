using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

public sealed class RequestContextMiddleware(RequestDelegate next)
{
    public const string WorkspaceCookie = "agentstration.workspace";

    public async Task InvokeAsync(
        HttpContext httpContext,
        ICurrentRequestContext current,
        IRequestContextScopeFactory scopeFactory,
        IdentityExperienceService experience)
    {
        if (!current.IsInitialized
            || !httpContext.Request.Cookies.TryGetValue(WorkspaceCookie, out var rawWorkspace)
            || !Guid.TryParse(rawWorkspace, out var workspaceId)
            || workspaceId == current.Current.WorkspaceId)
        {
            await next(httpContext);
            return;
        }

        try
        {
            var selected = await experience.ValidateWorkspaceSelectionAsync(workspaceId, httpContext.RequestAborted);
            using (scopeFactory.Push(selected)) await next(httpContext);
        }
        catch (AuthorizationDeniedException)
        {
            httpContext.Response.Cookies.Delete(WorkspaceCookie);
            await next(httpContext);
        }
    }
}
