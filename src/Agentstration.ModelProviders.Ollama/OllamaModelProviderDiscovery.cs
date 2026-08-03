using Agentstration.ModelProviders;
using OllamaSharp;

namespace Agentstration.ModelProviders.Ollama;

public sealed class OllamaModelProviderDiscovery(IOllamaApiClient client) : IModelProviderDiscovery
{
    public string ProviderType => OllamaModelProvider.ProviderTypeName;

    public async ValueTask<ModelProviderHealth> GetHealthAsync(
        ModelProviderConfiguration provider,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.IsRunningAsync(cancellationToken)
                ? new ModelProviderHealth("available")
                : new ModelProviderHealth("unavailable", "Ollama did not respond to its lightweight version probe.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ModelProviderHealth("unavailable", exception.Message);
        }
    }

    public async ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(
        ModelProviderConfiguration provider,
        CancellationToken cancellationToken = default)
    {
        var models = await client.ListLocalModelsAsync(cancellationToken);
        return models.Select(model =>
        {
            var name = model.Name ?? model.ModelName ?? string.Empty;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(model.Details?.ParameterSize)) metadata["parameterSize"] = model.Details.ParameterSize;
            if (!string.IsNullOrWhiteSpace(model.Details?.QuantizationLevel)) metadata["quantization"] = model.Details.QuantizationLevel;
            return new DiscoveredModel(name, name, "available", ["chat"], metadata);
        }).Where(model => !string.IsNullOrWhiteSpace(model.Name)).ToArray();
    }
}
