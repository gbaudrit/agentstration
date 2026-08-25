using Agentstration.Management.Abstractions;
using Agentstration.Web.Api.Management;
using Agentstration.Web.Security;

namespace Agentstration.Web;

public static class ManagementEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationManagementApi(this IEndpointRouteBuilder endpoints)
    {
        MapRoutes(endpoints.MapGroup("/api")
            .RequireAuthorization(AgentstrationPolicies.Authenticated)
            .AddEndpointFilter<ManagementAuthorizationFilter>());
        return endpoints;
    }

    private static void MapRoutes(RouteGroupBuilder group)
    {
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
        RouteAndExecuteEndpoint.Map(group);
        PackEndpoints.Map(group);
        TriggerEndpoints.Map(group);
    }
}

public sealed class ManagementAuthorizationFilter(ICurrentRequestContext requestContext) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocationContext, EndpointFilterDelegate next)
    {
        if (!requestContext.IsInitialized) return Results.Unauthorized();
        var context = requestContext.Current;
        if (invocationContext.HttpContext.Request.RouteValues.TryGetValue("workspaceId", out var routeValue)
            && (!Guid.TryParse(routeValue?.ToString(), out var workspaceId) || workspaceId != context.WorkspaceId))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "workspace_access_denied", detail: "The requested workspace is not the current authorized workspace.");

        return await next(invocationContext);
    }
}
