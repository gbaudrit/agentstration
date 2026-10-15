using Agentstration.Api.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Security;

namespace Agentstration.Schemas.Api;

internal static class SchemaEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var schemas = endpoints.MapGroup("/api/schemas");
        schemas.MapGet("/", ListAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        schemas.MapGet("/formats", ListFormatsAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        schemas.MapPost("/formats/validate-definition", ValidateDefinitionAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        schemas.MapPost("/formats/validate-instance", ValidateInstanceAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        schemas.MapGet("/{name}", GetAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        schemas.MapPost("/", CreateAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        schemas.MapPut("/{name}", PutAsync).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        schemas.MapGet("/{name}/versions", ListVersionsAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        schemas.MapGet("/{name}/versions/{version}", GetVersionAsync)
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        schemas.MapPost("/{name}/versions", PublishAsync)
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
    }

    private static async Task<IResult> ListAsync(SchemaManagementService service, CancellationToken token) =>
        Results.Ok(new ValueResponse<SchemaResource>((await service.ListAsync(token))
            .Select(value => value.Value).ToArray()));

    private static async Task<IResult> ListFormatsAsync(
        ISchemaFormatRegistry registry,
        CancellationToken token) => Results.Ok(
        new ValueResponse<SchemaFormatDescriptor>(await registry.ListAsync(token)));

    private static Task<IResult> ValidateDefinitionAsync(ValidateSchemaDefinitionRequest body,
        ISchemaFormatRegistry registry, CancellationToken token) => ExecuteAsync(async () =>
    {
        var handler = await registry.ResolveAsync(body.Format, SchemaFormatCapabilities.ValidateDefinition,
            body.Provider, token);
        return Results.Ok(await handler.ValidateDefinitionAsync(body.Definition, body.Dialect, token));
    });

    private static Task<IResult> ValidateInstanceAsync(ValidateSchemaInstanceRequest body,
        ISchemaFormatRegistry registry, CancellationToken token) => ExecuteAsync(async () =>
    {
        var handler = await registry.ResolveAsync(body.Format, SchemaFormatCapabilities.ValidateInstance,
            body.Provider, token);
        return Results.Ok(await handler.ValidateInstanceAsync(body.Definition, body.Instance, body.Dialect, token));
    });

    private static Task<IResult> GetAsync(string name, string? resourceNamespace, string scopeRef,
        HttpResponse response, SchemaManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetExactAsync(ResourceScopeRef.Parse(scopeRef),
            ResourceNamespace.Parse(resourceNamespace), name, token)
            ?? throw new SchemaNotFoundException(ResourceNamespace.Parse(resourceNamespace), name);
        return Resource(stored, response, StatusCodes.Status200OK);
    });

    private static Task<IResult> CreateAsync(CreateSchemaRequest body, HttpResponse response,
        SchemaManagementService service, CancellationToken token) => ExecuteAsync(async () => Resource(
            await service.CreateAsync(new SchemaResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SchemaResourceKinds.Schema,
                Metadata = new()
                {
                    Name = body.Name,
                    Namespace = body.Namespace ?? ResourceNamespace.Default
                },
                ScopeRef = body.ScopeRef,
                Definition = body.Properties
            }, token), response, StatusCodes.Status201Created));

    private static Task<IResult> PutAsync(string name, string? resourceNamespace, string scopeRef,
        PutSchemaRequest body, HttpRequest request, HttpResponse response, SchemaManagementService service,
        CancellationToken token) => ExecuteAsync(async () => Resource(await service.PutAsync(
            ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(resourceNamespace), name, body.Properties,
            request.Headers.IfMatch.FirstOrDefault(), token), response, StatusCodes.Status200OK));

    private static Task<IResult> ListVersionsAsync(string name, string? resourceNamespace, string scopeRef,
        SchemaManagementService service, CancellationToken token) => ExecuteAsync(async () => Results.Ok(
            new ValueResponse<SchemaVersionResource>((await service.ListVersionsAsync(ResourceScopeRef.Parse(scopeRef),
                ResourceNamespace.Parse(resourceNamespace), name, token)).Select(value => value.Value).ToArray())));

    private static Task<IResult> GetVersionAsync(string name, string version, string? resourceNamespace,
        string scopeRef, SchemaManagementService service, CancellationToken token) => ExecuteAsync(async () =>
    {
        var stored = await service.GetVersionAsync(ResourceScopeRef.Parse(scopeRef),
            ResourceNamespace.Parse(resourceNamespace), name, version, token)
            ?? throw new SchemaNotFoundException(ResourceNamespace.Parse(resourceNamespace), $"{name}:{version}");
        return Results.Ok(stored.Value);
    });

    private static Task<IResult> PublishAsync(string name, string? resourceNamespace, string scopeRef,
        PublishSchemaVersionRequest body, HttpResponse response, SchemaManagementService service,
        CancellationToken token) => ExecuteAsync(async () => Resource(await service.PublishAsync(
            ResourceScopeRef.Parse(scopeRef), ResourceNamespace.Parse(resourceNamespace), name, body.Version, token),
            response, StatusCodes.Status201Created));

    private static IResult Resource<T>(StoredResource<T> stored, HttpResponse response, int status) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: status);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SchemaNotFoundException exception)
        { return Results.Problem(statusCode: 404, title: "Schema not found", detail: exception.Message); }
        catch (ResourceConcurrencyException exception)
        { return Results.Problem(statusCode: 409, title: "Resource version conflict", detail: exception.Message); }
        catch (ResourceScopeAccessDeniedException exception)
        { return Results.Problem(statusCode: 403, title: "Resource scope access denied", detail: exception.Message); }
        catch (SchemaFormatResolutionException exception)
        {
            return Results.Problem(statusCode: 422, title: "Schema format unavailable", detail: exception.Message,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
        catch (SchemaFormatOperationException exception)
        {
            var status = exception.Code == "schema_format_provider_timeout"
                ? StatusCodes.Status504GatewayTimeout
                : StatusCodes.Status502BadGateway;
            return Results.Problem(statusCode: status, title: "Schema format provider failed",
                detail: exception.Message,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
        catch (SchemaValidationException exception)
        {
            var extensions = new Dictionary<string, object?> { ["code"] = exception.Code };
            if (exception.Issues.Count > 0) extensions["issues"] = exception.Issues;
            return Results.Problem(statusCode: 422, title: "Invalid Schema operation", detail: exception.Message,
                extensions: extensions);
        }
        catch (Exception exception) when (exception is ResourceScopePolicyException or InvalidDescendantUseGrantException
            or ArgumentException or InvalidOperationException or FormatException)
        { return Results.Problem(statusCode: 422, title: "Invalid Schema operation", detail: exception.Message); }
    }
}
