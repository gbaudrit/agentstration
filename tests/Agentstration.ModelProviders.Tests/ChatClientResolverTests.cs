using System.Collections.Concurrent;
using System.Diagnostics;
using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class ChatClientResolverTests
{
    private const string ProfileId = "/resourceGroups/default/providers/Agentstration.Models/modelProfiles/reasoning-default";
    private const string ProviderId = "/resourceGroups/default/providers/Agentstration.ModelProviders/modelProviders/ollama-local";

    [TestMethod]
    public async Task ResolverTraversesProfileDeploymentProviderAndTechnicalImplementation()
    {
        using var chatClient = new OllamaModelProviderTests.StubChatClient();
        var provider = new RecordingProvider(chatClient);
        var resolver = new ChatClientResolver(
            new StubProfileStore(),
            new StubDeploymentStore(),
            new StubProviderStore(),
            new ModelProviderResolver([provider]),
            new GenAiObservabilityOptions { Enabled = false },
            NullLoggerFactory.Instance,
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
        Assert.AreEqual(ProfileId, provider.Deployment?.Name);
        Assert.AreEqual("qwen3:1.7b", provider.Deployment?.ModelName);

        _ = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);
        Assert.AreEqual("qwen3:1.7b", chatClient.Options?.ModelId);
        Assert.AreEqual(0.2f, chatClient.Options?.Temperature);
        Assert.AreEqual(1000, chatClient.Options?.MaxOutputTokens);
    }

    [TestMethod]
    public async Task UnknownProfileIsExplicit()
    {
        var store = new StubProfileStore(configured: false);
        await Assert.ThrowsExactlyAsync<ModelProfileNotFoundException>(async () => await store.GetRequiredAsync("missing"));
    }

    [TestMethod]
    public async Task UnknownDeploymentIsExplicit()
    {
        var store = new StubDeploymentStore(configured: false);
        await Assert.ThrowsExactlyAsync<ModelDeploymentNotFoundException>(async () => await store.GetRequiredAsync("missing"));
    }

    [TestMethod]
    public async Task UnknownProviderConfigurationIsExplicit()
    {
        var store = new StubProviderStore(configured: false);
        await Assert.ThrowsExactlyAsync<ModelProviderConfigurationNotFoundException>(async () => await store.GetRequiredAsync("missing"));
    }

    [TestMethod]
    public async Task MissingTechnicalProviderImplementationIsExplicit()
    {
        var resolver = new ChatClientResolver(
            new StubProfileStore(),
            new StubDeploymentStore(),
            new StubProviderStore(),
            new ModelProviderResolver([]),
            new GenAiObservabilityOptions { Enabled = false },
            NullLoggerFactory.Instance,
            NullLogger<ChatClientResolver>.Instance);

        await Assert.ThrowsExactlyAsync<ModelProviderNotFoundException>(async () => await resolver.ResolveAsync(ProfileId));
    }

    [TestMethod]
    public async Task ResolverEmitsChatTelemetryWithoutMessageContent()
    {
        const string secretPrompt = "secret-provider-prompt";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GenAiObservabilityOptions.ChatClientSourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var chatClient = new OllamaModelProviderTests.StubChatClient();
        var resolver = new ChatClientResolver(
            new StubProfileStore(),
            new StubDeploymentStore(),
            new StubProviderStore(),
            new ModelProviderResolver([new RecordingProvider(chatClient)]),
            new GenAiObservabilityOptions(),
            NullLoggerFactory.Instance,
            NullLogger<ChatClientResolver>.Instance);
        using var resolved = await resolver.ResolveAsync(ProfileId);

        _ = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, secretPrompt)]);

        Assert.IsNotEmpty(stopped);
        var emittedData = string.Join(' ', stopped.SelectMany(ActivityData));
        Assert.IsFalse(emittedData.Contains(secretPrompt, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ActivityData(Activity activity) =>
        activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
            .Concat(activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}")));

    private sealed class StubProfileStore(bool configured = true) : IModelProfileStore
    {
        public ValueTask<ModelProfileConfiguration> GetRequiredAsync(string resourceId, CancellationToken cancellationToken = default) =>
            configured && string.Equals(resourceId, ProfileId, StringComparison.Ordinal)
                ? ValueTask.FromResult(new ModelProfileConfiguration
                {
                    Name = "reasoning-default",
                    DeploymentName = ProfileId,
                    Generation = new ModelGenerationOptions { Temperature = 0.2, MaxOutputTokens = 1000 }
                })
                : ValueTask.FromException<ModelProfileConfiguration>(new ModelProfileNotFoundException(resourceId));
    }

    private sealed class StubDeploymentStore(bool configured = true) : IModelDeploymentStore
    {
        public ValueTask<ModelDeploymentConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default) =>
            configured && string.Equals(name, ProfileId, StringComparison.Ordinal)
                ? ValueTask.FromResult(new ModelDeploymentConfiguration
                {
                    Name = ProfileId,
                    ProviderName = ProviderId,
                    ModelName = "qwen3:1.7b"
                })
                : ValueTask.FromException<ModelDeploymentConfiguration>(new ModelDeploymentNotFoundException(name));
    }

    private sealed class StubProviderStore(bool configured = true) : IModelProviderConfigurationStore
    {
        private static readonly ModelProviderConfiguration Provider = new()
        {
            ResourceId = ProviderId,
            Name = "ollama-local",
            ProviderType = "ollama",
            Endpoint = new Uri("http://localhost:11434/")
        };

        public ValueTask<ModelProviderConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default) =>
            configured && string.Equals(name, ProviderId, StringComparison.Ordinal)
                ? ValueTask.FromResult(Provider)
                : ValueTask.FromException<ModelProviderConfiguration>(new ModelProviderConfigurationNotFoundException(name));

        public ValueTask<IReadOnlyList<ModelProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ModelProviderConfiguration>>(configured ? [Provider] : []);
    }

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
