using Agentstration.Extensions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Web.Api.Models;

internal static class ExtensionsApiHttp
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ExtensionRegistrationNotFoundException exception) { return Results.Problem(statusCode: 404, title: "extension-registration-not-found", detail: exception.Message); }
        catch (ExtensionRegistrationInUseException exception) { return Results.Problem(statusCode: 409, title: "extension-registration-in-use", detail: exception.Message); }
        catch (ExtensionRegistrationValidationException exception) { return Results.Problem(statusCode: 422, title: "extension-registration-invalid", detail: exception.Message); }
        catch (ResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "resource-not-found", detail: exception.Message); }
        catch (ResourceConcurrencyException exception) { return Results.Problem(statusCode: 409, title: "resource-version-conflict", detail: exception.Message); }
        catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "validation-failed", detail: exception.Message); }
    }

    public static IResult ResourceResult<T>(StoredResource<T> stored, HttpResponse response, int statusCode) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();
    public static ResourceNamespace Namespace(string? value) => ResourceNamespace.Parse(value);
}
