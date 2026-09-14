using Agentstration.Web.Api.Models;

namespace Agentstration.Resources.Api;

public static class ResourcesApiModule
{
    public static IServiceCollection AddResourcesApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapResourcesApi(this IEndpointRouteBuilder endpoints)
    {
        ResourceScopeEndpoints.Map(endpoints);
        return endpoints;
    }
}
