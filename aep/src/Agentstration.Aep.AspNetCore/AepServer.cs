using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Aep.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Aep.AspNetCore;

public interface IAepModelProvider
{
    AepModelProviderDescriptor Descriptor { get; }
    Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(AepChatRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AepModelDescriptor>>(Descriptor.Models ?? []);
    Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AepProviderHealth("available"));
}

public sealed class AepExtensionOptions
{
    public AepExtensionIdentity Extension { get; set; } = new("agentstration.extension", "Agentstration extension", "1.0.0");
    public IDictionary<string, AepCapabilityDescriptor> Capabilities { get; } = new Dictionary<string, AepCapabilityDescriptor>(StringComparer.Ordinal);
    public IList<AepMcpServerDescriptor> McpServers { get; } = [];
    public IList<AepToolContribution> Tools { get; } = [];
    public IList<AepOptionSetDescriptor> OptionSets { get; } = [];
}

public static class AepServerExtensions
{
    public static IServiceCollection AddAep(this IServiceCollection services, Action<AepExtensionOptions>? configure = null) =>
        services.AddAgentstrationAep(configure);

    public static IServiceCollection AddAgentstrationAep(this IServiceCollection services, Action<AepExtensionOptions>? configure = null)
    {
        services.AddOptions<AepExtensionOptions>();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        if (configure is not null) services.Configure(configure);
        services.AddHealthChecks();
        return services;
    }

    public static IServiceCollection AddModelProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IAepModelProvider => services.AddSingleton<IAepModelProvider, TProvider>();

