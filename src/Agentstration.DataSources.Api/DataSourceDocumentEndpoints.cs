using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;
using Agentstration.ResourceManagement;
using Agentstration.Identity.Contracts;
using Agentstration.Storage;
using Agentstration.Web.Security;

namespace Agentstration.DataSources.Api.Internal;

internal static class DataSourceDocumentEndpoints
{
    public static void Map(RouteGroupBuilder acquisitions)
    {
        acquisitions.MapGet("/{id}/documents/capabilities", (string id, string? @namespace,
            DataSourceDocumentAccessService access, CancellationToken ct) => ExecuteAsync(async () =>
                Results.Ok(await access.DescribeAsync(ResourceNamespace.Parse(@namespace), id, ct))))
            .Produces<DataSourceDocumentDescription>().WithSummary("Inspect the exact acquisition document publication")
            .RequireAuthorization(AgentstrationPolicies.CanReadRuns);
        acquisitions.MapPost("/{id}/documents/query", (string id, string? @namespace, DataSourceDocumentQueryRequest body,
            DataSourceDocumentAccessService access, CancellationToken ct) => ExecuteAsync(async () =>
                Results.Ok(await access.QueryAcquisitionAsync(ResourceNamespace.Parse(@namespace), id, body, ct))))
            .Produces<DataSourceDocumentQueryResult>().WithSummary("Query the selected immutable acquisition publication")
            .RequireAuthorization(AgentstrationPolicies.CanExecuteRuns);
        acquisitions.MapPost("/{id}/documents/tools", (string id, string? @namespace, PublishDataSourceDocumentToolRequest body,
            DataSourceDocumentToolExposureService tools, CancellationToken ct) => ExecuteAsync(async () =>
                Results.Ok(await tools.PublishAsync(ResourceNamespace.Parse(@namespace), id, body, ct))))
            .Produces<ResourceReference>().WithSummary("Publish a versioned latest-or-exact acquisition query Tool")
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (DataSourceValidationException e) { return Problem(e.Code, e.Code == "data_source_access_denied" ? 403 : 422); }
        catch (DataSourceAcquisitionException e) { return Problem(e.Code, e.Code.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 422); }
        catch (StorageException e) { return Problem(e.Code, e.StatusCode); }
        catch (AuthorizationDeniedException) { return Problem("data_source_document_access_denied", 403); }
        catch (ResourceScopeAccessDeniedException) { return Problem("data_source_document_selection_forbidden", 403); }
        catch (ResourceConcurrencyException) { return Problem("resource_version_conflict", 409); }
        catch (Exception e) when (e is ArgumentException or FormatException)
        { return Problem("data_source_document_query_invalid", 422); }
    }

    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, title: code,
        type: "https://agentstration.dev/problems/" + code, extensions: new Dictionary<string, object?> { ["code"] = code });
}
