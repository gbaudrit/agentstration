using Agentstration.Web.Api.Models;

namespace Agentstration.Parameters.Api;

public static class ParametersApiModule
{
    public static IServiceCollection AddParametersApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapParametersApi(this IEndpointRouteBuilder endpoints)
    {
        ParameterEndpoints.Map(endpoints);
        return endpoints;
    }
}
