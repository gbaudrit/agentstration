using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agentstration.ModelProviders;

internal sealed class ResolvedModelChatClient(IChatClient inner, ModelChatClientMetadata metadata) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetResponseAsync(messages, Effective(options), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in inner.GetStreamingResponseAsync(messages, Effective(options), cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ModelChatClientMetadata)
            ? metadata
            : inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The provider/DI container owns the shared underlying client lifetime.
    }

    private ChatOptions Effective(ChatOptions? options)
    {
        var effective = options?.Clone() ?? new ChatOptions();
        effective.ModelId ??= metadata.ModelName;
        effective.Temperature ??= metadata.Generation?.Temperature is double temperature ? checked((float)temperature) : null;
        effective.TopP ??= metadata.Generation?.TopP is double topP ? checked((float)topP) : null;
        effective.TopK ??= metadata.Generation?.TopK;
        effective.MaxOutputTokens ??= metadata.Generation?.MaxOutputTokens;
        effective.Seed ??= metadata.Generation?.Seed;
        effective.StopSequences ??= metadata.Generation?.StopSequences?.ToList();
        return effective;
    }
}
