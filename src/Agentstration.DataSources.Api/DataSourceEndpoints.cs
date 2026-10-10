using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.DataSources.Api.Internal;

internal static class DataSourceEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync).Produces<IEnumerable<DataSourceResource>>().WithSummary("List Data Sources").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{name}", GetAsync).Produces<DataSourceResource>().WithSummary("Get a Data Source").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{name}/readiness", ReadinessAsync).Produces<DataSourceReadiness>().WithSummary("Get Data Source readiness").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/", CreateAsync).Produces<DataSourceResource>(StatusCodes.Status201Created).WithSummary("Create a Data Source").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPut("/{name}", PutAsync).Produces<DataSourceResource>().WithSummary("Update a Data Source").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/{name}", DeleteAsync).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a Data Source").RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> ListAsync(string? @namespace, DataSourceManagementService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var values = (await service.ListAsync(cancellationToken)).Select(value => value.Value);
        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            var parsed = ResourceNamespace.Parse(@namespace);
            values = values.Where(value => value.Namespace == parsed);
        }
        return Results.Ok(values.ToArray());
    });

    private static Task<IResult> GetAsync(string name, string? @namespace, string? scopeRef,
        HttpResponse response, DataSourceManagementService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Resource(await service.GetAsync(ResourceNamespace.Parse(@namespace), name,
            ParseScope(scopeRef), cancellationToken) ?? throw NotFound(name, @namespace), response, 200));

    private static Task<IResult> ReadinessAsync(string name, string? @namespace, string? scopeRef,
        DataSourceManagementService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Results.Ok(await service.GetReadinessAsync(ResourceNamespace.Parse(@namespace), name,
                ParseScope(scopeRef), cancellationToken)));

    private static Task<IResult> CreateAsync(CreateDataSourceRequest body, HttpResponse response,
        DataSourceManagementService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var ns = ResourceNamespace.Parse(body.Namespace);
        var stored = await service.CreateAsync(new DataSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSource,
            Metadata = new ResourceMetadata { Name = body.Name, Namespace = ns },
            ScopeRef = body.ScopeRef,
            Definition = body.Properties
        }, cancellationToken);
        response.Headers.Location = Path(stored.Value);
        return Resource(stored, response, 201);
    });

    private static Task<IResult> PutAsync(string name, string? @namespace, string? scopeRef,
        PutDataSourceRequest body, HttpRequest request, HttpResponse response,
        DataSourceManagementService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Resource(await service.PutAsync(ResourceNamespace.Parse(@namespace), name, ParseScope(scopeRef),
                body.Properties, request.Headers.IfMatch.FirstOrDefault(), cancellationToken), response, 200));

    private static Task<IResult> DeleteAsync(string name, string? @namespace, string? scopeRef,
        HttpRequest request, DataSourceManagementService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(ResourceNamespace.Parse(@namespace), name, ParseScope(scopeRef),
            request.Headers.IfMatch.FirstOrDefault(), cancellationToken);
        return Results.NoContent();
    });

    private static string Path(DataSourceResource value) =>
        $"/api/datasources/{Uri.EscapeDataString(value.Name)}?namespace={Uri.EscapeDataString(value.Namespace.Value)}&scopeRef={Uri.EscapeDataString(value.ScopeRef!.Value.Value)}";
    private static ResourceScopeRef? ParseScope(string? value) => string.IsNullOrWhiteSpace(value) ? null : ResourceScopeRef.Parse(value);
    private static ResourceNotFoundException NotFound(string name, string? ns) =>
        new(new ResourceKey(DataSourceResourceKinds.DataSource, name, ResourceNamespace.Parse(ns)));
    private static IResult Resource<T>(StoredResource<T> value, HttpResponse response, int status) where T : Resource
    { response.Headers.ETag = value.ETag; return Results.Json(value.Value, statusCode: status); }
    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourceNotFoundException exception) { return Problem("data-source-not-found", "Data Source not found", 404, exception.Message); }
        catch (ResourceConcurrencyException exception) { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (ResourceScopeAccessDeniedException exception) { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (Exception exception) when (exception is DataSourceValidationException or ResourceScopePolicyException
            or ResourceReferenceAmbiguousException or ResourceReferenceOutsideScopeException or ArgumentException or FormatException)
        { return Problem(exception is DataSourceValidationException validation ? validation.Code : "data-source-invalid", "Invalid Data Source", 422, exception.Message); }
    }
    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    { Type = $"https://agentstration.dev/problems/{type}", Title = title, Status = status, Detail = detail });
}

