using Agentstration.Management.Abstractions;
using Microsoft.AspNetCore.Authorization;

namespace Agentstration.Web.Security;

public static class AgentstrationPolicies
{
    public const string Authenticated = "agentstration:authenticated";
    public const string PlatformAdmin = "agentstration:platform-admin";
    public const string WorkspaceReader = "agentstration:workspace-reader";
    public const string WorkspaceAdmin = "agentstration:workspace-admin";
    public const string AuthorizationReader = "agentstration:authorization-reader";
    public const string AuthorizationAdmin = "agentstration:authorization-admin";
    public const string CanReadAgents = "agentstration:agents:read";
    public const string CanManageAgents = "agentstration:agents:manage";
    public const string CanRunAgents = "agentstration:agents:run";
    public const string CanReadRuns = "agentstration:runs:read";
    public const string CanRunFlows = "agentstration:flows:run";
}

public sealed record WorkspacePermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed record ResolvedPrincipalFeature(Principal Principal);

public sealed record PlatformAdministratorRequirement : IAuthorizationRequirement;

public sealed class PlatformAdministratorHandler(
    IPlatformAuthorizationService authorization,
    IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<PlatformAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdministratorRequirement requirement)
    {
        var feature = httpContextAccessor.HttpContext?.Features.Get<ResolvedPrincipalFeature>();
        if (feature is not null && await authorization.IsPlatformAdministratorAsync(
                feature.Principal.Id,
                httpContextAccessor.HttpContext!.RequestAborted))
            context.Succeed(requirement);
    }
}

public sealed class WorkspacePermissionHandler(
    ICurrentRequestContext requestContext,
    Agentstration.Management.Abstractions.IAuthorizationService permissions,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<WorkspacePermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkspacePermissionRequirement requirement)
    {
        if (context.Resource is Workspace) return;
        var feature = httpContextAccessor.HttpContext?.Features.Get<ResolvedPrincipalFeature>();
        if (feature is null || !requestContext.IsInitialized || requestContext.Current.PrincipalId != feature.Principal.Id) return;
        if (await permissions.HasPermissionAsync(requestContext.Current, requirement.Permission, httpContextAccessor.HttpContext!.RequestAborted))
            context.Succeed(requirement);
    }
}

public sealed class WorkspaceResourcePermissionHandler(
    ICurrentRequestContext requestContext,
    Agentstration.Management.Abstractions.IAuthorizationService permissions,
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
        if (current.PrincipalId != feature.Principal.Id || current.TenantId != resource.TenantId || current.WorkspaceId != resource.Id) return;
        if (await permissions.HasPermissionAsync(current, requirement.Permission, httpContextAccessor.HttpContext!.RequestAborted))
            context.Succeed(requirement);
    }
}
