using Agentstration.Parameters;
using Agentstration.Parameters.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;

namespace Agentstration.Web.Api.Models;

internal static class ParameterEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var parameters = endpoints.MapGroup("/api/parameters");
        parameters.MapGet("/", async (ParameterManagementService service, CancellationToken token) =>
            Results.Ok((await service.ListAsync(token)).Select(value => value.Value)))
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        parameters.MapGet("/{name}", async (string name, string? scopeRef, HttpResponse response,
            ParameterManagementService service, CancellationToken token) => await Execute(async () =>
            {
                var scope = Scope(scopeRef);
                var stored = scope is null
                    ? await service.GetAsync(name, token)
                    : await service.GetExactAsync(scope.Value, name, token);
                if (stored is null) throw new ParameterResourceNotFoundException(name);
                response.Headers.ETag = stored.ETag;
                return Results.Ok(stored.Value);
            })).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        parameters.MapGet("/{name}/usages", async (string name, string scopeRef,
            ParameterManagementService service, CancellationToken token) => await Execute(async () =>
            {
                var usages = (await service.GetUsagesAsync(ResourceScopeRef.Parse(scopeRef), name, token))
                    .Select(value => new ParameterUsageResponse(value.Kind, value.Name, value.DisplayName, value.Url)).ToArray();
                return Results.Ok(new ParameterUsagesResponse(usages, usages.Length));
            })).RequireAuthorization(AgentstrationPolicies.CanReadResources);
        parameters.MapPost("/", async (CreateParameterRequest body, HttpResponse response,
            ParameterManagementService service, CancellationToken token) => await Execute(async () => Resource(
                await service.CreateAsync(new ParameterResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ParameterResourceKinds.Parameter,
                    Metadata = new() { Name = body.Name },
                    ScopeRef = body.ScopeRef,
                    Definition = body.Properties
                }, token), response, 201))).RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        parameters.MapPut("/{name}", async (string name, string? scopeRef, PutParameterRequest body,
            HttpRequest request, HttpResponse response, ParameterManagementService service, CancellationToken token) =>
            await Execute(async () => Resource(Scope(scopeRef) is { } scope
                ? await service.PutExactAsync(scope, name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token)
                : await service.PutAsync(name, body.Properties, request.Headers.IfMatch.FirstOrDefault(), token), response, 200)))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        parameters.MapDelete("/{name}", async (string name, string? scopeRef, HttpRequest request,
            ParameterManagementService service, CancellationToken token) => await Execute(async () =>
            {
                if (Scope(scopeRef) is { } scope)
                    await service.DeleteExactAsync(scope, name, request.Headers.IfMatch.FirstOrDefault(), token);
                else
                    await service.DeleteAsync(name, request.Headers.IfMatch.FirstOrDefault(), token);
                return Results.NoContent();
            })).RequireAuthorization(AgentstrationPolicies.CanDeleteResources);
    }

    private static ResourceScopeRef? Scope(string? value) => string.IsNullOrWhiteSpace(value) ? null : ResourceScopeRef.Parse(value);
    private static IResult Resource<T>(StoredResource<T> stored, HttpResponse response, int status) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: status);
    }
    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ParameterResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "Parameter not found", detail: exception.Message); }
        catch (ParameterInUseException exception) { return Results.Problem(statusCode: 409, title: "Parameter in use", detail: exception.Message); }
        catch (ResourceConcurrencyException exception) { return Results.Problem(statusCode: 409, title: "Resource version conflict", detail: exception.Message); }
        catch (ResourceScopeAccessDeniedException exception) { return Results.Problem(statusCode: 403, title: "Resource scope access denied", detail: exception.Message); }
        catch (ResourceScopePolicyException exception) { return Results.Problem(statusCode: 422, title: "Invalid resource scope", detail: exception.Message); }
        catch (InvalidDescendantUseGrantException exception) { return Results.Problem(statusCode: 422, title: "Invalid use grant", detail: exception.Message); }
        catch (ParameterAccessDeniedException exception) { return Results.Problem(statusCode: 403, title: "Parameter access denied", detail: exception.Message); }
        catch (Exception exception) when (exception is ParameterManagementException or ArgumentException or InvalidOperationException or FormatException)
        { return Results.Problem(statusCode: 422, title: "Invalid Parameter operation", detail: exception.Message); }
    }
}
