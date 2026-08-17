using Agentstration.Web.Api.Runtime;

namespace Agentstration.Web;

public static class RuntimeEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationRuntimeApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/runtime/runs")
            .RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.Authenticated);
        CreateRuntimeRunEndpoint.Map(group);
        ListRuntimeRunsEndpoint.Map(group);
        GetRuntimeRunEndpoint.Map(group);
        StreamRuntimeRunEventsEndpoint.Map(group);
        CancelRuntimeRunEndpoint.Map(group);
        RetryRuntimeRunEndpoint.Map(group);
        var agents = endpoints.MapGroup("/api/runtime/agents")
            .RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.Authenticated);
        GetAgentRuntimeReadinessEndpoint.Map(agents);
        PrepareAgentRuntimeEndpoint.Map(agents);
        return endpoints;
    }
}
