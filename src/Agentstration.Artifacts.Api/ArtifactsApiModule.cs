using Agentstration.Artifacts.Api.Internal;

namespace Agentstration.Artifacts.Api;

public static class ArtifactsApiModule
{
    public static IServiceCollection AddArtifactsApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapArtifactsApi(this IEndpointRouteBuilder endpoints)
    {
        ArtifactEndpoints.Map(endpoints.MapGroup("/api/artifacts"));
        return endpoints;
    }
}
