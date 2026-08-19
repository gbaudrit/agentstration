using Agentstration.Aep.Client;
using Agentstration.Aep.MicrosoftExtensionsAI;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.ModelProviders;

public sealed class AepModelProvider(IHttpClientFactory httpClients) : IModelProvider, IModelProviderOptionsValidator, IModelProviderDiscovery, IModelProviderCapabilitiesResolver
{
    public const string AdapterType = "aep";
    public string ProviderType => AdapterType;
    public bool CanHandle(string providerType) => !string.IsNullOrWhiteSpace(providerType);

    public IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        if (string.IsNullOrWhiteSpace(deployment.ModelName))
            throw new ModelProviderConfigurationException($"AEP deployment '{deployment.Name}' must specify a model name.");
        var client = httpClients.CreateClient("agentstration-aep");
        client.BaseAddress = provider.Endpoint;
        return new AepChatClient(new AepClient(client).CreateModelProvider(provider.ProviderType), deployment.ModelName, deployment.ProviderOptions);
    }

    public void Validate(IReadOnlyDictionary<string, System.Text.Json.JsonElement> providerOptions)
    {
        ArgumentNullException.ThrowIfNull(providerOptions);
    }

    public async ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient(provider);
            var descriptor = await client.DiscoverAsync(cancellationToken);
            var contribution = descriptor.Contributions.ModelProviders.FirstOrDefault(value => string.Equals(value.Id, provider.ProviderType, StringComparison.OrdinalIgnoreCase));
            if (contribution is null) return new ModelProviderHealth("unavailable", $"The extension does not contribute model provider '{provider.ProviderType}'.");
            var health = await client.CreateModelProvider(provider.ProviderType).GetHealthAsync(cancellationToken);
            return new ModelProviderHealth(health.Status, health.Details);
        }
        catch (AepProtocolException exception)
        {
            return new ModelProviderHealth(exception.Code == "protocol_incompatible" ? "incompatible" : "unavailable", exception.Message);
        }
        catch (HttpRequestException exception) { return new ModelProviderHealth("unreachable", exception.Message); }
    }

    public async ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default)
    {
        var models = await CreateClient(provider).CreateModelProvider(provider.ProviderType).ListModelsAsync(cancellationToken);
        return models.Select(value => new DiscoveredModel(
            value.Id,
            value.DisplayName,
            "available",
            value.Capabilities ?? [],
            value.Metadata ?? new Dictionary<string, string>())).ToArray();
    }

    public async ValueTask<ResolvedModelProviderCapabilities> ResolveCapabilitiesAsync(
        ModelProviderConfiguration provider,
        ModelDeploymentConfiguration deployment,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient(provider);
        var manifest = await client.DiscoverAsync(cancellationToken);
        var contribution = manifest.Contributions.ModelProviders.SingleOrDefault(
            value => string.Equals(value.Id, provider.ProviderType, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModelProviderConfigurationException($"The AEP extension does not contribute model provider '{provider.ProviderType}'.");
        var models = await client.CreateModelProvider(provider.ProviderType).ListModelsAsync(cancellationToken);
        var model = models.SingleOrDefault(value => string.Equals(value.Id, deployment.ModelName, StringComparison.Ordinal));
        if (model is null) throw new ModelProviderConfigurationException($"Model '{deployment.ModelName}' is not available from provider '{provider.Name}'.");
        return new ResolvedModelProviderCapabilities(
            Map(contribution.Capabilities),
            Map(model.Capabilities ?? []),
            new AgentRuntimeCapabilities
            {
                Streaming = new(CapabilitySupport.Native),
                Tools = new(CapabilitySupport.Native),
                StructuredOutput = new(CapabilitySupport.Native),
                Reasoning = new ReasoningCapability { Support = CapabilitySupport.Partial }
            });
    }

    private AepClient CreateClient(ModelProviderConfiguration provider)
    {
        var client = httpClients.CreateClient("agentstration-aep");
        client.BaseAddress = provider.Endpoint;
        return new AepClient(client);
    }

    private static AgentRuntimeCapabilities Map(Agentstration.Aep.Abstractions.AepModelProviderCapabilities value) => new()
    {
        Streaming = new(value.Streaming ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
        Tools = new(value.Tools ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
        StructuredOutput = new(value.StructuredOutput ? CapabilitySupport.Native : CapabilitySupport.Unsupported),
        Reasoning = new ReasoningCapability { Support = value.Thinking ? CapabilitySupport.Native : CapabilitySupport.Unsupported }
    };

    private static AgentRuntimeCapabilities Map(IReadOnlyList<string> values)
    {
        bool Has(string name) => values.Contains(name, StringComparer.OrdinalIgnoreCase);
        FeatureCapability Feature(string name) => new(Has(name) ? CapabilitySupport.Native : CapabilitySupport.Unsupported);
        return new AgentRuntimeCapabilities
        {
            Streaming = Feature("streaming"),
            Tools = Feature("tools"),
            StructuredOutput = Feature("structuredOutput"),
            Reasoning = new ReasoningCapability { Support = Has("reasoning") || Has("thinking") ? CapabilitySupport.Native : CapabilitySupport.Unsupported }
        };
    }
}
