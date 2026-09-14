using Agentstration.Web.Api.Management;
using Agentstration.Web.Security;

namespace Agentstration.Packs.Api;

public static class PacksApiModule
{
    public static IServiceCollection AddPacksApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapPacksApi(this IEndpointRouteBuilder endpoints)
    {
        PackEndpoints.Map(endpoints.MapGroup("/api").RequireAuthorization(AgentstrationPolicies.Authenticated));
        PackSourceEndpoints.Map(endpoints);
        return endpoints;
    }
}
