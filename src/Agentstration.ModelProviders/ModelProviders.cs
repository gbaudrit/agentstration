using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.ModelProviders;

public sealed record ModelProviderConfiguration
{
    public required Guid Uid { get; init; }
    public ResourceNamespace Namespace { get; init; } = ResourceNamespace.Default;
    public required string Name { get; init; }
    public required string AdapterType { get; init; }
    public required string ContributionId { get; init; }
    public required ResourceReference Extension { get; init; }
    public required Uri Endpoint { get; init; }
    public bool ExtensionEnabled { get; init; } = true;
    public string? ExpectedExtensionId { get; init; }
    public string? DisplayName { get; init; }
    public ExtensionRegistrationSource RegistrationSource { get; init; } = ExtensionRegistrationSource.Manual;
    public string? EndpointDisplayName { get; init; }
    public ResourceReference? Credential { get; init; }
}

public sealed record ModelDeploymentConfiguration
{
    public required string Name { get; init; }
    public required string ProviderName { get; init; }
    public ResourceNamespace ProviderNamespace { get; init; } = ResourceNamespace.Default;
    public required string ModelName { get; init; }
    public IReadOnlyDictionary<string, VersionedExtensionOptions> ProviderOptions { get; init; } = new Dictionary<string, VersionedExtensionOptions>();
}

public sealed record ModelProfileConfiguration
{
    public required string Name { get; init; }
    public required string DeploymentName { get; init; }
    public ModelGenerationOptions Generation { get; init; } = new();
    public ModelReasoningOptions Reasoning { get; init; } = new();
    public ModelOutputOptions Output { get; init; } = new();
    public IReadOnlyDictionary<string, VersionedExtensionOptions> ProviderOptions { get; init; } = new Dictionary<string, VersionedExtensionOptions>();
}

public sealed record ModelChatClientMetadata(
    string ModelProfile,
    string Deployment,
    string ContributionId,
    string ProviderName,
    string ModelName,
    ModelGenerationOptions? Generation = null,
    ModelReasoningOptions? Reasoning = null,
    ModelOutputOptions? Output = null,
    IReadOnlyDictionary<string, VersionedExtensionOptions>? ProviderOptions = null,
    AgentRuntimeCapabilities? ProviderCapabilities = null,
    AgentRuntimeCapabilities? ModelCapabilities = null,
    AgentRuntimeCapabilities? AdapterCapabilities = null);

public sealed record ResolvedModelProviderCapabilities(
    AgentRuntimeCapabilities Provider,
    AgentRuntimeCapabilities Model,
    AgentRuntimeCapabilities Adapter);

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
    bool CanHandle(string providerType) => string.Equals(ProviderType, providerType, StringComparison.OrdinalIgnoreCase);
    IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment);
}

public interface IModelProviderOptionsValidator
{
    string ProviderType { get; }
    bool CanHandle(string providerType) => string.Equals(ProviderType, providerType, StringComparison.OrdinalIgnoreCase);
    void Validate(IReadOnlyDictionary<string, JsonElement> providerOptions);
}

public interface IModelProviderCapabilitiesResolver
{
    string ProviderType { get; }
    bool CanHandle(string providerType) => string.Equals(ProviderType, providerType, StringComparison.OrdinalIgnoreCase);
    ValueTask<ResolvedModelProviderCapabilities> ResolveCapabilitiesAsync(
        ModelProviderConfiguration provider,
        ModelDeploymentConfiguration deployment,
        CancellationToken cancellationToken = default);
}

public interface IModelProviderResolver
{
    IModelProvider GetRequiredProvider(string providerType);
}

public interface IModelProfileStore
{
    ValueTask<ModelProfileConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default);
    ValueTask<ModelProfileConfiguration> GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        @namespace.IsDefault
            ? GetRequiredAsync(name, cancellationToken)
            : ValueTask.FromException<ModelProfileConfiguration>(new ModelProfileNotFoundException($"{@namespace}/{name}"));
}

public interface IModelDeploymentStore
{
    ValueTask<ModelDeploymentConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default);
    ValueTask<ModelDeploymentConfiguration> GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        @namespace.IsDefault
            ? GetRequiredAsync(name, cancellationToken)
            : ValueTask.FromException<ModelDeploymentConfiguration>(new ModelDeploymentNotFoundException($"{@namespace}/{name}"));
}

public interface IModelProviderConfigurationStore
{
    ValueTask<ModelProviderConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default);
    ValueTask<ModelProviderConfiguration> GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
        @namespace.IsDefault
            ? GetRequiredAsync(name, cancellationToken)
            : ValueTask.FromException<ModelProviderConfiguration>(new ModelProviderConfigurationNotFoundException($"{@namespace}/{name}"));
    ValueTask<IReadOnlyList<ModelProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default);
}

public interface IModelProviderDiscovery
{
    string ProviderType { get; }
    bool CanHandle(string providerType) => string.Equals(ProviderType, providerType, StringComparison.OrdinalIgnoreCase);
    ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default);
}

public interface IChatClientResolver
{
    ValueTask<IChatClient> ResolveAsync(string modelProfileName, CancellationToken cancellationToken = default);
    ValueTask<IChatClient> ResolveAsync(ResourceNamespace @namespace, string modelProfileName, CancellationToken cancellationToken = default) =>
        @namespace.IsDefault
            ? ResolveAsync(modelProfileName, cancellationToken)
            : ValueTask.FromException<IChatClient>(new ModelProfileNotFoundException($"{@namespace}/{modelProfileName}"));
}

public sealed class ModelProviderResolver(IEnumerable<IModelProvider> providers) : IModelProviderResolver
{
    private readonly IReadOnlyDictionary<string, IModelProvider> providersByType = providers.ToDictionary(
        provider => provider.ProviderType,
        StringComparer.OrdinalIgnoreCase);

    public IModelProvider GetRequiredProvider(string providerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        if (providersByType.TryGetValue(providerType, out var provider)) return provider;
        provider = providersByType.Values.SingleOrDefault(value => value.CanHandle(providerType));
        return provider ?? throw new ModelProviderNotFoundException(providerType);
    }
}

public abstract class ModelProviderResolutionException(string message) : Exception(message);
public sealed class ModelProfileNotFoundException(string name) : ModelProviderResolutionException($"Model profile '{name}' was not found.");
public sealed class ModelDeploymentNotFoundException(string name) : ModelProviderResolutionException($"Model deployment '{name}' was not found.");
public sealed class ModelProviderConfigurationNotFoundException(string name) : ModelProviderResolutionException($"Model provider configuration '{name}' was not found.");
public sealed class ModelProviderNotFoundException(string providerType) : ModelProviderResolutionException($"Model provider implementation '{providerType}' is not registered.");
public sealed class ModelProviderConfigurationException(string message) : ModelProviderResolutionException(message);
