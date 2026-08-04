using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.ModelProviders;
using Agentstration.ModelProviders.Ollama;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class OllamaModelProviderTests
{
    private static readonly Uri Endpoint = new("http://localhost:11434");

    [TestMethod]
    public void ProviderResolverFindsProvidersCaseInsensitivelyAndRejectsUnknownProviders()
    {
        var provider = new StubModelProvider();
        var resolver = new ModelProviderResolver([provider]);

        Assert.AreSame(provider, resolver.GetRequiredProvider("OLLAMA"));
        Assert.ThrowsExactly<ModelProviderNotFoundException>(() => resolver.GetRequiredProvider("unknown"));
    }

    [TestMethod]
    public async Task OllamaProviderSelectsDeploymentModelPerRequest()
    {
        using var chatClient = new StubChatClient();
        var factory = new StubClientFactory(chatClient);
        var provider = new OllamaModelProvider(factory, NullLogger<OllamaModelProvider>.Instance);
        var deployment = DeploymentConfiguration() with { ModelName = "qwen3:4b" };

        using var resolved = provider.CreateChatClient(ProviderConfiguration(), deployment);
        _ = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        Assert.AreEqual("qwen3:4b", chatClient.Options?.ModelId);
        Assert.AreEqual("ollama", provider.ProviderType);
    }

    [TestMethod]
    public void OllamaProviderUsesConfiguredEndpointAndRejectsMissingModel()
    {
        using var chatClient = new StubChatClient();
        var factory = new StubClientFactory(chatClient);
        var provider = new OllamaModelProvider(factory, NullLogger<OllamaModelProvider>.Instance);
        var wrongEndpoint = ProviderConfiguration() with { Endpoint = new Uri("http://ollama.example:11434") };
        var missingModel = DeploymentConfiguration() with { ModelName = string.Empty };

        using var resolved = provider.CreateChatClient(wrongEndpoint, DeploymentConfiguration());
        Assert.AreEqual(wrongEndpoint.Endpoint, factory.LastProvider?.Endpoint);
        Assert.ThrowsExactly<ModelProviderConfigurationException>(() => provider.CreateChatClient(ProviderConfiguration(), missingModel));
    }

    [TestMethod]
    public async Task OllamaProviderMapsTypedAndAdditionalNativeOptions()
    {
        using var chatClient = new StubChatClient();
        var provider = CreateProvider(chatClient);
        var deployment = DeploymentConfiguration() with
        {
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["ollama"] = JsonSerializer.SerializeToElement(new
                {
                    think = "medium",
                    keepAlive = "10m",
                    contextSize = 8192,
                    numGpu = 1,
                    additionalOptions = new { repeat_penalty = 1.1 }
                })
            }
        };

        using var resolved = provider.CreateChatClient(ProviderConfiguration(), deployment);
        _ = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        Assert.AreEqual("medium", chatClient.Options?.AdditionalProperties?["think"]);
        Assert.AreEqual(8192, chatClient.Options?.AdditionalProperties?["num_ctx"]);
        Assert.AreEqual(1, chatClient.Options?.AdditionalProperties?["num_gpu"]);
        Assert.IsTrue(chatClient.Options?.AdditionalProperties?.ContainsKey("repeat_penalty") == true);
    }

    [TestMethod]
    public void OllamaProviderRejectsGenerateForMafChatClient()
    {
        using var chatClient = new StubChatClient();
        var deployment = DeploymentConfiguration() with
        {
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["ollama"] = JsonSerializer.SerializeToElement(new { endpointMode = "generate" })
            }
        };

        var exception = Assert.ThrowsExactly<ModelProviderConfigurationException>(() =>
            CreateProvider(chatClient).CreateChatClient(ProviderConfiguration(), deployment));
        StringAssert.Contains(exception.Message, "incompatible");
    }

    [TestMethod]
    public void RegistrationUsesDiscoveredEndpointAndConfiguredModelWithoutNetworkAccess()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddAgentstrationModelProviders(builder.Configuration);
        builder.AddOllamaModelProvider();
        using var services = builder.Services.BuildServiceProvider();

        Assert.IsNotNull(services.GetRequiredService<IOllamaClientFactory>());
        Assert.AreEqual("ollama", services.GetRequiredService<IModelProviderResolver>().GetRequiredProvider("ollama").ProviderType);
        var pipeline = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("agentstration-ollama-dynamic");
        Assert.IsTrue(ContainsPayloadCaptureHandler(pipeline));
    }

    private static bool ContainsPayloadCaptureHandler(HttpMessageHandler handler)
    {
        HttpMessageHandler? current = handler;
        while (current is not null)
        {
            if (current is GenAiHttpPayloadCaptureHandler) return true;
            if (current is not DelegatingHandler) break;
            current = (current as DelegatingHandler)?.InnerHandler;
        }
        return false;
    }

    private static OllamaModelProvider CreateProvider(IChatClient client) => new(
        new StubClientFactory(client),
        NullLogger<OllamaModelProvider>.Instance);

    private static ModelProviderConfiguration ProviderConfiguration() => new()
    {
        ResourceId = "/resourceGroups/default/providers/Agentstration.Model/modelProviders/ollama-local",
        Name = "ollama-local",
        ProviderType = "ollama",
        Endpoint = Endpoint
    };

    private static ModelDeploymentConfiguration DeploymentConfiguration() => new()
    {
        Name = "local-reasoning",
        ProviderName = "ollama-local",
        ModelName = "qwen3:1.7b"
    };

    private sealed class StubModelProvider : IModelProvider
    {
        public string ProviderType => "ollama";
        public IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment) => throw new NotSupportedException();
    }

    private sealed class StubClientFactory(IChatClient client) : IOllamaClientFactory
    {
        public ModelProviderConfiguration? LastProvider { get; private set; }

        public OllamaSharp.OllamaApiClient CreateApiClient(ModelProviderConfiguration provider, string? modelName = null) =>
            throw new NotSupportedException();

        public IChatClient CreateChatClient(ModelProviderConfiguration provider, string modelName)
        {
            LastProvider = provider;
            return client;
        }
    }

    internal sealed class StubChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response(options));

        private ChatResponse Response(ChatOptions? options)
        {
            Options = options;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }

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
