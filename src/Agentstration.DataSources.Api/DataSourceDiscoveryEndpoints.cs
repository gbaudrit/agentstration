using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.DataSources.Api.Internal;

internal static class DataSourceDiscoveryEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(RouteGroupBuilder sources, RouteGroupBuilder discoveries, RouteGroupBuilder targets)
    {
        sources.MapPost("/{name}/discoveries", StartAsync).WithSummary("Start Data Source discovery")
            .Produces<DataSourceDiscoveryResource>(StatusCodes.Status202Accepted).RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sources.MapGet("/{name}/discoveries", ListAsync).WithSummary("List Data Source discoveries")
            .Produces<IEnumerable<DataSourceDiscoveryResource>>().RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        sources.MapGet("/{name}/acquisition-targets", ListTargetsAsync).WithSummary("List Data Source acquisition targets")
            .Produces<IEnumerable<DataSourceAcquisitionTargetView>>().RequireAuthorization(AgentstrationPolicies.CanReadResources);
        discoveries.MapGet("/{id}", GetAsync).WithSummary("Get Data Source discovery")
            .Produces<DataSourceDiscoveryResource>().RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        discoveries.MapPost("/{id}/cancel", CancelAsync).WithSummary("Cancel Data Source discovery")
            .Produces<DataSourceDiscoveryResource>().RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        discoveries.MapPost("/{id}/targets", PromoteAsync).WithSummary("Promote a discovered choice to an acquisition target")
            .Produces<DataSourceAcquisitionTargetResource>(StatusCodes.Status201Created).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        targets.MapPut("/{name}", PutTargetAsync).WithSummary("Update a Data Source acquisition target")
            .Produces<DataSourceAcquisitionTargetResource>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        targets.MapPost("/{name}/reconcile", ReconcileTargetAsync).WithSummary("Explicitly reconcile an acquisition target with a discovery choice")
            .Produces<DataSourceAcquisitionTargetResource>().RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        targets.MapDelete("/{name}", DeleteTargetAsync).WithSummary("Delete a Data Source acquisition target")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> StartAsync(string name, string? @namespace, string? scopeRef,
        StartDataSourceDiscoveryRequest body, HttpRequest request, HttpResponse response,
        DataSourceDiscoveryService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var selectedScope = body.DataSourceScopeRef ?? (string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef));
        var stored = await service.StartAsync(ResourceNamespace.Parse(@namespace), name, selectedScope, body.Parameters,
            body.CorrelationId, request.Headers["Idempotency-Key"].FirstOrDefault(), token);
        response.Headers.Location = $"/api/datasourcediscoveries/{Uri.EscapeDataString(stored.Value.Name)}?namespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: StatusCodes.Status202Accepted);
    });

    private static Task<IResult> ListAsync(string name, string? @namespace, string? scopeRef,
        DataSourceDiscoveryService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(
            await service.ListAsync(ResourceNamespace.Parse(@namespace), name,
                string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef), token)));

    private static Task<IResult> GetAsync(string id, string? @namespace, HttpResponse response,
        DataSourceDiscoveryService service, CancellationToken token) => ExecuteAsync(async () =>
    { var stored = await service.GetAsync(ResourceNamespace.Parse(@namespace), id, token); response.Headers.ETag = stored.ETag; return Results.Ok(stored.Value); });

    private static Task<IResult> CancelAsync(string id, string? @namespace, HttpResponse response,
        DataSourceDiscoveryService service, CancellationToken token) => ExecuteAsync(async () =>
    { var stored = await service.CancelAsync(ResourceNamespace.Parse(@namespace), id, token); response.Headers.ETag = stored.ETag; return Results.Ok(stored.Value); });

    private static Task<IResult> PromoteAsync(string id, string? @namespace, PromoteDataSourceAcquisitionTargetRequest body,
        HttpResponse response, DataSourceAcquisitionTargetService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.PromoteAsync(ResourceNamespace.Parse(@namespace), id, body, token); response.Headers.ETag = stored.ETag;
        response.Headers.Location = $"/api/datasourceacquisitiontargets/{Uri.EscapeDataString(stored.Value.Name)}?namespace={Uri.EscapeDataString(stored.Value.Namespace.Value)}";
        return Results.Json(stored.Value, statusCode: StatusCodes.Status201Created);
    });

    private static Task<IResult> ListTargetsAsync(string name, string? @namespace, string? scopeRef,
        DataSourceAcquisitionTargetService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(
            await service.ListAsync(ResourceNamespace.Parse(@namespace), name,
                string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef), token)));

    private static Task<IResult> PutTargetAsync(string name, string? @namespace, PutDataSourceAcquisitionTargetRequest body,
        HttpRequest request, HttpResponse response, DataSourceAcquisitionTargetService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.PutAsync(ResourceNamespace.Parse(@namespace), name, body, request.Headers.IfMatch.FirstOrDefault(), token);
        response.Headers.ETag = stored.ETag; return Results.Ok(stored.Value);
    });

    private static Task<IResult> DeleteTargetAsync(string name, string? @namespace, HttpRequest request,
        DataSourceAcquisitionTargetService service, CancellationToken token) => ExecuteAsync(async () =>
    { await service.DeleteAsync(ResourceNamespace.Parse(@namespace), name, request.Headers.IfMatch.FirstOrDefault(), token); return Results.NoContent(); });

    private static Task<IResult> ReconcileTargetAsync(string name, string? @namespace,
        ReconcileDataSourceAcquisitionTargetRequest body, HttpRequest request, HttpResponse response,
        DataSourceAcquisitionTargetService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.ReconcileAsync(ResourceNamespace.Parse(@namespace), name, body,
        request.Headers.IfMatch.FirstOrDefault(), token); response.Headers.ETag = stored.ETag; return Results.Ok(stored.Value);
    });

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (DataSourceAcquisitionException exception)
        {
            var status = exception.Code.EndsWith("_not_found", StringComparison.Ordinal) ? 404
                : exception.Code.Contains("conflict", StringComparison.Ordinal) || exception.Code.Contains("already_running", StringComparison.Ordinal) ? 409 : 422;
            return Problem(exception.Code, "Invalid Data Source discovery or target", status, exception.Message);
        }
        catch (ResourceScopeAccessDeniedException exception) { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (ResourceConcurrencyException exception) { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { return Problem("data-source-discovery-invalid", "Invalid Data Source discovery or target", 422, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    { Type = ProblemBase + type, Title = title, Status = status, Detail = detail });
}
