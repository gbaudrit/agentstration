using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace Agentstration.ModelProviders.Ollama;

internal sealed class OllamaDeploymentChatClient(IChatClient inner, string modelName, OllamaModelOptions modelOptions) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetResponseAsync(messages, WithModel(options), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in inner.GetStreamingResponseAsync(messages, WithModel(options), cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The host owns the shared Ollama client lifetime.
    }

    private ChatOptions WithModel(ChatOptions? options)
    {
        var effective = options?.Clone() ?? new ChatOptions();
        effective.ModelId = modelName;
        effective.AdditionalProperties ??= [];
        if (modelOptions.Think is { } think)
            effective.AddOllamaOption(OllamaOption.Think, think switch
            {
                OllamaThinkOption.Disabled => false,
                OllamaThinkOption.Enabled => true,
                _ => think.ToString().ToLowerInvariant()
            });
        if (modelOptions.KeepAlive is { } keepAlive) effective.AdditionalProperties["keep_alive"] = keepAlive;
        if (modelOptions.ContextSize is { } contextSize) effective.AddOllamaOption(OllamaOption.NumCtx, contextSize);
        if (modelOptions.NumGpu is { } numGpu) effective.AddOllamaOption(OllamaOption.NumGpu, numGpu);
        if (modelOptions.NumThread is { } numThread) effective.AddOllamaOption(OllamaOption.NumThread, numThread);
        if (modelOptions.NumBatch is { } numBatch) effective.AddOllamaOption(OllamaOption.NumBatch, numBatch);
        if (modelOptions.Mirostat is { } mirostat) effective.AddOllamaOption(OllamaOption.MiroStat, mirostat);
        foreach (var additional in modelOptions.AdditionalOptions)
            effective.AddOllamaOption(new OllamaOption(additional.Key), additional.Value);
        return effective;
    }
}
