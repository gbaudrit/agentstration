using Agentstration.Management.Abstractions;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Security;

namespace Agentstration.Web.Hosting;

public sealed class PrincipalResolutionMiddleware(RequestDelegate next)
{
    public const string WorkspaceHeader = "X-Agentstration-Workspace";

    public async Task InvokeAsync(
        HttpContext httpContext,
        IPrincipalResolver resolver,
        IIdentityStore identityStore,
        IRequestContextScopeFactory scopeFactory)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            await next(httpContext);
            return;
        }

        var accountClaim = httpContext.User.FindFirst(LocalIdentityClaimTypes.AccountId)?.Value;
        Principal? principal;
        if (Guid.TryParse(accountClaim, out var accountId))
            principal = await resolver.ResolveLocalAsync(accountId, httpContext.RequestAborted);
        else
        {
            var issuer = httpContext.User.FindFirst("iss")?.Value;
            var subject = httpContext.User.FindFirst("sub")?.Value;
            principal = string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)
                ? null
                : await resolver.ResolveAsync(issuer, subject, httpContext.RequestAborted);
        }
        if (principal?.Status != PrincipalStatus.Active)
        {
            await next(httpContext);
            return;
        }

        httpContext.Features.Set(new ResolvedPrincipalFeature(principal));
        var memberships = await identityStore.ListWorkspaceMembershipsAsync(principal.Id, httpContext.RequestAborted);
        var activeMemberships = memberships.Where(value => value.Status == MembershipStatus.Active).ToArray();
        var requestedWorkspaceId = RequestedWorkspace(httpContext);
        var selected = requestedWorkspaceId is null
            ? activeMemberships.FirstOrDefault()
            : activeMemberships.SingleOrDefault(value => value.WorkspaceId == requestedWorkspaceId.Value);
        if (selected is null)
        {
            await next(httpContext);
            return;
        }

        var workspace = await identityStore.GetWorkspaceAsync(selected.WorkspaceId, httpContext.RequestAborted);
        if (workspace?.Status != WorkspaceStatus.Active)
        {
            await next(httpContext);
            return;
        }

        using (scopeFactory.Push(new RequestContext(principal.Id, workspace.TenantId, workspace.Id)))
            await next(httpContext);
    }

    private static Guid? RequestedWorkspace(HttpContext context)
    {
        if (context.Request.RouteValues.TryGetValue("workspaceId", out var routeValue)
            && Guid.TryParse(routeValue?.ToString(), out var routeWorkspace)) return routeWorkspace;
        if (Guid.TryParse(context.Request.Headers[WorkspaceHeader].FirstOrDefault(), out var headerWorkspace)) return headerWorkspace;
        if (Guid.TryParse(context.Request.Cookies[RequestContextMiddleware.WorkspaceCookie], out var cookieWorkspace)) return cookieWorkspace;
        return null;
    }
}
