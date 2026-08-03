using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentstration.ModelProviders.Ollama;

public sealed class OllamaModelProvider(
    IChatClient chatClient,
    IOptions<OllamaModelProviderOptions> options,
    ILogger<OllamaModelProvider> logger) : IModelProvider, IModelProviderOptionsValidator
{
    public const string ProviderTypeName = "ollama";
    private readonly OllamaModelProviderOptions options = options.Value;
    public string ProviderType => ProviderTypeName;

    public void Validate(IReadOnlyDictionary<string, System.Text.Json.JsonElement> providerOptions) =>
        _ = OllamaModelOptionsParser.Parse(providerOptions);

    public IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(deployment);
        if (!string.Equals(provider.ProviderType, ProviderTypeName, StringComparison.OrdinalIgnoreCase))
            throw new ModelProviderConfigurationException($"Provider configuration '{provider.Name}' is not an Ollama provider.");
        if (!string.Equals(provider.Endpoint.AbsoluteUri.TrimEnd('/'), options.Endpoint.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new ModelProviderConfigurationException($"Ollama provider '{provider.Name}' does not match the endpoint configured for this host.");
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
        return new OllamaDeploymentChatClient(chatClient, deployment.ModelName, modelOptions);
    }
}
