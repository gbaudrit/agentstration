using Agentstration.Knowledge.Api.Internal;

namespace Agentstration.Knowledge.Api;

public static class KnowledgeApiModule
{
    public static IServiceCollection AddKnowledgeApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapKnowledgeApi(this IEndpointRouteBuilder endpoints)
    {
        var sources = endpoints.MapGroup("/api/knowledgesources");
        KnowledgeSourceEndpoints.Map(sources);
        KnowledgeAcquisitionEndpoints.Map(sources, endpoints.MapGroup("/api/knowledgeacquisitions"));
        return endpoints;
    }
}
