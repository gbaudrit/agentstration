using Agentstration.Flows.Application;
using Agentstration.Web;
using Agentstration.Web.Features.Flows;
using Agentstration.Web.Security;

namespace Agentstration.Flows.Api;

public static class FlowsApiModule
{
    public static IServiceCollection AddFlowsApi(this IServiceCollection services)
    {
        services.AddSingleton<SignalRFlowRunEventSink>();
        return services;
    }

    public static IEndpointRouteBuilder MapFlowsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAgentstrationFlowApi();
        endpoints.MapHub<FlowRunHub>("/hubs/flow-runs").RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        return endpoints;
    }
}
