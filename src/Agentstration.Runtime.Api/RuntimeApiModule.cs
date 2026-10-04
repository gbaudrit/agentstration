using Agentstration.Runtime.Api.Api;
using Agentstration.Web;
using Agentstration.Web.Api.Management;
using Agentstration.Web.Api.Models;

namespace Agentstration.Runtime.Api;

public static class RuntimeApiModule
{
    public static IServiceCollection AddRuntimeApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapRuntimeApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAgentstrationRuntimeApi();
        endpoints.MapAwpRuntimeWorkerEndpoints();
        RuntimeProfileEndpoints.Map(endpoints.MapGroup("/api/runtimeprofiles"));
        RouteAndExecuteEndpoint.Map(endpoints.MapGroup("/api"));
        return endpoints;
    }
}
