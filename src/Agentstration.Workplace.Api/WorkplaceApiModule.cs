using Agentstration.Application.Work;
using Agentstration.Web;
using Agentstration.Web.Features.Workplace;
using Agentstration.Web.Security;

namespace Agentstration.Workplace.Api;

public static class WorkplaceApiModule
{
    public static IServiceCollection AddWorkplaceApi(this IServiceCollection services)
    {
        services.AddSingleton<IWorkplaceEventSink, SignalRWorkplaceEventSink>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkplaceApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAgentstrationWorkplaceApi();
        endpoints.MapAgentstrationWorkOperationsApi();
        endpoints.MapHub<WorkplaceHub>("/hubs/workplace").RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        return endpoints;
    }
}
