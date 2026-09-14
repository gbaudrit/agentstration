using Agentstration.Web.Api.Models;

namespace Agentstration.Secrets.Api;

public static class SecretsApiModule
{
    public static IServiceCollection AddSecretsApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapSecretsApi(this IEndpointRouteBuilder endpoints)
    {
        SecretEndpoints.Map(endpoints);
        return endpoints;
    }
}
