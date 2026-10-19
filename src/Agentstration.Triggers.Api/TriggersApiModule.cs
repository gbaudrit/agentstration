using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Triggers;
using Agentstration.Web.Api.Management;
using Agentstration.Web.Security;

namespace Agentstration.Triggers.Api;

public static class TriggersApiModule
{
    public static IServiceCollection AddTriggersApi(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapTriggersApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api").RequireAuthorization(AgentstrationPolicies.Authenticated);
        TriggerEndpoints.MapConfiguration(group);
        TriggerEndpoints.MapOccurrences(group);
        return endpoints;
    }
}

internal static class TriggersApiHttp
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (ResourceConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (ResourceScopePolicyException exception) { return Results.Problem(statusCode: 422, title: "resource_scope_invalid", detail: exception.Message); }
        catch (TriggerValidationException exception) { return Results.Problem(statusCode: 422, title: exception.Code, detail: exception.Message); }
        catch (TriggerExecutionException exception) { return Results.Problem(statusCode: 409, title: exception.Code, detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: 409, title: "operation_conflict", detail: exception.Message); }
    }

    public static IResult ResourceResult<T>(StoredResource<T> stored, HttpResponse response, int statusCode) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static void RequireApiVersion(HttpRequest request)
    {
        if (request.Query.TryGetValue("api-version", out var version)
            && !string.Equals(version, ResourceApiVersions.CoreV1, StringComparison.Ordinal))
            throw new TriggerValidationException("api_version_not_supported", $"Only api-version={ResourceApiVersions.CoreV1} is supported.");
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();
}
