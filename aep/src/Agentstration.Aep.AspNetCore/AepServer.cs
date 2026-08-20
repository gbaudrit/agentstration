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

public interface IAepMemoryProvider
{
    AepMemoryProviderDescriptor Descriptor { get; }
    Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AepProviderHealth("available"));
    Task WriteAsync(AepMemoryRecord record, CancellationToken cancellationToken);
    Task<AepMemoryRecord?> GetAsync(AepMemoryRecordRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<AepMemoryRecord>> ListAsync(AepMemoryListRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(AepMemoryRecordRequest request, CancellationToken cancellationToken);
    Task<int> ClearScopeAsync(AepMemoryScopeRequest request, CancellationToken cancellationToken);
    Task<int> PurgeExpiredAsync(AepMemoryPurgeRequest request, CancellationToken cancellationToken);
}

public sealed class AepExtensionOptions
{
    public AepExtensionIdentity Extension { get; set; } = new("agentstration.extension", "Agentstration extension", "1.0.0");
    public IDictionary<string, AepCapabilityDescriptor> Capabilities { get; } = new Dictionary<string, AepCapabilityDescriptor>(StringComparer.Ordinal);
    public IList<AepMcpServerDescriptor> McpServers { get; } = [];
    public IList<AepToolContribution> Tools { get; } = [];
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

    public static IServiceCollection AddMemoryProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IAepMemoryProvider => services.AddSingleton<IAepMemoryProvider, TProvider>();

    public static IEndpointRouteBuilder MapAgentstrationAep(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(AepProtocol.DiscoveryPath, (IOptions<AepExtensionOptions> options, IEnumerable<IAepModelProvider> providers, IEnumerable<IAepMemoryProvider> memories) =>
            Results.Json(CreateManifest(options.Value, providers, memories), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.LegacyDiscoveryPath, (IOptions<AepExtensionOptions> options, IEnumerable<IAepModelProvider> providers, IEnumerable<IAepMemoryProvider> memories) =>
            Results.Json(CreateManifest(options.Value, providers, memories), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.HealthPath, () => Results.Json(new AepHealth("available"), AepProtocol.JsonOptions));
        endpoints.MapGet(AepProtocol.ModelProvidersPath, (IEnumerable<IAepModelProvider> providers) =>
            Results.Json(providers.Select(value => value.Descriptor).ToArray(), AepProtocol.JsonOptions));
        endpoints.MapPost($"{AepProtocol.ModelProvidersPath}/{{providerId}}/chat", ChatAsync);
        endpoints.MapPost($"{AepProtocol.ModelProvidersPath}/{{providerId}}/chat/stream", StreamAsync);
        endpoints.MapGet($"{AepProtocol.ModelProvidersPath}/{{providerId}}/models", ListModelsAsync);
        endpoints.MapGet($"{AepProtocol.ModelProvidersPath}/{{providerId}}/health", ProviderHealthAsync);
        endpoints.MapGet(AepProtocol.MemoryProvidersPath, (IEnumerable<IAepMemoryProvider> providers) =>
            Results.Json(providers.Select(value => value.Descriptor).ToArray(), AepProtocol.JsonOptions));
        endpoints.MapGet($"{AepProtocol.MemoryProvidersPath}/{{providerId}}/health", MemoryHealthAsync);
        endpoints.MapPost($"{AepProtocol.MemoryProvidersPath}/{{providerId}}/records", WriteMemoryAsync);
        endpoints.MapPost($"{AepProtocol.MemoryProvidersPath}/{{providerId}}/records/get", GetMemoryAsync);
        endpoints.MapPost($"{AepProtocol.MemoryProvidersPath}/{{providerId}}/records/query", ListMemoryAsync);
        endpoints.MapPost($"{AepProtocol.MemoryProvidersPath}/{{providerId}}/records/delete", DeleteMemoryAsync);
        endpoints.MapPost($"{AepProtocol.MemoryProvidersPath}/{{providerId}}/records/clear", ClearMemoryAsync);
        endpoints.MapPost($"{AepProtocol.MemoryProvidersPath}/{{providerId}}/records/purge", PurgeMemoryAsync);
        endpoints.MapHealthChecks("/health");
        return endpoints;
    }

    public static IEndpointRouteBuilder MapAep(this IEndpointRouteBuilder endpoints) => endpoints.MapAgentstrationAep();

    private static AepManifest CreateManifest(AepExtensionOptions options, IEnumerable<IAepModelProvider> providers, IEnumerable<IAepMemoryProvider> memories)
    {
        var modelProviders = providers.Select(value => value.Descriptor).ToArray();
        var memoryProviders = memories.Select(value => value.Descriptor).ToArray();
        var capabilities = new Dictionary<string, AepCapabilityDescriptor>(options.Capabilities, StringComparer.Ordinal)
        {
            [AepCapabilityNames.Health] = new("1.0", AepProtocol.HealthPath)
        };
        if (modelProviders.Length > 0) capabilities[AepCapabilityNames.ModelProvider] = new("1.0", AepProtocol.ModelProvidersPath);
        if (memoryProviders.Length > 0) capabilities[AepCapabilityNames.MemoryProvider] = new("1.0", AepProtocol.MemoryProvidersPath);
        if (options.Tools.Count > 0) capabilities[AepCapabilityNames.Tools] = new("1.0");
        var descriptor = new AepManifest(
            AepProtocol.Version,
            options.Extension,
            capabilities,
            new AepContributions(modelProviders, options.Tools.ToArray(), memoryProviders),
            options.McpServers.Count == 0 ? null : new AepMcpDescriptor(options.McpServers.ToArray()));
        var errors = AepDescriptorValidator.Validate(descriptor);
        if (errors.Count > 0) throw new InvalidOperationException($"The AEP extension descriptor is invalid: {string.Join(" ", errors)}");
        return descriptor;
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

    private static async Task<IResult> ChatAsync(string providerId, AepChatRequest request, IEnumerable<IAepModelProvider> providers, CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null) return Error(StatusCodes.Status404NotFound, "provider_unavailable", $"Model provider '{providerId}' is not registered.");
        try { return Results.Json(await provider.ChatAsync(request, cancellationToken), AepProtocol.JsonOptions); }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
    }

    private static async Task StreamAsync(string providerId, AepChatRequest request, IEnumerable<IAepModelProvider> providers, HttpResponse response, CancellationToken cancellationToken)
    {
        var provider = Find(providers, providerId);
        if (provider is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            await response.WriteAsJsonAsync(new AepErrorResponse(new AepError("provider_unavailable", $"Model provider '{providerId}' is not registered.")), AepProtocol.JsonOptions, cancellationToken);
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

    private static IAepMemoryProvider? FindMemory(IEnumerable<IAepMemoryProvider> providers, string id) =>
        providers.FirstOrDefault(value => string.Equals(value.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase));

    private static async Task<IResult> MemoryHealthAsync(string providerId, IEnumerable<IAepMemoryProvider> providers, CancellationToken token)
    {
        var provider = FindMemory(providers, providerId);
        return provider is null ? Error(404, "provider_unavailable", $"Memory provider '{providerId}' is not registered.")
            : Results.Json(await provider.GetHealthAsync(token), AepProtocol.JsonOptions);
    }

    private static async Task<IResult> WriteMemoryAsync(string providerId, AepMemoryRecord record, IEnumerable<IAepMemoryProvider> providers, CancellationToken token)
    {
        var provider = FindMemory(providers, providerId);
        if (provider is null) return Error(404, "provider_unavailable", $"Memory provider '{providerId}' is not registered.");
        await provider.WriteAsync(record, token);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMemoryAsync(string providerId, AepMemoryRecordRequest request, IEnumerable<IAepMemoryProvider> providers, CancellationToken token)
    {
        var provider = FindMemory(providers, providerId);
        if (provider is null) return Error(404, "provider_unavailable", $"Memory provider '{providerId}' is not registered.");
        return Results.Json(new AepMemoryGetResponse(await provider.GetAsync(request, token)), AepProtocol.JsonOptions);
    }

    private static async Task<IResult> ListMemoryAsync(string providerId, AepMemoryListRequest request, IEnumerable<IAepMemoryProvider> providers, CancellationToken token)
    {
        var provider = FindMemory(providers, providerId);
        if (provider is null) return Error(404, "provider_unavailable", $"Memory provider '{providerId}' is not registered.");
        return Results.Json(new AepMemoryListResponse(await provider.ListAsync(request, token)), AepProtocol.JsonOptions);
    }

    private static async Task<IResult> DeleteMemoryAsync(string providerId, AepMemoryRecordRequest request, IEnumerable<IAepMemoryProvider> providers, CancellationToken token)
    {
        var provider = FindMemory(providers, providerId);
        if (provider is null) return Error(404, "provider_unavailable", $"Memory provider '{providerId}' is not registered.");
        return Results.Json(new AepMemoryMutationResponse(await provider.DeleteAsync(request, token) ? 1 : 0), AepProtocol.JsonOptions);
    }

    private static async Task<IResult> ClearMemoryAsync(string providerId, AepMemoryScopeRequest request, IEnumerable<IAepMemoryProvider> providers, CancellationToken token)
    {
        var provider = FindMemory(providers, providerId);
        if (provider is null) return Error(404, "provider_unavailable", $"Memory provider '{providerId}' is not registered.");
        return Results.Json(new AepMemoryMutationResponse(await provider.ClearScopeAsync(request, token)), AepProtocol.JsonOptions);
    }

    private static async Task<IResult> PurgeMemoryAsync(string providerId, AepMemoryPurgeRequest request, IEnumerable<IAepMemoryProvider> providers, CancellationToken token)
    {
        var provider = FindMemory(providers, providerId);
        if (provider is null) return Error(404, "provider_unavailable", $"Memory provider '{providerId}' is not registered.");
        return Results.Json(new AepMemoryMutationResponse(await provider.PurgeExpiredAsync(request, token)), AepProtocol.JsonOptions);
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
