using System.Runtime.CompilerServices;
using Agentstration.ModelProviders;
using Agentstration.ModelProviders.Ollama;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class OllamaModelProviderTests
{
    [TestMethod]
    public void OptionsRequireAnHttpEndpointAndModel()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new OllamaModelProviderOptions().Validate());
        Assert.ThrowsExactly<InvalidOperationException>(() => new OllamaModelProviderOptions
        {
            Endpoint = new Uri("file:///tmp/ollama"),
            DefaultModel = "qwen3:1.7b"
        }.Validate());
    }

    [TestMethod]
    public void ResolverFindsProvidersCaseInsensitivelyAndRejectsUnknownProviders()
    {
        var provider = new StubModelProvider();
        var resolver = new ModelProviderResolver([provider]);

        Assert.AreSame(provider, resolver.GetRequiredProvider("OLLAMA"));
        Assert.ThrowsExactly<InvalidOperationException>(() => resolver.GetRequiredProvider("unknown"));
    }

    [TestMethod]
    public void OllamaProviderReturnsTheConfiguredClientOnlyForTheConfiguredModel()
    {
        using var chatClient = new StubChatClient();
        var provider = new OllamaModelProvider(
            chatClient,
            Options.Create(new OllamaModelProviderOptions { DefaultModel = "qwen3:1.7b" }),
            NullLogger<OllamaModelProvider>.Instance);

        Assert.AreSame(chatClient, provider.CreateChatClient("qwen3:1.7b"));
        Assert.ThrowsExactly<InvalidOperationException>(() => provider.CreateChatClient("another-model"));
    }

    [TestMethod]
    public void RegistrationUsesDiscoveredEndpointAndConfiguredModelWithoutNetworkAccess()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:local-chat"] = "Endpoint=http://localhost:11434",
            [$"{OllamaModelProviderOptions.SectionName}:DefaultModel"] = "qwen3:1.7b"
        });

        builder.AddOllamaModelProvider("local-chat");
        using var services = builder.Services.BuildServiceProvider();

        Assert.IsNotNull(services.GetRequiredService<IChatClient>());
        Assert.AreEqual("ollama", services.GetRequiredService<IModelProviderResolver>().GetRequiredProvider("ollama").ProviderType);
        Assert.AreEqual(new Uri("http://localhost:11434"), services.GetRequiredService<IOptions<OllamaModelProviderOptions>>().Value.Endpoint);
    }

    private sealed class StubModelProvider : IModelProvider
    {
        public string ProviderType => "ollama";
        public IChatClient CreateChatClient(string model) => throw new NotSupportedException();
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
