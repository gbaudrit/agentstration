using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Aep.MicrosoftExtensionsAI;
using Agentstration.Extensions.Ollama;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OllamaSharp;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class AepVerticalTests
{
    [TestMethod]
    public void ContractsRoundTripWithProtocolVersionAndExtensibleContent()
    {
        var descriptor = new AepManifest(
            AepProtocol.Version,
            new("extension.test", "Test", "1.2.3"),
            new Dictionary<string, AepCapabilityDescriptor>(),
            new([new("test", "Test provider", new(Tools: true, ModelDiscovery: true))]));

        var json = JsonSerializer.Serialize(descriptor, AepProtocol.JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<AepManifest>(json, AepProtocol.JsonOptions);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(AepProtocol.Version, roundTrip.ProtocolVersion);
        Assert.IsTrue(roundTrip.Contributions.ModelProviders[0].Capabilities.Tools);
        StringAssert.Contains(json, "modelProviders");
    }

    [TestMethod]
    public void DescriptorSupportsMultipleMcpServersAndSchemaFreeToolMappings()
    {
        var descriptor = new AepManifest(
            AepProtocol.Version,
            new("extension.tools", "Tools", "1.0.0"),
            new Dictionary<string, AepCapabilityDescriptor>(),
            new([], [new("search", "Search", new("primary", "search_docs"), "Search documents")]),
            new([new("primary", "/mcp"), new("remote", "https://tools.example/mcp")]));

        var json = JsonSerializer.Serialize(descriptor, AepProtocol.JsonOptions);
        var errors = AepDescriptorValidator.Validate(descriptor);

        Assert.IsEmpty(errors);
        Assert.AreEqual(new Uri("http://extension/mcp"), AepDescriptorValidator.ResolveMcpEndpoint(new Uri("http://extension"), descriptor.Mcp!.Servers[0]));
        Assert.IsFalse(json.Contains("inputSchema", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("outputSchema", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DescriptorRejectsUnknownMcpServerAndMalformedEndpoint()
    {
        var descriptor = new AepManifest(
            AepProtocol.Version,
            new("extension.tools", "Tools", "1.0.0"),
            new Dictionary<string, AepCapabilityDescriptor>(),
            new([], [new("search", "Search", new("missing", "search_docs"))]),
            new([new("primary", "ftp://invalid/mcp")]));

        var errors = AepDescriptorValidator.Validate(descriptor);

        Assert.HasCount(2, errors);
        Assert.IsTrue(errors.Any(value => value.Contains("HTTP(S)", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(value => value.Contains("unknown MCP server", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ServerAndClientSupportDiscoveryChatStreamingModelsAndErrors()
    {
        await using var factory = new AepExtensionFactory();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);

        var descriptor = await client.DiscoverAsync();
        var providers = await client.ListModelProvidersAsync();
        var provider = client.CreateModelProvider("test");
        var response = await provider.ChatAsync(Request());
        var health = await provider.GetHealthAsync();
        var updates = new List<AepChatUpdate>();
        await foreach (var update in provider.ChatStreamingAsync(Request())) updates.Add(update);
        var models = await provider.ListModelsAsync();

        Assert.AreEqual("Agentstration.Extensions.Ollama", descriptor.Extension.Id);
        Assert.AreEqual("test", providers.Single().Id);
        Assert.AreEqual("pong", response.Messages.Single().Contents.Single().Text);
        Assert.AreEqual("available", health.Status);
        Assert.AreEqual("po", updates[0].Contents.Single().Text);
        Assert.AreEqual("test-model", models.Single().Id);
        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => client.CreateModelProvider("missing").ChatAsync(Request()));
        Assert.AreEqual("provider_unavailable", exception.Code);
    }

    [TestMethod]
    public async Task ClientRejectsIncompatibleProtocol()
    {
        var descriptor = new AepManifest("2.0", new("x", "x", "1"), new Dictionary<string, AepCapabilityDescriptor>(), new([]));
        using var httpClient = new HttpClient(new StaticHandler(HttpStatusCode.OK, JsonSerializer.Serialize(descriptor, AepProtocol.JsonOptions))) { BaseAddress = new Uri("http://extension") };

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => new AepClient(httpClient).DiscoverAsync());

        Assert.AreEqual("protocol_incompatible", exception.Code);
    }

    [TestMethod]
    public async Task MicrosoftExtensionsAiAdapterMapsMessagesOptionsResponseStreamingAndCancellation()
    {
        await using var factory = new AepExtensionFactory();
        using var httpClient = factory.CreateClient();
        using var adapter = new AepChatClient(new AepClient(httpClient).CreateModelProvider("test"), "test-model");
        var tool = AIFunctionFactory.Create((string city) => $"sunny in {city}", new AIFunctionFactoryOptions { Name = "weather", Description = "Gets weather" });
        var response = await adapter.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "rules"), new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions { Temperature = 0.25f, MaxOutputTokens = 42, ResponseFormat = ChatResponseFormat.Json, Tools = [tool] });
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in adapter.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "ping")])) updates.Add(update);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.AreEqual("pong", response.Text);
        Assert.AreEqual("pong", string.Concat(updates.Select(value => value.Text)));
        await Assert.ThrowsAsync<OperationCanceledException>(() => adapter.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task OllamaExtensionMapsModelAndNativeOptionsWithoutCallingARealServer()
    {
        using var inner = new CapturingChatClient();
        using var api = new OllamaApiClient(new HttpClient { BaseAddress = new Uri("http://localhost:11434") });
        var provider = new OllamaAepModelProvider(inner, api);
        var options = new Dictionary<string, JsonElement>
        {
            ["ollama"] = JsonSerializer.SerializeToElement(new { think = "medium", contextSize = 8192, additionalOptions = new { repeat_penalty = 1.1 } })
        };

        var response = await provider.ChatAsync(Request() with { Options = new AepModelOptions { Temperature = 0.2f, AdditionalOptions = options } }, default);

        Assert.AreEqual("pong", response.Messages.Single().Contents.Single().Text);
        Assert.AreEqual("test-model", inner.Options?.ModelId);
        Assert.AreEqual("medium", inner.Options?.AdditionalProperties?["think"]);
        Assert.AreEqual(8192, inner.Options?.AdditionalProperties?["num_ctx"]);
        Assert.IsTrue(inner.Options?.AdditionalProperties?.ContainsKey("repeat_penalty") == true);
    }

    [TestMethod]
    public void GenericModelProviderResolverUsesAepFallbackForContributionIds()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        using var provider = services.BuildServiceProvider();
        var aep = new AepModelProvider(provider.GetRequiredService<IHttpClientFactory>());
        var resolver = new ModelProviderResolver([aep]);

        Assert.AreSame(aep, resolver.GetRequiredProvider("ollama"));
        Assert.AreSame(aep, resolver.GetRequiredProvider("llamacpp"));
    }

    [TestMethod]
    public async Task AepResolutionMapsProviderModelAndAdapterCapabilitiesIndependently()
    {
        await using var factory = new AepExtensionFactory();
        using var httpClient = factory.CreateClient();
        var provider = new AepModelProvider(new FixedHttpClientFactory(httpClient));
        var configuration = new ModelProviderConfiguration
        {
            Uid = Guid.NewGuid(),
            Namespace = ResourceNamespace.Default,
            Name = "test-local",
            ProviderType = "test",
            Endpoint = httpClient.BaseAddress!
        };

        var capabilities = await provider.ResolveCapabilitiesAsync(
            configuration,
            new ModelDeploymentConfiguration { Name = "profile", ProviderName = "test-local", ModelName = "test-model" });

        Assert.AreEqual(CapabilitySupport.Native, capabilities.Provider.Tools.Support);
        Assert.AreEqual(CapabilitySupport.Native, capabilities.Model.Streaming.Support);
        Assert.AreEqual(CapabilitySupport.Native, capabilities.Model.Tools.Support);
        Assert.AreEqual(CapabilitySupport.Unsupported, capabilities.Model.StructuredOutput.Support);
        Assert.AreEqual(CapabilitySupport.Partial, capabilities.Adapter.Reasoning.Support);
    }

    private static AepChatRequest Request() => new("test-model", [new(AepRole.User, [AepContent.FromText("ping")])]);

    private sealed class AepExtensionFactory : WebApplicationFactory<OllamaAepModelProvider>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAepModelProvider>();
            services.AddSingleton<IAepModelProvider, FakeProvider>();
        });
    }

    private sealed class FakeProvider : IAepModelProvider
    {
        public AepModelProviderDescriptor Descriptor { get; } = new("test", "Test", new(Tools: true, ModelDiscovery: true));
        public Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("test-model", request.Model);
            if (request.Options?.Temperature == 0.25f)
            {
                Assert.AreEqual("weather", request.Tools?.Single().Name);
                Assert.AreEqual(JsonValueKind.Object, request.Tools?.Single().Parameters.ValueKind);
                Assert.AreEqual("json_object", request.Options?.ResponseFormat?.GetProperty("type").GetString());
            }
            return Task.FromResult(new AepChatResponse([new(AepRole.Assistant, [AepContent.FromText("pong")])], request.Model, AepFinishReason.Stop));
        }
        public async IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(AepChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new([AepContent.FromText("po")], AepRole.Assistant, request.Model);
            await Task.Yield();
            yield return new([AepContent.FromText("ng")], FinishReason: AepFinishReason.Stop);
        }
        public Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AepModelDescriptor>>([new("test-model", "Test model", ["chat", "streaming", "tools"])]);
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        });
    }

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
