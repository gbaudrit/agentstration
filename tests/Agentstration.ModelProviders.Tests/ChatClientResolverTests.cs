using Agentstration.ModelProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class ChatClientResolverTests
{
    private const string ProfileId = "/resourceGroups/default/providers/Agentstration.Models/modelProfiles/reasoning-default";

    [TestMethod]
    public async Task ResolverTraversesProfileDeploymentProviderAndTechnicalImplementation()
    {
        using var chatClient = new OllamaModelProviderTests.StubChatClient();
        var provider = new RecordingProvider(chatClient);
        var configuration = Configuration();
        var resolver = new ChatClientResolver(
            new ConfigurationModelProfileStore(configuration),
            new ConfigurationModelDeploymentStore(configuration),
            new ConfigurationModelProviderStore(configuration),
            new ModelProviderResolver([provider]),
            NullLogger<ChatClientResolver>.Instance);

        var resolved = await resolver.ResolveAsync(ProfileId);

        Assert.IsNotNull(resolved);
        var metadata = resolved.GetService(typeof(ModelChatClientMetadata)) as ModelChatClientMetadata;
        Assert.IsNotNull(metadata);
        Assert.AreEqual("reasoning-default", metadata.ModelProfile);
        Assert.AreEqual("ollama", metadata.ProviderType);
        Assert.AreEqual("qwen3:1.7b", metadata.ModelName);
        Assert.AreEqual(0.2, metadata.Generation?.Temperature);
        Assert.AreEqual(1000, metadata.Generation?.MaxOutputTokens);
        Assert.AreEqual("ollama-local", provider.Provider?.Name);
        Assert.AreEqual("local-reasoning", provider.Deployment?.Name);
        Assert.AreEqual("qwen3:1.7b", provider.Deployment?.ModelName);

        _ = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);
        Assert.AreEqual("qwen3:1.7b", chatClient.Options?.ModelId);
        Assert.AreEqual(0.2f, chatClient.Options?.Temperature);
        Assert.AreEqual(1000, chatClient.Options?.MaxOutputTokens);
    }

    [TestMethod]
    public async Task UnknownProfileIsExplicit()
    {
        var store = new ConfigurationModelProfileStore(Configuration());
        await Assert.ThrowsExactlyAsync<ModelProfileNotFoundException>(async () => await store.GetRequiredAsync("missing"));
    }

    [TestMethod]
    public async Task UnknownDeploymentIsExplicit()
    {
        var store = new ConfigurationModelDeploymentStore(Configuration());
        await Assert.ThrowsExactlyAsync<ModelDeploymentNotFoundException>(async () => await store.GetRequiredAsync("missing"));
    }

    [TestMethod]
    public async Task UnknownProviderConfigurationIsExplicit()
    {
        var store = new ConfigurationModelProviderStore(Configuration());
        await Assert.ThrowsExactlyAsync<ModelProviderConfigurationNotFoundException>(async () => await store.GetRequiredAsync("missing"));
    }

    [TestMethod]
    public async Task MissingTechnicalProviderImplementationIsExplicit()
    {
        var configuration = Configuration();
        var resolver = new ChatClientResolver(
            new ConfigurationModelProfileStore(configuration),
            new ConfigurationModelDeploymentStore(configuration),
            new ConfigurationModelProviderStore(configuration),
            new ModelProviderResolver([]),
            NullLogger<ChatClientResolver>.Instance);

        await Assert.ThrowsExactlyAsync<ModelProviderNotFoundException>(async () => await resolver.ResolveAsync(ProfileId));
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(OllamaModelProviderTests.ConfigurationValues())
        .Build();

    private sealed class RecordingProvider(OllamaModelProviderTests.StubChatClient client) : IModelProvider
    {
        public string ProviderType => "ollama";
        public ModelProviderConfiguration? Provider { get; private set; }
        public ModelDeploymentConfiguration? Deployment { get; private set; }

        public Microsoft.Extensions.AI.IChatClient CreateChatClient(ModelProviderConfiguration provider, ModelDeploymentConfiguration deployment)
        {
            Provider = provider;
            Deployment = deployment;
            return client;
        }
    }
}
