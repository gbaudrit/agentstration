using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class ChatClientResolverTests
{
    private const string ProfileId = "reasoning-default";
    private const string ProviderId = "ollama-local";

    [TestMethod]
    public async Task ResolverTraversesProfileDeploymentProviderAndTechnicalImplementation()
    {
        using var chatClient = new StubChatClient();
        var provider = new RecordingProvider(chatClient);
        var capabilityResolver = new RecordingCapabilitiesResolver();
        var resolver = new ChatClientResolver(
            new StubProfileStore(),
            new StubDeploymentStore(),
            new StubProviderStore(),
            new ModelProviderResolver([provider]),
            new GenAiObservabilityOptions { Enabled = false },
            NullLoggerFactory.Instance,
            NullLogger<ChatClientResolver>.Instance,
            [capabilityResolver]);

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
        Assert.AreEqual(CapabilitySupport.Native, metadata.ProviderCapabilities?.Streaming.Support);
        Assert.AreEqual(CapabilitySupport.Native, metadata.ModelCapabilities?.Tools.Support);
        Assert.AreEqual(ProviderId, capabilityResolver.Provider?.Name);

        _ = await resolved.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);
        Assert.AreEqual("qwen3:1.7b", chatClient.Options?.ModelId);
        Assert.AreEqual(0.2f, chatClient.Options?.Temperature);
        Assert.AreEqual(1000, chatClient.Options?.MaxOutputTokens);
    }

    [TestMethod]
    public async Task ResolverPreservesProfileAndProviderNamespaces()
    {
        var profileNamespace = new ResourceNamespace("agentstration.sample-pack");
        var providerNamespace = new ResourceNamespace("shared.providers");
        using var chatClient = new StubChatClient();
        var profiles = new StubProfileStore();
        var deployments = new StubDeploymentStore(providerNamespace: providerNamespace);
        var providers = new StubProviderStore(providerNamespace: providerNamespace);
        var resolver = new ChatClientResolver(
            profiles,
            deployments,
            providers,
            new ModelProviderResolver([new RecordingProvider(chatClient)]),
            new GenAiObservabilityOptions { Enabled = false },
            NullLoggerFactory.Instance,
            NullLogger<ChatClientResolver>.Instance);

        _ = await resolver.ResolveAsync(profileNamespace, ProfileId);

        Assert.AreEqual(profileNamespace, profiles.RequestedNamespace);
        Assert.AreEqual(profileNamespace, deployments.RequestedNamespace);
        Assert.AreEqual(providerNamespace, providers.RequestedNamespace);
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
        using var chatClient = new StubChatClient();
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
        public ResourceNamespace? RequestedNamespace { get; private set; }

        public ValueTask<ModelProfileConfiguration> GetRequiredAsync(string resourceId, CancellationToken cancellationToken = default) =>
            GetRequiredAsync(ResourceNamespace.Default, resourceId, cancellationToken);

        public ValueTask<ModelProfileConfiguration> GetRequiredAsync(ResourceNamespace @namespace, string resourceId, CancellationToken cancellationToken = default)
        {
            RequestedNamespace = @namespace;
            return
            configured && string.Equals(resourceId, ProfileId, StringComparison.Ordinal)
                ? ValueTask.FromResult(new ModelProfileConfiguration
                {
                    Name = "reasoning-default",
                    DeploymentName = ProfileId,
                    Generation = new ModelGenerationOptions { Temperature = 0.2, MaxOutputTokens = 1000 }
                })
                : ValueTask.FromException<ModelProfileConfiguration>(new ModelProfileNotFoundException(resourceId));
        }
    }

    private sealed class StubDeploymentStore(bool configured = true, ResourceNamespace? providerNamespace = null) : IModelDeploymentStore
    {
        public ResourceNamespace? RequestedNamespace { get; private set; }

        public ValueTask<ModelDeploymentConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default) =>
            GetRequiredAsync(ResourceNamespace.Default, name, cancellationToken);

        public ValueTask<ModelDeploymentConfiguration> GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default)
        {
            RequestedNamespace = @namespace;
            return
            configured && string.Equals(name, ProfileId, StringComparison.Ordinal)
                ? ValueTask.FromResult(new ModelDeploymentConfiguration
                {
                    Name = ProfileId,
                    ProviderName = ProviderId,
                    ProviderNamespace = providerNamespace ?? ResourceNamespace.Default,
                    ModelName = "qwen3:1.7b"
                })
                : ValueTask.FromException<ModelDeploymentConfiguration>(new ModelDeploymentNotFoundException(name));
        }
    }

    private sealed class StubProviderStore(bool configured = true, ResourceNamespace? providerNamespace = null) : IModelProviderConfigurationStore
    {
        public ResourceNamespace? RequestedNamespace { get; private set; }

        private ModelProviderConfiguration Provider => new()
        {
            Uid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "ollama-local",
            Namespace = providerNamespace ?? ResourceNamespace.Default,
            ProviderType = "ollama",
            Endpoint = new Uri("http://localhost:11434/")
        };

        public ValueTask<ModelProviderConfiguration> GetRequiredAsync(string name, CancellationToken cancellationToken = default) =>
            GetRequiredAsync(ResourceNamespace.Default, name, cancellationToken);

        public ValueTask<ModelProviderConfiguration> GetRequiredAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default)
        {
            RequestedNamespace = @namespace;
            return
            configured && string.Equals(name, ProviderId, StringComparison.Ordinal)
                ? ValueTask.FromResult(Provider)
                : ValueTask.FromException<ModelProviderConfiguration>(new ModelProviderConfigurationNotFoundException(name));
        }

        public ValueTask<IReadOnlyList<ModelProviderConfiguration>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ModelProviderConfiguration>>(configured ? [Provider] : []);
    }

    private sealed class RecordingProvider(StubChatClient client) : IModelProvider
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

    private sealed class RecordingCapabilitiesResolver : IModelProviderCapabilitiesResolver
    {
        public string ProviderType => "ollama";
        public ModelProviderConfiguration? Provider { get; private set; }

        public ValueTask<ResolvedModelProviderCapabilities> ResolveCapabilitiesAsync(
            ModelProviderConfiguration provider,
            ModelDeploymentConfiguration deployment,
            CancellationToken cancellationToken = default)
        {
            Provider = provider;
            var capabilities = new AgentRuntimeCapabilities
            {
                Streaming = new(CapabilitySupport.Native),
                Tools = new(CapabilitySupport.Native)
            };
            return ValueTask.FromResult(new ResolvedModelProviderCapabilities(capabilities, capabilities, capabilities));
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
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
