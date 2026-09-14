using Agentstration.Web.Api.Management;
using Agentstration.Web.Api.Models;
using Agentstration.Web.Security;

namespace Agentstration.Sources.Api;

public static class SourcesApiModule
{
    public static IServiceCollection AddSourcesApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapSourcesApi(this IEndpointRouteBuilder endpoints)
    {
        SourceEndpoints.Map(endpoints.MapGroup("/api").RequireAuthorization(AgentstrationPolicies.Authenticated));
        SourceProviderEndpoints.Map(endpoints);
        SourceRegistryEndpoints.Map(endpoints);
        return endpoints;
    }
}