    public static IEndpointRouteBuilder MapAgentstrationAep(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AepProtocol.DiscoveryPath, (IOptions<AepExtensionOptions> options, IEnumerable<IAepModelProvider> providers) =>
            Results.Json(CreateManifest(options.Value, providers), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.LegacyDiscoveryPath, (IOptions<AepExtensionOptions> options, IEnumerable<IAepModelProvider> providers) =>
            Results.Json(CreateManifest(options.Value, providers), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.HealthPath, () => Results.Json(new AepHealth("available"), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.ModelProvidersPath, (IEnumerable<IAepModelProvider> providers) =>
            Results.Json(providers.Select(value => value.Descriptor).ToArray(), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.ConfigurationPath, (IOptions<AepExtensionOptions> options) =>
            Results.Json(new AepConfigurationCatalog(options.Value.OptionSets.ToArray()), AepProtocol.JsonOptions));
        endpoints.MapPost($"{AepProtocol.ModelProvidersPath}/{{providerId}}/chat", ChatAsync);
        endpoints.MapPost($"{AepProtocol.ModelProvidersPath}/{{providerId}}/chat/stream", StreamAsync);
        endpoints.MapGet($"{AepProtocol.ModelProvidersPath}/{{providerId}}/models", ListModelsAsync);
        endpoints.MapGet($"{AepProtocol.ModelProvidersPath}/{{providerId}}/health", ProviderHealthAsync);
        endpoints.MapHealthChecks("/health");
        return endpoints;
    }

    public static IEndpointRouteBuilder MapAep(this IEndpointRouteBuilder endpoints) => endpoints.MapAgentstrationAep();

    private static AepManifest CreateManifest(AepExtensionOptions options, IEnumerable<IAepModelProvider> providers)
    {
        var modelProviders = providers.Select(value => value.Descriptor).ToArray();
        ValidateOptionSets(options.OptionSets, modelProviders);
        var capabilities = new Dictionary<string, AepCapabilityDescriptor>(options.Capabilities, StringComparer.Ordinal)
        {
            [AepCapabilityNames.Health] = new("1.0", AepProtocol.HealthPath)
        };
        if (modelProviders.Length > 0) capabilities[AepCapabilityNames.ModelProvider] = new("1.0", AepProtocol.ModelProvidersPath);
        if (options.Tools.Count > 0) capabilities[AepCapabilityNames.Tools] = new("1.0");
        if (options.OptionSets.Count > 0) capabilities[AepCapabilityNames.Configuration] = new("1.0", AepProtocol.ConfigurationPath);
        var descriptor = new AepManifest(
            AepProtocol.Version,
            options.Extension,
            capabilities,
            new AepContributions(modelProviders, options.Tools.ToArray()),
            options.McpServers.Count == 0 ? null : new AepMcpDescriptor(options.McpServers.ToArray()));
        var errors = AepDescriptorValidator.Validate(descriptor);
        if (errors.Count > 0) throw new InvalidOperationException($"The AEP extension descriptor is invalid: {string.Join(" ", errors)}");
        return descriptor;
    }

    private static void ValidateOptionSets(
        ICollection<AepOptionSetDescriptor> optionSets,
        IReadOnlyCollection<AepModelProviderDescriptor> modelProviders)
    {
        var duplicate = optionSets.GroupBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(value => value.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"AEP option set '{duplicate.Key}' is declared more than once.");
        foreach (var optionSet in optionSets)
        {
            if (string.IsNullOrWhiteSpace(optionSet.Id)) throw new InvalidOperationException("An AEP option set id is required.");
            if (!string.Equals(optionSet.ContributionKind, AepContributionKinds.ModelProvider, StringComparison.Ordinal)
                || !modelProviders.Any(value => string.Equals(value.Id, optionSet.ContributionId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"AEP option set '{optionSet.Id}' targets an unknown contribution.");
            if (optionSet.Versions.Count == 0
                || !optionSet.Versions.Any(value => string.Equals(value.Version, optionSet.PreferredVersion, StringComparison.Ordinal)))
                throw new InvalidOperationException($"AEP option set '{optionSet.Id}' must contain its preferred version.");
            if (optionSet.Versions.Select(value => value.Version).Distinct(StringComparer.Ordinal).Count() != optionSet.Versions.Count)
                throw new InvalidOperationException($"AEP option set '{optionSet.Id}' contains duplicate versions.");
            foreach (var version in optionSet.Versions)
            {
                var actual = AepSchemaDigest.Compute(version.Schema);
                if (!string.Equals(actual, version.SchemaDigest, StringComparison.Ordinal))
                    throw new InvalidOperationException($"AEP option set '{optionSet.Id}' version '{version.Version}' has an invalid schema digest.");
            }
        }
    }

    private static async Task<IResult> ProviderHealthAsync(string providerId, IEnumerable<IAepModelProvider> providers, CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "provider_unavailable", $"Model provider '{providerId}' is not registered.");
        return Results.Json(await provider.GetHealthAsync(cancellationToken), AepProtocol.JsonOptions);
    }

    private static async Task<IResult> ListModelsAsync(string providerId, IEnumerable<IAepModelProvider> providers, CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "provider_unavailable", $"Model provider '{providerId}' is not registered.");
        try { return Results.Json(await provider.ListModelsAsync(cancellationToken), AepProtocol.JsonOptions); }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
    }

    private static async Task<IResult> ChatAsync(
        string providerId,
        AepChatRequest request,
        IEnumerable<IAepModelProvider> providers,
        IOptions<AepExtensionOptions> options,
        CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "provider_unavailable", $"Model provider '{providerId}' is not registered.");
        try
        {
            ValidateNativeOptions(providerId, request.Options?.NativeOptions, options.Value.OptionSets);
            return Results.Json(await provider.ChatAsync(request, cancellationToken), AepProtocol.JsonOptions);
        }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
    }

    private static async Task StreamAsync(
        string providerId,
        AepChatRequest request,
        IEnumerable<IAepModelProvider> providers,
        IOptions<AepExtensionOptions> options,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            await response.WriteAsJsonAsync(new AepErrorResponse(new AepError("provider_unavailable", $"Model provider '{providerId}' is not registered.")), AepProtocol.JsonOptions, cancellationToken);
            return;
        }
        try { ValidateNativeOptions(providerId, request.Options?.NativeOptions, options.Value.OptionSets); }
        catch (AepServerException exception)
        {
            response.StatusCode = exception.StatusCode;
            await response.WriteAsJsonAsync(new AepErrorResponse(new AepError(exception.Code, exception.Message)), AepProtocol.JsonOptions, cancellationToken);
            return;
        }
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        try
        {
            await foreach (var update in provider.ChatStreamingAsync(request, cancellationToken).WithCancellation(cancellationToken))
            {
                await response.WriteAsync("data: ", cancellationToken);
                await JsonSerializer.SerializeAsync(response.Body, update, AepProtocol.JsonOptions, cancellationToken);
                await response.WriteAsync("\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
                if (update.FinishReason is not null) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static IAepModelProvider? Find(IEnumerable<IAepModelProvider> providers, string id) =>
        providers.FirstOrDefault(value => string.Equals(value.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));

    private static void ValidateNativeOptions(
        string providerId,
        AepVersionedOptions? nativeOptions,
        IEnumerable<AepOptionSetDescriptor> optionSets)
    {
        if (nativeOptions is null) return;
        var optionSet = optionSets.SingleOrDefault(value =>
            string.Equals(value.Id, nativeOptions.OptionSet, StringComparison.Ordinal)
            && string.Equals(value.ContributionKind, AepContributionKinds.ModelProvider, StringComparison.Ordinal)
            && string.Equals(value.ContributionId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.Scope, AepOptionScopes.ModelProfile, StringComparison.Ordinal));
        if (optionSet is null)
            throw new AepServerException("option_set_unsupported", $"Option set '{nativeOptions.OptionSet}' is not supported by model provider '{providerId}'.", StatusCodes.Status422UnprocessableEntity);
        var version = optionSet.Versions.SingleOrDefault(value => string.Equals(value.Version, nativeOptions.Version, StringComparison.Ordinal));
        if (version is null)
            throw new AepServerException("option_version_unsupported", $"Option set '{nativeOptions.OptionSet}' version '{nativeOptions.Version}' is not supported.", StatusCodes.Status422UnprocessableEntity);
        if (!string.Equals(version.SchemaDigest, nativeOptions.SchemaDigest, StringComparison.Ordinal))
            throw new AepServerException("option_schema_mismatch", $"Option set '{nativeOptions.OptionSet}' version '{nativeOptions.Version}' has an unexpected schema digest.", StatusCodes.Status422UnprocessableEntity);
        if (nativeOptions.Values.ValueKind != JsonValueKind.Object)
            throw new AepServerException("invalid_options", "Native option values must be a JSON object.", StatusCodes.Status422UnprocessableEntity);
        var issues = AepOptionSchemaValidator.Validate(nativeOptions.Values, version.Schema);
        if (issues.Count > 0)
            throw new AepServerException(
                "invalid_options",
                string.Join(" ", issues.Select(value => value.Message)),
                StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new AepErrorResponse(new AepError(code, message)), AepProtocol.JsonOptions, statusCode: status);
}

public sealed class AepServerException(string code, string message, int statusCode = StatusCodes.Status502BadGateway, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
