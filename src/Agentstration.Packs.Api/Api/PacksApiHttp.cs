using Agentstration.Packs;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Web.Api.Management;

internal static class PacksApiHttp
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (ResourceConcurrencyException exception) { return Results.Problem(statusCode: 412, title: "precondition_failed", detail: exception.Message); }
        catch (ResourceReferenceOutsideScopeException exception) { return Results.Problem(statusCode: 400, title: "resource_reference_outside_scope", detail: exception.Message); }
        catch (ResourceReferenceAmbiguousException exception) { return Results.Problem(statusCode: 400, title: "resource_reference_ambiguous", detail: exception.Message); }
        catch (ResourceScopeAccessDeniedException exception) { return Results.Problem(statusCode: 403, title: "resource_scope_access_denied", detail: exception.Message); }
        catch (ResourceScopePolicyException exception) { return Results.Problem(statusCode: 422, title: "resource_scope_invalid", detail: exception.Message); }
        catch (PackNotFoundException exception) { return Results.Problem(statusCode: 404, title: "pack_not_found", detail: exception.Message); }
        catch (KeyNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (PackValidationException exception) { return Results.Problem(statusCode: 400, title: exception.Code, detail: exception.Message); }
        catch (PackAlreadyInstalledException exception) { return Results.Problem(statusCode: 409, title: "pack_already_installed", detail: exception.Message); }
        catch (PackResourceConflictException exception) { return Results.Problem(statusCode: 409, title: "pack_resource_conflict", detail: exception.Message); }
        catch (PackResourceModifiedException exception) { return Results.Problem(statusCode: 409, title: "pack_resource_modified", detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation_failed", detail: exception.Message); }
        catch (InvalidDataException exception) { return Results.Problem(statusCode: 400, title: "pack_archive_invalid", detail: exception.Message); }
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
            throw new PackValidationException("api_version_not_supported", $"Only api-version={ResourceApiVersions.CoreV1} is supported.");
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();
}

internal interface IManagementEndpoint
{
    static abstract void Map(RouteGroupBuilder group);
}
