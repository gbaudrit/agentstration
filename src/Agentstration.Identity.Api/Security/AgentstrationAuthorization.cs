using Agentstration.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Agentstration.Web.Security;

public sealed record WorkspacePermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed record PlatformAdministratorRequirement : IAuthorizationRequirement;
public sealed record InteractiveUserRequirement : IAuthorizationRequirement;

public sealed class InteractiveUserHandler : AuthorizationHandler<InteractiveUserRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, InteractiveUserRequirement requirement)
    {
        if (!context.User.HasClaim(claim => claim.Type == PersonalAccessTokenClaimTypes.TokenId))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class PlatformAdministratorHandler(
    IPlatformAuthorizationService authorization,
    IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<PlatformAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdministratorRequirement requirement)
    {
        var feature = httpContextAccessor.HttpContext?.Features.Get<ResolvedPrincipalFeature>();
        if (!context.User.HasClaim(claim => claim.Type == PersonalAccessTokenClaimTypes.TokenId)
            && feature is not null && await authorization.IsPlatformAdministratorAsync(
                feature.PrincipalId,
                httpContextAccessor.HttpContext!.RequestAborted))
            context.Succeed(requirement);
    }
}

public sealed class WorkspacePermissionHandler(
    ICurrentRequestContext requestContext,
    Agentstration.Identity.Contracts.IAuthorizationService permissions,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<WorkspacePermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkspacePermissionRequirement requirement)
    {
        if (context.Resource is Workspace) return;
        var feature = httpContextAccessor.HttpContext?.Features.Get<ResolvedPrincipalFeature>();
        if (feature is null || !requestContext.IsInitialized || requestContext.Current.PrincipalId != feature.PrincipalId) return;
        if (await permissions.HasPermissionAsync(requestContext.Current, requirement.Permission, httpContextAccessor.HttpContext!.RequestAborted))
            context.Succeed(requirement);
    }
}

public sealed class WorkspaceResourcePermissionHandler(
    ICurrentRequestContext requestContext,
    Agentstration.Identity.Contracts.IAuthorizationService permissions,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<WorkspacePermissionRequirement, Workspace>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkspacePermissionRequirement requirement,
        Workspace resource)
    {
        var feature = httpContextAccessor.HttpContext?.Features.Get<ResolvedPrincipalFeature>();
        if (feature is null || !requestContext.IsInitialized) return;
        var current = requestContext.Current;
        if (current.PrincipalId != feature.PrincipalId || current.TenantId != resource.TenantId || current.WorkspaceId != resource.Id) return;
        if (await permissions.HasPermissionAsync(current, requirement.Permission, httpContextAccessor.HttpContext!.RequestAborted))
            context.Succeed(requirement);
    }
}
