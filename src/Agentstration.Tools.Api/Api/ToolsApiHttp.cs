using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Web.Api.Models;

internal static class ToolsApiHttp
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ResourceNotFoundException exception) { return Problem("tool-resource-not-found", "Resource not found", 404, exception.Message); }
        catch (ResourceConcurrencyException exception) { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (ToolResourceValidationException exception) { return Problem("tool-resource-invalid", "Invalid tool resource", 422, exception.Message); }
        catch (ToolDefinitionValidationException exception) { return Problem(exception.Code, "Invalid ToolDefinition", 422, exception.Message); }
        catch (ToolExecutionHookValidationException exception) { return Problem("tool-execution-hook-invalid", "Invalid Tool execution hook", 422, exception.Message); }
        catch (ToolProviderDiscoveryFailedException exception) { return Problem("tool-provider-unavailable", "Tool provider unavailable", 503, exception.Message); }
        catch (ArgumentException exception) { return Problem("validation-failed", "Invalid request", 400, exception.Message); }
    }

    public static IResult ResourceResult<T>(StoredResource<T> stored, HttpResponse response, int statusCode) where T : Resource
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();
    public static ResourceNamespace Namespace(string? value) => ResourceNamespace.Parse(value);

    private static IResult Problem(string type, string title, int status, string detail)
    {
        return Results.Problem(new ProblemDetails
        {
            Type = ProblemBase + type,
            Title = title,
            Status = status,
            Detail = detail
        });
    }
}

internal interface IModelManagementEndpoint
{
    static abstract void Map(RouteGroupBuilder group);
}
