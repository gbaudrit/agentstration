using System.Text.Json;
using Agentstration.Management.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.ModelProviders;

public sealed record ModelProviderConfiguration
{
    public required string Name { get; init; }
    public required string ProviderType { get; init; }
    public required Uri Endpoint { get; init; }
    public string? DisplayName { get; init; }
    public string ManagementMode { get; init; } = "external";
    public string? EndpointDisplayName { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = ["chat"];
}

public sealed record ModelDeploymentConfiguration
{
    public required string Name { get; init; }
    public required string ProviderName { get; init; }
    public required string ModelName { get; init; }
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record ModelProfileConfiguration
{
    public required string Name { get; init; }
    public required string DeploymentName { get; init; }
    public ModelGenerationOptions Generation { get; init; } = new();
    public ModelReasoningOptions Reasoning { get; init; } = new();
    public ModelOutputOptions Output { get; init; } = new();
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record ModelChatClientMetadata(
    string ModelProfile,
    string Deployment,
    string ProviderType,
    string ProviderName,
    string ModelName,
    ModelGenerationOptions? Generation = null,
    ModelReasoningOptions? Reasoning = null,
    ModelOutputOptions? Output = null,
    IReadOnlyDictionary<string, JsonElement>? ProviderOptions = null);

public sealed record DiscoveredModel(
    string Name,
    string DisplayName,
    string Status,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ModelProviderHealth(string Status, string? Details = null);

public interface IModelProvider
{
    string ProviderType { get; }
    IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment);
}

public interface IModelProviderOptionsValidator
{
    string ProviderType { get; }
    void Validate(IReadOnlyDictionary<string, JsonElement> providerOptions);
}

public interface IModelProviderResolver
{
    IModelProvider GetRequiredProvider(string providerType);
}

public interface IModelProfileStore
{
    ValueTask<ModelProfileConfiguration> GetRequiredAsync(string resourceId, CancellationToken cancellationToken = default);
}

public interface IModelDeploymentStore
{
    ValueTask<ModelDeploymentConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default);
}

public interface IModelProviderConfigurationStore
{
    ValueTask<ModelProviderConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ModelProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default);
}

public interface IModelProviderDiscovery
{
    string ProviderType { get; }
    ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default);
}

public interface IChatClientResolver
{
    ValueTask<IChatClient> ResolveAsync(string modelProfileResourceId, CancellationToken cancellationToken = default);
}

public sealed class ModelProviderResolver(IEnumerable<IModelProvider> providers) : IModelProviderResolver
{
    private readonly IReadOnlyDictionary<string, IModelProvider> providersByType = providers.ToDictionary(
        provider => provider.ProviderType,
        StringComparer.OrdinalIgnoreCase);

    public IModelProvider GetRequiredProvider(string providerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        return providersByType.TryGetValue(providerType, out var provider)
            ? provider
            : throw new ModelProviderNotFoundException(providerType);
    }
}

public abstract class ModelProviderResolutionException(string message) : Exception(message);
public sealed class ModelProfileNotFoundException(string resourceId) : ModelProviderResolutionException($"Model profile '{resourceId}' was not found.");
public sealed class ModelDeploymentNotFoundException(string name) : ModelProviderResolutionException($"Model deployment '{name}' was not found.");
public sealed class ModelProviderConfigurationNotFoundException(string name) : ModelProviderResolutionException($"Model provider configuration '{name}' was not found.");
public sealed class ModelProviderNotFoundException(string providerType) : ModelProviderResolutionException($"Model provider implementation '{providerType}' is not registered.");
public sealed class ModelProviderConfigurationException(string message) : ModelProviderResolutionException(message);
