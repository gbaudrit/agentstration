using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.DataSources.Api.Internal;

internal static class DataSourceAcquisitionEndpoints
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static void Map(RouteGroupBuilder sources, RouteGroupBuilder acquisitions)
    {
        sources.MapPost("/{name}/acquisitions", StartAsync)
            .WithSummary("Start a Data Source acquisition")
            .Produces<DataSourceAcquisitionResource>(StatusCodes.Status202Accepted)
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        sources.MapGet("/{name}/acquisitions", ListForSourceAsync)
            .WithSummary("List Data Source acquisition history")
            .Produces<IEnumerable<DataSourceAcquisitionResource>>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        acquisitions.MapGet("/", ListAsync)
            .WithSummary("List Data Source acquisitions")
            .Produces<IEnumerable<DataSourceAcquisitionResource>>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        acquisitions.MapGet("/{id}", GetAsync)
            .WithSummary("Get a Data Source acquisition")
            .Produces<DataSourceAcquisitionResource>()
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        acquisitions.MapPost("/{id}/cancel", CancelAsync)
            .WithSummary("Cancel a Data Source acquisition")
            .Produces<DataSourceAcquisitionResource>()
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        acquisitions.MapPost("/{id}/retry", RetryAsync)
            .WithSummary("Retry a Data Source acquisition")
            .Produces<DataSourceAcquisitionResource>(StatusCodes.Status202Accepted)
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
    }

    private static Task<IResult> StartAsync(
        string name,
        string? @namespace,
        string? scopeRef,
        StartDataSourceAcquisitionRequest body,
        HttpRequest request,
        HttpResponse response,
        DataSourceAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var selectedScope = body.DataSourceScopeRef
            ?? (string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef));
        var stored = await service.StartAsync(ResourceNamespace.Parse(@namespace), name, selectedScope,
            body.Parameters, body.CorrelationId, request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);
        response.Headers.Location = Location(stored.Value);
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: StatusCodes.Status202Accepted);
    });

    private static Task<IResult> ListForSourceAsync(
        string name,
        string? @namespace,
        string? scopeRef,
        DataSourceAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.ListAsync(ResourceNamespace.Parse(@namespace), name,
                string.IsNullOrWhiteSpace(scopeRef) ? null : ResourceScopeRef.Parse(scopeRef), cancellationToken)));

    private static Task<IResult> ListAsync(
        DataSourceAcquisitionService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Results.Ok(
            await service.ListAsync(null, null, null, cancellationToken)));

    private static Task<IResult> GetAsync(string id, string? @namespace, HttpResponse response,
        DataSourceAcquisitionService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var stored = await service.GetAsync(ResourceNamespace.Parse(@namespace), id, cancellationToken);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> CancelAsync(string id, string? @namespace, HttpResponse response,
        DataSourceAcquisitionService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var stored = await service.CancelAsync(ResourceNamespace.Parse(@namespace), id, cancellationToken);
        response.Headers.ETag = stored.ETag;
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> RetryAsync(string id, string? @namespace, RetryDataSourceAcquisitionRequest body,
        HttpResponse response, DataSourceAcquisitionService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var stored = await service.RetryAsync(ResourceNamespace.Parse(@namespace), id,
                body.CorrelationId, cancellationToken);
            response.Headers.Location = Location(stored.Value);
            response.Headers.ETag = stored.ETag;
            return Results.Json(stored.Value, statusCode: StatusCodes.Status202Accepted);
        });

    private static string Location(DataSourceAcquisitionResource value) =>
        $"/api/datasourceacquisitions/{Uri.EscapeDataString(value.Name)}?namespace={Uri.EscapeDataString(value.Namespace.Value)}";

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (DataSourceAcquisitionException exception)
        {
            var status = exception.Code switch
            {
                "data_source_not_found" or "data_source_acquisition_not_found" => 404,
                "data_source_acquisition_already_running" or "data_source_acquisition_idempotency_conflict" => 409,
                _ => 422
            };
            return Problem(exception.Code, "Invalid Data Source acquisition", status, exception.Message);
        }
        catch (DataSourceValidationException exception)
        { return Problem(exception.Code, "Invalid Data Source acquisition", 422, exception.Message); }
        catch (ResourceScopeAccessDeniedException exception)
        { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (ResourceReferenceOutsideScopeException exception)
        { return Problem("resource-reference-outside-scope", "Resource reference outside scope", 422, exception.Message); }
        catch (ResourceReferenceAmbiguousException exception)
        { return Problem("resource-reference-ambiguous", "Ambiguous resource reference", 409, exception.Message); }
        catch (ResourceConcurrencyException exception)
        { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { return Problem("data-source-acquisition-invalid", "Invalid Data Source acquisition", 422, exception.Message); }
    }

    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    {
        Type = ProblemBase + type,
        Title = title,
        Status = status,
        Detail = detail
    });
}
