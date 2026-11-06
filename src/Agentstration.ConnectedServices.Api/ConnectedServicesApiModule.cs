using Agentstration.Web.Api.ConnectedServices;

namespace Agentstration.ConnectedServices.Api;

public static class ConnectedServicesApiModule
{
    public static IServiceCollection AddConnectedServicesApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapConnectedServicesApi(this IEndpointRouteBuilder endpoints)
    {
        ConnectedServiceEndpoints.Map(endpoints);
        return endpoints;
    }
}
