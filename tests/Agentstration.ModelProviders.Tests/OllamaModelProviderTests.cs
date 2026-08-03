using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private static readonly Uri Endpoint = new("http://localhost:11434");

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
        var provider = CreateProvider(chatClient);
        var deployment = DeploymentConfiguration() with { ModelName = "qwen3:4b" };

        using var resolved = provider.CreateChatClient(ProviderConfiguration(), deployment);
        _ = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        Assert.AreEqual("qwen3:4b", chatClient.Options?.ModelId);
        Assert.AreEqual("ollama", provider.ProviderType);
    }

    [TestMethod]
    public void OllamaProviderRejectsInvalidEndpointAndModel()
    {
        using var chatClient = new StubChatClient();
        var provider = CreateProvider(chatClient);
        var wrongEndpoint = ProviderConfiguration() with { Endpoint = new Uri("http://ollama.example:11434") };
        var missingModel = DeploymentConfiguration() with { ModelName = string.Empty };

        Assert.ThrowsExactly<ModelProviderConfigurationException>(() => provider.CreateChatClient(wrongEndpoint, DeploymentConfiguration()));
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
        builder.Configuration.AddInMemoryCollection(ConfigurationValues());

        builder.Services.AddAgentstrationModelProviders(builder.Configuration);
        builder.AddOllamaModelProvider("local-chat");
        using var services = builder.Services.BuildServiceProvider();

        Assert.IsNotNull(services.GetRequiredService<IChatClient>());
        Assert.AreEqual("ollama", services.GetRequiredService<IModelProviderResolver>().GetRequiredProvider("ollama").ProviderType);
        Assert.AreEqual(Endpoint, services.GetRequiredService<IOptions<OllamaModelProviderOptions>>().Value.Endpoint);
    }

    private static OllamaModelProvider CreateProvider(IChatClient client) => new(
        client,
        Options.Create(new OllamaModelProviderOptions { Endpoint = Endpoint, DefaultModel = "qwen3:1.7b" }),
        NullLogger<OllamaModelProvider>.Instance);

    private static ModelProviderConfiguration ProviderConfiguration() => new()
    {
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

    internal static Dictionary<string, string?> ConfigurationValues() => new()
    {
        ["ConnectionStrings:local-chat"] = "Endpoint=http://localhost:11434",
        [$"{OllamaModelProviderOptions.SectionName}:DefaultModel"] = "qwen3:1.7b",
        [$"{ModelProviderConfigurationSections.Root}:Providers:ollama-local:ProviderType"] = "ollama",
        [$"{ModelProviderConfigurationSections.Root}:Providers:ollama-local:ConnectionName"] = "local-chat",
        [$"{ModelProviderConfigurationSections.Root}:Deployments:local-reasoning:ProviderName"] = "ollama-local",
        [$"{ModelProviderConfigurationSections.Root}:Deployments:local-reasoning:ModelName"] = "qwen3:1.7b",
        [$"{ModelProviderConfigurationSections.Root}:Profiles:reasoning-default:DeploymentName"] = "local-reasoning",
        [$"{ModelProviderConfigurationSections.Root}:Profiles:reasoning-default:Generation:Temperature"] = "0.2",
        [$"{ModelProviderConfigurationSections.Root}:Profiles:reasoning-default:Generation:MaxOutputTokens"] = "1000"
    };

    private sealed class StubModelProvider : IModelProvider
    {
        public string ProviderType => "ollama";
        public IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment) => throw new NotSupportedException();
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
