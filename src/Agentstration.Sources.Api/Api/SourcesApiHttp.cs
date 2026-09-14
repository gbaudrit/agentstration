using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Sources;
using Agentstration.Sources.Contracts;

namespace Agentstration.Web.Api.Management;

internal static class SourcesApiHttp
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource_not_found", detail: exception.Message); }
        catch (ResourceConcurrencyException exception) { return Results.Problem(statusCode: 409, title: "resource-version-conflict", detail: exception.Message); }
        catch (ResourceReferenceOutsideScopeException exception) { return Results.Problem(statusCode: 400, title: "resource_reference_outside_scope", detail: exception.Message); }
        catch (ResourceReferenceAmbiguousException exception) { return Results.Problem(statusCode: 400, title: "resource_reference_ambiguous", detail: exception.Message); }
        catch (ResourceScopeAccessDeniedException exception) { return Results.Problem(statusCode: 403, title: "resource_scope_access_denied", detail: exception.Message); }
        catch (ResourceScopePolicyException exception) { return Results.Problem(statusCode: 422, title: "resource_scope_invalid", detail: exception.Message); }
        catch (SourceProviderNotFoundException exception) { return Results.Problem(statusCode: 404, title: "source-provider-not-found", detail: exception.Message); }
        catch (SourceProviderInUseException exception) { return Results.Problem(statusCode: 409, title: "source-provider-in-use", detail: exception.Message, extensions: new Dictionary<string, object?> { ["references"] = exception.Usages }); }
        catch (SourceProviderValidationException exception) { return Results.Problem(statusCode: 422, title: "source-provider-invalid", detail: exception.Message); }
        catch (SourceRegistryNotFoundException exception) { return Results.Problem(statusCode: 404, title: "source-registry-not-found", detail: exception.Message); }
        catch (SourceRegistryOperationException exception)
        {
            var status = exception.Code == "source_registry_disabled" ? 409 : exception.Unavailable ? 503 : 422;
            return Results.Problem(statusCode: status, title: exception.Code.Replace('_', '-'), detail: exception.Message);
        }
        catch (SourceValidationException exception) { return Results.Problem(statusCode: 422, title: exception.Code.Replace('_', '-'), detail: exception.Message); }
        catch (SourceVersionConflictException exception) { return Results.Problem(statusCode: 409, title: "source-version-conflict", detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation-failed", detail: exception.Message); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: 409, title: "operation-conflict", detail: exception.Message); }
    }

    public static IResult ResourceResult<T>(StoredResource<T> stored, HttpResponse response, int statusCode) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();
    public static ResourceNamespace Namespace(string? value) => ResourceNamespace.Parse(value);

    public static void RequireApiVersion(HttpRequest request)
    {
        if (request.Query.TryGetValue("api-version", out var version)
            && !string.Equals(version, ResourceApiVersions.CoreV1, StringComparison.Ordinal))
            throw new SourceValidationException("api_version_not_supported", $"Only api-version={ResourceApiVersions.CoreV1} is supported.");
    }
}
