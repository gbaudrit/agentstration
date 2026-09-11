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
        IPlatformAuthorizationService platformAuthorization,
        IRequestContextScopeFactory scopeFactory)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            await next(httpContext);
            return;
        }

        var personalAccessTokenClaim = httpContext.User.FindFirst(PersonalAccessTokenClaimTypes.TokenId)?.Value;
        var accountClaim = httpContext.User.FindFirst(LocalIdentityClaimTypes.AccountId)?.Value;
        Principal? principal;
        if (Guid.TryParse(personalAccessTokenClaim, out _)
            && Guid.TryParse(httpContext.User.FindFirst(PersonalAccessTokenClaimTypes.PrincipalId)?.Value, out var personalAccessTokenPrincipalId))
            principal = await identityStore.GetPrincipalAsync(personalAccessTokenPrincipalId, httpContext.RequestAborted);
        else if (Guid.TryParse(accountClaim, out var accountId))
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
        var requestedWorkspaceId = RequestedWorkspace(httpContext);
        var isPersonalAccessToken = Guid.TryParse(personalAccessTokenClaim, out var personalAccessTokenId);
        var personalAccessTokenWorkspaceId = Guid.TryParse(
            httpContext.User.FindFirst(PersonalAccessTokenClaimTypes.WorkspaceId)?.Value,
            out var parsedPersonalAccessTokenWorkspaceId)
            ? parsedPersonalAccessTokenWorkspaceId
            : (Guid?)null;
        var isPlatformAdministrator = await platformAuthorization.IsPlatformAdministratorAsync(
            principal.Id,
            httpContext.RequestAborted);
        Workspace? workspace;
        if (isPersonalAccessToken)
        {
            if (personalAccessTokenWorkspaceId is not { } restrictedWorkspaceId
                || requestedWorkspaceId is not null && requestedWorkspaceId != restrictedWorkspaceId)
            {
                await next(httpContext);
                return;
            }
            workspace = isPlatformAdministrator
                ? await identityStore.GetWorkspaceAsync(restrictedWorkspaceId, httpContext.RequestAborted)
                : await ResolveMembershipWorkspaceAsync(
                    identityStore,
                    principal.Id,
                    restrictedWorkspaceId,
                    httpContext.RequestAborted);
        }
        else if (isPlatformAdministrator)
        {
            workspace = requestedWorkspaceId is { } requested
                ? await identityStore.GetWorkspaceAsync(requested, httpContext.RequestAborted)
                : await ResolvePlatformDefaultWorkspaceAsync(identityStore, principal.Id, httpContext.RequestAborted);
        }
        else
        {
            workspace = await ResolveMembershipWorkspaceAsync(
                identityStore,
                principal.Id,
                requestedWorkspaceId,
                httpContext.RequestAborted);
        }
        if (workspace?.Status != WorkspaceStatus.Active)
        {
            await next(httpContext);
            return;
        }

        var tenant = await identityStore.GetTenantAsync(workspace.TenantId, httpContext.RequestAborted);
        if (tenant?.Status != TenantStatus.Active)
        {
            await next(httpContext);
            return;
        }

        var restriction = isPersonalAccessToken
            ? new AuthorizationRestriction(
                personalAccessTokenId,
                workspace.Id,
                httpContext.User.FindAll(PersonalAccessTokenClaimTypes.Permission)
                    .Select(claim => claim.Value)
                    .ToHashSet(StringComparer.Ordinal))
            : null;
        using (scopeFactory.Push(new RequestContext(principal.Id, workspace.TenantId, workspace.Id, restriction)))
            await next(httpContext);
    }

    private static async Task<Workspace?> ResolveMembershipWorkspaceAsync(
        IIdentityStore store,
        Guid principalId,
        Guid? requestedWorkspaceId,
        CancellationToken cancellationToken)
    {
        var memberships = (await store.ListWorkspaceMembershipsAsync(principalId, cancellationToken))
            .Where(value => value.Status == MembershipStatus.Active);
        var selected = requestedWorkspaceId is null
            ? memberships.FirstOrDefault()
            : memberships.SingleOrDefault(value => value.WorkspaceId == requestedWorkspaceId.Value);
        return selected is null ? null : await store.GetWorkspaceAsync(selected.WorkspaceId, cancellationToken);
    }

    private static async Task<Workspace?> ResolvePlatformDefaultWorkspaceAsync(
        IIdentityStore store,
        Guid principalId,
        CancellationToken cancellationToken)
    {
        var preferences = await store.GetPrincipalPreferencesAsync(principalId, cancellationToken);
        if (preferences?.DefaultWorkspaceId is { } defaultWorkspaceId)
        {
            var preferred = await store.GetWorkspaceAsync(defaultWorkspaceId, cancellationToken);
            if (preferred is not null
                && (preferences.DefaultTenantId is null || preferred.TenantId == preferences.DefaultTenantId))
                return preferred;
        }

        foreach (var tenant in (await store.ListTenantsAsync(cancellationToken)).Where(value => value.Status == TenantStatus.Active))
        {
            var workspace = (await store.ListWorkspacesAsync(tenant.Id, cancellationToken))
                .FirstOrDefault(value => value.Status == WorkspaceStatus.Active);
            if (workspace is not null) return workspace;
        }
        return null;
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
