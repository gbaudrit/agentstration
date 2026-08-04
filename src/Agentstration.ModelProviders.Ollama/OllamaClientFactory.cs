using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Agentstration.ModelProviders.Ollama;

public interface IOllamaClientFactory
{
    OllamaApiClient CreateApiClient(ModelProviderConfiguration provider, string? modelName = null);
    IChatClient CreateChatClient(ModelProviderConfiguration provider, string modelName);
}

public sealed class OllamaClientFactory(IHttpClientFactory httpClients) : IOllamaClientFactory
{
    public OllamaApiClient CreateApiClient(ModelProviderConfiguration provider, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var httpClient = httpClients.CreateClient("agentstration-ollama-dynamic");
        httpClient.BaseAddress = provider.Endpoint;
        return new OllamaApiClient(httpClient, modelName ?? string.Empty);
    }

    public IChatClient CreateChatClient(ModelProviderConfiguration provider, string modelName) =>
        CreateApiClient(provider, modelName);
}
