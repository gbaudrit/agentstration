using Agentstration.Knowledge.Api.Internal;

namespace Agentstration.Knowledge.Api;

public static class KnowledgeApiModule
{
    public static IServiceCollection AddKnowledgeApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapKnowledgeApi(this IEndpointRouteBuilder endpoints)
    {
        var sources = endpoints.MapGroup("/api/knowledgesources");
        var projections = endpoints.MapGroup("/api/knowledgeprojections");
        KnowledgeSourceEndpoints.Map(sources);
        KnowledgeProjectionEndpoints.Map(sources, projections);
        KnowledgeSnapshotEndpoints.Map(sources, endpoints.MapGroup("/api/knowledgesnapshots"));
        KnowledgeRetrievalEndpoints.Map(sources);
        return endpoints;
    }
}
