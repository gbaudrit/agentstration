using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.ModelProviders;
using Agentstration.Resources;
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
        catch (ToolExecutionHookValidationException exception) { return Problem("tool-execution-hook-invalid", "Invalid Tool execution hook", 422, exception.Message); }
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

    public static IResult ResourceResult(StoredResource<ToolExecutionHookResource> stored, HttpResponse response, int statusCode)
    {
        response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Value, statusCode: statusCode);
    }

    public static string? IfMatch(HttpRequest request) => request.Headers.IfMatch.FirstOrDefault();
    public static ResourceNamespace Namespace(string? value) => ResourceNamespace.Parse(value);

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
        resolution.Warnings,
        Capabilities(resolution),
        resolution.Incompatibilities?.Select(issue => new ModelCompatibilityIssueResponse(
            issue.Capability,
            Support(issue.EffectiveSupport),
            issue.Message)).ToArray() ?? []);

    private static IReadOnlyList<ModelCapabilityResponse> Capabilities(ModelProfileResolution resolution)
    {
        if (resolution.CapabilityLevels is null || resolution.EffectiveCapabilities is null) return [];
        var levels = resolution.CapabilityLevels;
        var effective = resolution.EffectiveCapabilities;
        return
        [
            Capability("Streaming", levels.Provider.Streaming, levels.Model.Streaming, levels.Adapter.Streaming, effective.Streaming),
            Capability("Tools", levels.Provider.Tools, levels.Model.Tools, levels.Adapter.Tools, effective.Tools),
            Capability("Structured output", levels.Provider.StructuredOutput, levels.Model.StructuredOutput, levels.Adapter.StructuredOutput, effective.StructuredOutput),
            new ModelCapabilityResponse(
                "Reasoning",
                Support(levels.Provider.Reasoning.Support),
                Support(levels.Model.Reasoning.Support),
                Support(levels.Adapter.Reasoning.Support),
                Support(effective.Reasoning.Support),
                effective.Reasoning.SupportedEfforts.Order(StringComparer.OrdinalIgnoreCase).ToArray())
        ];
    }

    private static ModelCapabilityResponse Capability(
        string name,
        Agentstration.Runtime.Abstractions.FeatureCapability provider,
        Agentstration.Runtime.Abstractions.FeatureCapability model,
        Agentstration.Runtime.Abstractions.FeatureCapability adapter,
        Agentstration.Runtime.Abstractions.FeatureCapability effective) =>
        new(name, Support(provider.Support), Support(model.Support), Support(adapter.Support), Support(effective.Support), []);

    private static string Support(Agentstration.Runtime.Abstractions.CapabilitySupport support) => support.ToString().ToLowerInvariant();

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
