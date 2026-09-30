using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Knowledge.Api.Internal;

internal static class KnowledgeSnapshotEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(
        RouteGroupBuilder sources,
        RouteGroupBuilder acquisitions,
        RouteGroupBuilder snapshots)
    {
        acquisitions.MapPost("/{id}/snapshots", PublishAsync)
            .WithSummary("Publish a Knowledge Snapshot from an acquisition")
            .Produces<KnowledgeSnapshotResource>(StatusCodes.Status201Created)
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        acquisitions.MapGet("/{id}/snapshot-publications", ListPublicationsAsync)
            .WithSummary("List Knowledge Snapshot publication attempts")
            .Produces<IEnumerable<KnowledgeSnapshotPublicationResource>>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        sources.MapGet("/{name}/snapshots", ListAsync)
            .WithSummary("List Knowledge Snapshot history")
            .Produces<IEnumerable<KnowledgeSnapshotView>>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        sources.MapGet("/{name}/snapshots/active", GetActiveAsync)
            .WithSummary("Get the active Knowledge Snapshot")
            .Produces<KnowledgeSnapshotView>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        sources.MapPut("/{name}/snapshots/active", SelectActiveAsync)
            .WithSummary("Select the active Knowledge Snapshot")
            .Produces<KnowledgeSnapshotView>()
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        snapshots.MapGet("/{id}", GetAsync)
            .WithSummary("Get a Knowledge Snapshot")
            .Produces<KnowledgeSnapshotView>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
    }

    private static Task<IResult> PublishAsync(
        string id,
        string? @namespace,
        PublishKnowledgeSnapshotRequest body,
        HttpRequest request,
        HttpResponse response,
        KnowledgeSnapshotService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var stored = await service.PublishAsync(id, ResourceNamespace.Parse(@namespace), body,
                request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);
            response.Headers.ETag = stored.ETag;
            response.Headers.Location = Location(stored.Value);
            return Results.Json(stored.Value, statusCode: StatusCodes.Status201Created);
        });

    private static Task<IResult> ListPublicationsAsync(
        string id,
        string? @namespace,
        KnowledgeSnapshotService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.ListPublicationsAsync(id, ResourceNamespace.Parse(@namespace), cancellationToken)));

    private static Task<IResult> ListAsync(
        string name,
        string? @namespace,
        KnowledgeSnapshotService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.ListAsync(new(name, ResourceNamespace.Parse(@namespace)), cancellationToken)));

    private static Task<IResult> GetActiveAsync(
        string name,
        string? @namespace,
        KnowledgeSnapshotService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            var value = await service.GetActiveAsync(new(name, ResourceNamespace.Parse(@namespace)), cancellationToken);
            return value is null
                ? Problem("knowledge_snapshot_active_not_found", "Active Knowledge Snapshot not found", 404,
                    $"KnowledgeSource '{name}' has no active Snapshot.")
                : Results.Ok(value);
        });

    private static Task<IResult> SelectActiveAsync(
        string name,
        string? @namespace,
        SelectActiveKnowledgeSnapshotRequest body,
        KnowledgeSnapshotService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.SelectActiveAsync(new(name, ResourceNamespace.Parse(@namespace)),
                body.SnapshotName, cancellationToken)));

    private static Task<IResult> GetAsync(
        string id,
        string? @namespace,
        KnowledgeSnapshotService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.GetAsync(new(id, ResourceNamespace.Parse(@namespace)), cancellationToken)));

    private static string Location(KnowledgeSnapshotResource value) =>
        $"/api/knowledgesnapshots/{Uri.EscapeDataString(value.Name)}?namespace={Uri.EscapeDataString(value.Namespace.Value)}";

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KnowledgeSnapshotException exception)
        {
            var status = exception.Code switch
            {
                "knowledge_snapshot_not_found" or "knowledge_snapshot_source_not_found"
                    or "knowledge_snapshot_acquisition_not_found" => 404,
                "knowledge_snapshot_idempotency_conflict" or "knowledge_snapshot_selection_conflict"
                    or "knowledge_snapshot_immutable_conflict" => 409,
                _ => 422
            };
            return Problem(exception.Code, "Invalid Knowledge Snapshot operation", status, exception.Message);
        }
        catch (ResourceConcurrencyException exception)
        { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (ResourceScopeAccessDeniedException exception)
        { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { return Problem("knowledge_snapshot_operation_invalid", "Invalid Knowledge Snapshot operation", 422, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    {
        Type = ProblemBase + type,
        Title = title,
        Status = status,
        Detail = detail
    });
}
