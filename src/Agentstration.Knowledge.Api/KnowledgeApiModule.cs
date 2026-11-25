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
        KnowledgeAccessEndpoints.Map(sources);
        endpoints.MapPost("/api/knowledge-previews/prepare", async (Agentstration.Knowledge.Contracts.PrepareKnowledgePreviewRequest body,
            KnowledgePreviewService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.PrepareAsync(body, ct)); }
            catch (Agentstration.DataSources.Contracts.SourceSampleUnavailableException error) { return Results.Problem(statusCode: 422, title: error.Code); }
            catch (KnowledgeSourceValidationException error) { return Results.Problem(statusCode: 422, title: error.Code); }
            catch (Agentstration.Flows.FlowValidationException error) { return Results.Problem(statusCode: 422, title: error.Code); }
        }).RequireAuthorization(Agentstration.Web.Security.AgentstrationPolicies.CanExecuteRuns);
        return endpoints;
    }
}
