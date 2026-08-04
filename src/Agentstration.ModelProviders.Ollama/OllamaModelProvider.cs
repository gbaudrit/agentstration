using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agentstration.ModelProviders.Ollama;

public sealed class OllamaModelProvider(
    IOllamaClientFactory clients,
    ILogger<OllamaModelProvider> logger) : IModelProvider, IModelProviderOptionsValidator
{
    public const string ProviderTypeName = "ollama";
    public string ProviderType => ProviderTypeName;

    public void Validate(IReadOnlyDictionary<string, System.Text.Json.JsonElement> providerOptions) =>
        _ = OllamaModelOptionsParser.Parse(providerOptions);

    public IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        if (!string.Equals(provider.ProviderType, ProviderTypeName, StringComparison.OrdinalIgnoreCase))
            throw new ModelProviderConfigurationException($"Provider configuration '{provider.Name}' is not an Ollama provider.");
        if (string.IsNullOrWhiteSpace(deployment.ModelName))
            throw new ModelProviderConfigurationException($"Ollama deployment '{deployment.Name}' must specify a model name.");
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Created chat client for provider {ProviderType}/{ProviderName}, deployment {Deployment}, and model {Model}",
                ProviderTypeName,
                provider.Name,
                deployment.Name,
                deployment.ModelName);
        }
        var modelOptions = OllamaModelOptionsParser.Parse(deployment.ProviderOptions);
        return new OllamaDeploymentChatClient(clients.CreateChatClient(provider, deployment.ModelName), deployment.ModelName, modelOptions);
    }
}