internal static class DataSourceProfileEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync).Produces<IEnumerable<DataSourceProfileResource>>().WithSummary("List Data Source Profiles").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{name}", GetAsync).Produces<DataSourceProfileResource>().WithSummary("Get a Data Source Profile").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{name}/revisions", ListRevisionsAsync).Produces<IEnumerable<DataSourceProfileRevisionResource>>().WithSummary("List Data Source Profile revisions").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapGet("/{name}/revisions/{version}", GetRevisionAsync).Produces<DataSourceProfileRevisionResource>().WithSummary("Get a Data Source Profile revision").RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/", CreateAsync).Produces<DataSourceProfileResource>(StatusCodes.Status201Created).WithSummary("Create a Data Source Profile").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPut("/{name}", PutAsync).Produces<DataSourceProfileResource>().WithSummary("Update a Data Source Profile draft").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPost("/{name}/revisions", PublishAsync).Produces<DataSourceProfileRevisionResource>(StatusCodes.Status201Created).WithSummary("Publish a Data Source Profile revision").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapPut("/{name}/active-revision", ActivateAsync).Produces<DataSourceProfileResource>().WithSummary("Activate a Data Source Profile revision").RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapDelete("/{name}", DeleteAsync).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a Data Source Profile").RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static Task<IResult> ListAsync(string? @namespace, DataSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var values = (await service.ListAsync(cancellationToken)).Select(value => value.Value);
        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            var parsed = ResourceNamespace.Parse(@namespace);
            values = values.Where(value => value.Namespace == parsed);
        }
        return Results.Ok(values.ToArray());
    });

    private static Task<IResult> GetAsync(string name, string? @namespace, string? scopeRef,
        HttpResponse response, DataSourceProfileService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Resource(await service.GetAsync(ResourceNamespace.Parse(@namespace), name,
            ParseScope(scopeRef), cancellationToken) ?? throw NotFound(name, @namespace), response, 200));

    private static Task<IResult> ListRevisionsAsync(string name, string? @namespace, string? scopeRef,
        DataSourceProfileService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Results.Ok((await service.ListRevisionsAsync(ResourceNamespace.Parse(@namespace), name,
                ParseScope(scopeRef), cancellationToken)).Select(value => value.Value).ToArray()));

    private static Task<IResult> GetRevisionAsync(string name, string version, string? @namespace, string? scopeRef,
        HttpResponse response, DataSourceProfileService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Resource(await service.GetRevisionAsync(ResourceNamespace.Parse(@namespace), name, version,
                ParseScope(scopeRef), cancellationToken) ?? throw NotFound(name, @namespace), response, 200));

    private static Task<IResult> CreateAsync(CreateDataSourceProfileRequest body, HttpResponse response,
        DataSourceProfileService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var ns = ResourceNamespace.Parse(body.Namespace);
        var stored = await service.CreateAsync(new DataSourceProfileResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSourceProfile,
            Metadata = new ResourceMetadata { Name = body.Name, Namespace = ns },
            ScopeRef = body.ScopeRef,
            Definition = body.Properties
        }, cancellationToken);
        response.Headers.Location = Path(stored.Value);
        return Resource(stored, response, 201);
    });

    private static Task<IResult> PutAsync(string name, string? @namespace, string? scopeRef,
        PutDataSourceProfileRequest body, HttpRequest request, HttpResponse response,
        DataSourceProfileService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
            Resource(await service.PutAsync(ResourceNamespace.Parse(@namespace), name, ParseScope(scopeRef),
                body.Properties, request.Headers.IfMatch.FirstOrDefault(), cancellationToken), response, 200));

    private static Task<IResult> PublishAsync(string name, string? @namespace, string? scopeRef,
        PublishDataSourceProfileRequest body, HttpResponse response, DataSourceProfileService service,
        CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(await service.PublishAsync(
            ResourceNamespace.Parse(@namespace), name, ParseScope(scopeRef), body, cancellationToken), response, 201));

    private static Task<IResult> ActivateAsync(string name, string? @namespace, string? scopeRef,
        ActivateDataSourceProfileRequest body, HttpRequest request, HttpResponse response,
        DataSourceProfileService service, CancellationToken cancellationToken) => ExecuteAsync(async () => Resource(
            await service.ActivateAsync(ResourceNamespace.Parse(@namespace), name, ParseScope(scopeRef), body,
                request.Headers.IfMatch.FirstOrDefault(), cancellationToken), response, 200));

    private static Task<IResult> DeleteAsync(string name, string? @namespace, string? scopeRef,
        HttpRequest request, DataSourceProfileService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        await service.DeleteAsync(ResourceNamespace.Parse(@namespace), name, ParseScope(scopeRef),
            request.Headers.IfMatch.FirstOrDefault(), cancellationToken);
        return Results.NoContent();
    });

    private static string Path(DataSourceProfileResource value) =>
        $"/api/datasourceprofiles/{Uri.EscapeDataString(value.Name)}?namespace={Uri.EscapeDataString(value.Namespace.Value)}&scopeRef={Uri.EscapeDataString(value.ScopeRef!.Value.Value)}";
    private static ResourceScopeRef? ParseScope(string? value) => string.IsNullOrWhiteSpace(value) ? null : ResourceScopeRef.Parse(value);
    private static ResourceNotFoundException NotFound(string name, string? ns) =>
        new(new ResourceKey(DataSourceResourceKinds.DataSourceProfile, name, ResourceNamespace.Parse(ns)));
    private static IResult Resource<T>(StoredResource<T> value, HttpResponse response, int status) where T : Resource
    { response.Headers.ETag = value.ETag; return Results.Json(value.Value, statusCode: status); }
    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourceNotFoundException exception) { return Problem("data-source-profile-not-found", "Data Source Profile not found", 404, exception.Message); }
        catch (ResourceConcurrencyException exception) { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (ResourceScopeAccessDeniedException exception) { return Problem("resource-scope-access-denied", "Resource scope access denied", 403, exception.Message); }
        catch (DataSourceValidationException exception)
        { return Problem(exception.Code, "Invalid Data Source Profile", exception.Code.EndsWith("_in_use", StringComparison.Ordinal) ? 409 : 422, exception.Message); }
        catch (Exception exception) when (exception is ResourceScopePolicyException or ResourceReferenceAmbiguousException
            or ResourceReferenceOutsideScopeException or ArgumentException or FormatException)
        { return Problem("data-source-profile-invalid", "Invalid Data Source Profile", 422, exception.Message); }
    }
    private static IResult Problem(string type, string title, int status, string detail) => Results.Problem(new ProblemDetails
    { Type = $"https://agentstration.dev/problems/{type}", Title = title, Status = status, Detail = detail });
}
