using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Profiles;

namespace Agentstration.Web.Api.Models;

internal static class RuntimeProfileApiHttp
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourceNotFoundException exception) { return Results.Problem(statusCode: 404, title: "runtime-profile-not-found", detail: exception.Message); }
        catch (ResourceConcurrencyException exception) { return Results.Problem(statusCode: 409, title: "resource-version-conflict", detail: exception.Message); }
        catch (RuntimeProfileInUseException exception)
        {
            return Results.Problem(statusCode: 409, title: "runtime-profile-in-use", detail: exception.Message,
                extensions: new Dictionary<string, object?> { ["references"] = exception.Usages });
        }
        catch (RuntimeProfileValidationException exception) { return Results.Problem(statusCode: 422, title: "runtime-profile-invalid", detail: exception.Message); }
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
