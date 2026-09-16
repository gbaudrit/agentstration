using Agentstration.Identity.Contracts;
using Agentstration.Web.Api.Management;
using Agentstration.Web.Security;

namespace Agentstration.Agents.Api;

public static class AgentsApiModule
{
    public static IServiceCollection AddAgentsApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapAgentsApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api")
            .RequireAuthorization(AgentstrationPolicies.Authenticated)
            .AddEndpointFilter<AgentsWorkspaceAuthorizationFilter>();

        PutAgentEndpoint.Map(group);
        ListAgentsEndpoint.Map(group);
        GetAgentEndpoint.Map(group);
        DeleteAgentEndpoint.Map(group);
        CreateAgentRevisionEndpoint.Map(group);
        PurgeAgentRevisionEndpoint.Map(group);
        CreateDeploymentEndpoint.Map(group);
        GetDeploymentEndpoint.Map(group);
        ListDeploymentsEndpoint.Map(group);
        StartDeploymentEndpoint.Map(group);
        StopDeploymentEndpoint.Map(group);
        ReconcileDeploymentEndpoint.Map(group);
        return endpoints;
    }
}

public sealed class AgentsWorkspaceAuthorizationFilter(ICurrentRequestContext requestContext) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!requestContext.IsInitialized) return Results.Unauthorized();
        var current = requestContext.Current;
        if (context.HttpContext.Request.RouteValues.TryGetValue("workspaceId", out var routeValue)
            && (!Guid.TryParse(routeValue?.ToString(), out var workspaceId) || workspaceId != current.WorkspaceId))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "workspace_access_denied", detail: "The requested workspace is not the current authorized workspace.");
        return await next(context);
    }
}
