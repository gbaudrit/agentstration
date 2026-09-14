using Agentstration.Web;

namespace Agentstration.Bootstrap.Api;

public static class BootstrapApiModule
{
    public static IServiceCollection AddBootstrapApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapBootstrapApi(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapAgentstrationBootstrapProfiles();
}
