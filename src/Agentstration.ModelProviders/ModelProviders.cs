using Microsoft.Extensions.AI;

namespace Agentstration.ModelProviders;

public interface IModelProvider
{
    string ProviderType { get; }
    IChatClient CreateChatClient(string model);
}

public interface IModelProviderResolver
{
    IModelProvider GetRequiredProvider(string providerType);
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
            : throw new InvalidOperationException($"Model provider '{providerType}' is not registered.");
    }
}
