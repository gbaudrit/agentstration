using Agentstration.Knowledge.Api.Internal;

namespace Agentstration.Knowledge.Api;

public static class KnowledgeApiModule
{
    public static IServiceCollection AddKnowledgeApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapKnowledgeApi(this IEndpointRouteBuilder endpoints)
    {
        KnowledgeSourceEndpoints.Map(endpoints.MapGroup("/api/knowledgesources"));
        return endpoints;
    }
}
