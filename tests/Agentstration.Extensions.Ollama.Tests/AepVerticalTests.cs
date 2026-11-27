using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Ollama;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class AepVerticalTests
{
    [TestMethod]
    public async Task OllamaExtensionMapsModelAndNativeOptionsWithoutCallingARealServer()
    {
        using var inner = new CapturingChatClient();
        using var api = new OllamaApiClient(new HttpClient { BaseAddress = new Uri("http://localhost:11434") });
        var provider = new OllamaAepModelProvider(inner, api);
        var values = JsonSerializer.SerializeToElement(new { think = "medium", contextSize = 8192, additionalOptions = new { repeat_penalty = 1.1 } });
        var options = new AepVersionedOptions(
            OllamaOptionContracts.ModelProfileOptionSet,
            OllamaOptionContracts.Version,
            OllamaOptionContracts.ModelProfile.Versions.Single().SchemaDigest,
            values);

        var response = await provider.ChatAsync(Request() with { Options = new AepModelOptions { Temperature = 0.2f, NativeOptions = options } }, default);

        Assert.AreEqual("pong", response.Messages.Single().Contents.Single().Text);
        Assert.AreEqual("test-model", inner.Options?.ModelId);
        Assert.AreEqual("medium", inner.Options?.AdditionalProperties?["think"]);
        Assert.AreEqual(8192, inner.Options?.AdditionalProperties?["num_ctx"]);
        Assert.IsTrue(inner.Options?.AdditionalProperties?.ContainsKey("repeat_penalty") == true);
    }

    private static AepChatRequest Request() => new("test-model", [new(AepRole.User, [AepContent.FromText("ping")])]);

    private sealed class CapturingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")) { ModelId = options?.ModelId, FinishReason = ChatFinishReason.Stop });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
