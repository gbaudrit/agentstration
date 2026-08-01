using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentstration.ModelProviders.Ollama;

public sealed class OllamaModelProvider(
    IChatClient chatClient,
    IOptions<OllamaModelProviderOptions> options,
    ILogger<OllamaModelProvider> logger) : IModelProvider
{
    public const string ProviderTypeName = "ollama";
    private readonly OllamaModelProviderOptions options = options.Value;
    public string ProviderType => ProviderTypeName;

    public IChatClient CreateChatClient(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!string.Equals(model, options.DefaultModel, StringComparison.Ordinal))
            throw new InvalidOperationException($"Ollama model '{model}' is not configured in this host. The available development model is '{options.DefaultModel}'.");
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Resolved model provider {ProviderType} with model {Model}", ProviderTypeName, model);
        }
        return chatClient;
    }
}
