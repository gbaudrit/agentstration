using Agentstration.Aep.Client;
using Agentstration.Aep.MicrosoftExtensionsAI;
using Microsoft.Extensions.AI;

namespace Agentstration.ModelProviders;

public sealed class AepModelProvider(IHttpClientFactory httpClients) : IModelProvider, IModelProviderOptionsValidator, IModelProviderDiscovery
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

    private AepClient CreateClient(ModelProviderConfiguration provider)
    {
        var client = httpClients.CreateClient("agentstration-aep");
        client.BaseAddress = provider.Endpoint;
        return new AepClient(client);
    }
}
