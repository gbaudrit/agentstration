using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Management.Contracts;
using Agentstration.ModelProviders;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Web.Api.Models;

internal static class ModelManagementHttp
{
    private const string ProblemBase = "https://agentstration.dev/problems/";

    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ModelProviderResourceNotFoundException exception) { return Problem("model-provider-not-found", "Model provider not found", 404, exception.Message); }
        catch (ModelProviderInUseException exception)
        {
            return Problem("model-provider-in-use", "Model provider is in use", 409, exception.Message,
                new Dictionary<string, object?> { ["references"] = exception.Usages });
        }
        catch (ModelProviderValidationException exception) { return Problem("model-provider-invalid", "Invalid model provider", 422, exception.Message); }
        catch (ControlPlaneResourceNotFoundException exception) { return Problem("model-profile-not-found", "Resource not found", 404, exception.Message); }
        catch (ControlPlaneConcurrencyException exception) { return Problem("resource-version-conflict", "Resource version conflict", 409, exception.Message); }
        catch (RuntimeProfileInUseException exception)
        {
            return Problem("runtime-profile-in-use", "Runtime profile is in use", 409, exception.Message,
                new Dictionary<string, object?> { ["references"] = exception.Usages });
        }
        catch (RuntimeProfileValidationException exception) { return Problem("runtime-profile-invalid", "Invalid runtime profile", 422, exception.Message); }
        catch (ToolResourceValidationException exception) { return Problem("tool-resource-invalid", "Invalid tool resource", 422, exception.Message); }
        catch (ToolProviderDiscoveryFailedException exception) { return Problem("tool-provider-unavailable", "Tool provider unavailable", 503, exception.Message); }
        catch (ModelProfileInUseException exception)
        {
            return Problem("model-profile-in-use", "Model profile is in use", 409, exception.Message, new Dictionary<string, object?>
            {
                ["profile"] = exception.ProfileName,
                ["references"] = exception.Usages
            });
        }
        catch (ModelProviderUnavailableException exception) { return Problem("model-provider-unavailable", "Model provider unavailable", 503, exception.Message); }
        catch (ModelProfileValidationException exception)
        {
            return Problem(exception.Code, "Invalid model profile", 422, exception.Message, new Dictionary<string, object?> { ["errors"] = exception.Errors });
        }
        catch (ModelProviderResolutionException exception) { return Problem("model-profile-invalid", "Invalid model configuration", 422, exception.Message); }
        catch (ArgumentException exception) { return Problem("validation-failed", "Invalid request", 400, exception.Message); }
    }

    public static IResult ResourceResult(StoredResource<ModelProfileResource> stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static IResult ResourceResult(StoredResource<ModelProviderResource> stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static IResult ResourceResult(StoredResource<RuntimeProfileResource> stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static IResult ResourceResult(StoredResource<ToolProviderResource> stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static IResult ResourceResult(StoredResource<ToolResource> stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();

    public static ModelProfileResolutionResponse Resolution(ModelProfileResolution resolution) => new(
        new ModelProfileIdentityResponse(resolution.Profile.Metadata.Name, resolution.Profile.Metadata.Name, resolution.Profile.Definition.DisplayName),
        resolution.Provider is null ? null : new ModelProviderReferenceResponse(
            resolution.Profile.Definition.Provider.Name,
            resolution.Provider.Name,
            resolution.Provider.DisplayName,
            resolution.Provider.ProviderType,
            resolution.ProviderHealth.Status),
        new ModelReferenceResponse(
            resolution.Profile.Definition.Model.Name,
            resolution.Model?.Status ?? (resolution.Status == "modelUnavailable" ? "unavailable" : "unknown"),
            resolution.Model?.Capabilities),
        new EffectiveModelOptionsResponse(
            resolution.Profile.Definition.Generation,
            resolution.Profile.Definition.Reasoning,
            resolution.Profile.Definition.Output),
        resolution.Status,
        resolution.Warnings);

    private static IResult Problem(
        string type,
        string title,
        int status,
        string detail,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Type = ProblemBase + type,
            Title = title,
            Status = status,
            Detail = detail
        };
        if (extensions is not null)
            foreach (var extension in extensions) problem.Extensions[extension.Key] = extension.Value;
        return Results.Problem(problem);
    }
}
