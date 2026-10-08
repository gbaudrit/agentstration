using Agentstration.Knowledge.Api.Internal;

namespace Agentstration.Knowledge.Api;

public static class KnowledgeApiModule
{
    public static IServiceCollection AddKnowledgeApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapKnowledgeApi(this IEndpointRouteBuilder endpoints)
    {
        var sources = endpoints.MapGroup("/api/knowledgesources");
        KnowledgeSourceProfileEndpoints.Map(endpoints.MapGroup("/api/knowledgesourceprofiles"));
        var acquisitions = endpoints.MapGroup("/api/knowledgeacquisitions");
        var projections = endpoints.MapGroup("/api/knowledgeprojections");
        KnowledgeSourceEndpoints.Map(sources);
        KnowledgeAcquisitionEndpoints.Map(sources, acquisitions);
        KnowledgeProjectionEndpoints.Map(sources, projections);
        KnowledgeSnapshotEndpoints.Map(sources, acquisitions, endpoints.MapGroup("/api/knowledgesnapshots"));
        KnowledgeRetrievalEndpoints.Map(sources);
        return endpoints;
    }
}
