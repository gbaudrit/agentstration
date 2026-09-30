using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Knowledge.Api.Internal;

internal static class KnowledgeAcquisitionEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(RouteGroupBuilder sources, RouteGroupBuilder acquisitions)
    {
        sources.MapPost("/{name}/acquisitions", StartAsync)
            .WithSummary("Start a KnowledgeSource acquisition")
            .Produces<KnowledgeAcquisitionResource>(StatusCodes.Status202Accepted)
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sources.MapGet("/{name}/acquisitions", ListForSourceAsync)
            .WithSummary("List KnowledgeSource acquisition history")
            .Produces<IEnumerable<KnowledgeAcquisitionResource>>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        acquisitions.MapGet("/", ListAsync)
            .WithSummary("List KnowledgeSource acquisitions")
            .Produces<IEnumerable<KnowledgeAcquisitionResource>>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        acquisitions.MapGet("/{id}", GetAsync)
            .WithSummary("Get a KnowledgeSource acquisition")
            .Produces<KnowledgeAcquisitionResource>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        acquisitions.MapPost("/{id}/cancel", CancelAsync)
            .WithSummary("Cancel a KnowledgeSource acquisition")
            .Produces<KnowledgeAcquisitionResource>()
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        acquisitions.MapPost("/{id}/retry", RetryAsync)
            .WithSummary("Retry a KnowledgeSource acquisition")
            .Produces<KnowledgeAcquisitionResource>(StatusCodes.Status202Accepted)
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }

    private static Task<IResult> StartAsync(
        string name,
        string? @namespace,
        StartKnowledgeAcquisitionRequest body,
        HttpRequest request,
        HttpResponse response,
        KnowledgeAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.StartAsync(new(name, ResourceNamespace.Parse(@namespace)), body.Parameters,
                body.CorrelationId, request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);
            response.Headers.Location = Location(stored.Value);
            response.Headers.ETag = stored.ETag;
            return Results.Json(stored.Value, statusCode: StatusCodes.Status202Accepted);
        });

    private static Task<IResult> ListForSourceAsync(
        string name,
        string? @namespace,
        KnowledgeAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.ListAsync(new KnowledgeSourceId(name, ResourceNamespace.Parse(@namespace)), cancellationToken)));

    private static Task<IResult> ListAsync(
        KnowledgeAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.ListAsync(null, cancellationToken)));

    private static Task<IResult> GetAsync(
        string id,
        string? @namespace,
        HttpResponse response,
        KnowledgeAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.GetAsync(id, ResourceNamespace.Parse(@namespace), cancellationToken);
            response.Headers.ETag = stored.ETag;
            return Results.Ok(stored.Value);
        });

    private static Task<IResult> CancelAsync(
        string id,
        string? @namespace,
        HttpResponse response,
        KnowledgeAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.CancelAsync(id, ResourceNamespace.Parse(@namespace), cancellationToken);
            response.Headers.ETag = stored.ETag;
            return Results.Ok(stored.Value);
        });

    private static Task<IResult> RetryAsync(
        string id,
        string? @namespace,
        RetryKnowledgeAcquisitionRequest body,
        HttpResponse response,
        KnowledgeAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.RetryAsync(id, ResourceNamespace.Parse(@namespace), body.CorrelationId, cancellationToken);
            response.Headers.Location = Location(stored.Value);
            response.Headers.ETag = stored.ETag;
            return Results.Json(stored.Value, statusCode: StatusCodes.Status202Accepted);
        });

    private static string Location(KnowledgeAcquisitionResource value) =>
        $"/api/knowledgeacquisitions/{Uri.EscapeDataString(value.Name)}?namespace={Uri.EscapeDataString(value.Namespace.Value)}";

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KnowledgeSourceNotFoundException exception)
        { return Problem("knowledge-source-not-found", "KnowledgeSource not found", 404, exception.Message); }
        catch (KnowledgeAcquisitionException exception)
        {
            var status = exception.Code switch
            {
                "knowledge_acquisition_not_found" => 404,
                "knowledge_acquisition_already_running" or "knowledge_acquisition_idempotency_conflict" => 409,
                _ => 422
            };
            return Problem(exception.Code, "Invalid Knowledge acquisition", status, exception.Message);
        }
        catch (ResourceScopeAccessDeniedException exception)
        { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (ResourceConcurrencyException exception)
        { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { return Problem("knowledge-acquisition-invalid", "Invalid Knowledge acquisition", 422, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    {
        Type = ProblemBase + type,
        Title = title,
        Status = status,
        Detail = detail
    });
}
