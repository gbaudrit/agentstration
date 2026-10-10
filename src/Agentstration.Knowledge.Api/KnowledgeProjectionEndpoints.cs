using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Knowledge.Api.Internal;

internal static class KnowledgeProjectionEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(RouteGroupBuilder sources, RouteGroupBuilder projections)
    {
        sources.MapPost("/{name}/projections", StartAsync)
            .WithSummary("Project acquired Data Source artifacts into a Knowledge Snapshot")
            .Produces<KnowledgeProjectionResource>(StatusCodes.Status201Created)
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sources.MapGet("/{name}/projections", ListAsync)
            .WithSummary("List Knowledge projection history")
            .Produces<IEnumerable<KnowledgeProjectionResource>>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        projections.MapGet("/{id}", GetAsync)
            .WithSummary("Get a Knowledge projection")
            .Produces<KnowledgeProjectionResource>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
    }

    private static Task<IResult> StartAsync(
        string name,
        string? @namespace,
        StartKnowledgeProjectionRequest body,
        HttpResponse response,
        KnowledgeProjectionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.StartAsync(new(name, ResourceNamespace.Parse(@namespace)), body, cancellationToken);
            response.Headers.Location = Location(stored.Value);
            response.Headers.ETag = stored.ETag;
            return Results.Json(stored.Value, statusCode: StatusCodes.Status201Created);
        });

    private static Task<IResult> ListAsync(
        string name,
        string? @namespace,
        KnowledgeProjectionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.ListAsync(new(name, ResourceNamespace.Parse(@namespace)), cancellationToken)));

    private static Task<IResult> GetAsync(
        string id,
        string? @namespace,
        HttpResponse response,
        KnowledgeProjectionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(id, ResourceNamespace.Parse(@namespace), cancellationToken);
            response.Headers.ETag = stored.ETag;
            return Results.Ok(stored.Value);
        });

    private static string Location(KnowledgeProjectionResource value) =>
        $"/api/knowledgeprojections/{Uri.EscapeDataString(value.Name)}?namespace={Uri.EscapeDataString(value.Namespace.Value)}";

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KnowledgeProjectionException exception)
        {
            var status = exception.Code switch
            {
                "knowledge_projection_not_found" or "knowledge_projection_source_not_found" => 404,
                "knowledge_projection_activation_conflict" => 409,
                _ => 422
            };
            return Problem(exception.Code, "Invalid Knowledge projection", status, exception.Message);
        }
        catch (ResourceScopeAccessDeniedException exception)
        { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (ResourceConcurrencyException exception)
        { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { return Problem("knowledge-projection-invalid", "Invalid Knowledge projection", 422, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    {
        Type = ProblemBase + type,
        Title = title,
        Status = status,
        Detail = detail
    });
}
