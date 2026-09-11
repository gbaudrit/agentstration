using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Aep.MicrosoftExtensionsAI;
using Agentstration.Extensions.Ollama;
using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Secrets.Abstractions;
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
    public async Task ConfigurationCatalogPublishesImmutableOptionContracts()
    {
        await using var factory = new AepExtensionFactory();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);

        var descriptor = await client.DiscoverAsync();
        var configuration = await client.GetConfigurationAsync();
        var optionSet = configuration.OptionSets.Single();
        var version = optionSet.Versions.Single();

        Assert.IsTrue(descriptor.Capabilities.ContainsKey(AepCapabilityNames.Configuration));
        Assert.AreEqual(OllamaOptionContracts.ModelProfileOptionSet, optionSet.Id);
        Assert.AreEqual(OllamaOptionContracts.Version, optionSet.PreferredVersion);
        Assert.AreEqual(AepSchemaDigest.Compute(version.Schema), version.SchemaDigest);
    }

    [TestMethod]
    public async Task ServerRejectsInvalidOptionsBeforeInvokingProvider()
    {
        await using var factory = new AepExtensionFactory();
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var optionSet = (await client.GetConfigurationAsync()).OptionSets.Single();
        var version = optionSet.Versions.Single();
        var invalid = new AepVersionedOptions(
            optionSet.Id,
            version.Version,
            version.SchemaDigest,
            JsonSerializer.SerializeToElement(new { removedOption = true }));

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() =>
            client.CreateModelProvider("test").ChatAsync(Request() with { Options = new AepModelOptions { NativeOptions = invalid } }));

        Assert.AreEqual("invalid_options", exception.Code);
        Assert.AreEqual(0, factory.Provider.InvocationCount);
    }

    [TestMethod]
    public async Task OlderPinnedOptionVersionRemainsExecutableWhenNewVersionBecomesPreferred()
    {
        await using var factory = new AepExtensionFactory(addSecondOptionVersion: true);
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var optionSet = (await client.GetConfigurationAsync()).OptionSets.Single();
        var versionOne = optionSet.Versions.Single(value => value.Version == OllamaOptionContracts.Version);
        var pinned = new AepVersionedOptions(
            optionSet.Id,
            versionOne.Version,
            versionOne.SchemaDigest,
            JsonSerializer.SerializeToElement(new { }));

        var response = await client.CreateModelProvider("test").ChatAsync(
            Request() with { Options = new AepModelOptions { NativeOptions = pinned } });

        Assert.AreEqual("2.0.0", optionSet.PreferredVersion);
        Assert.HasCount(2, optionSet.Versions);
        Assert.AreEqual("pong", response.Messages.Single().Contents.Single().Text);
        Assert.AreEqual(1, factory.Provider.InvocationCount);
    }

    [TestMethod]
    public async Task OptionMigrationIsExplicitAndValidatedAgainstTheTargetContract()
    {
        await using var factory = new AepExtensionFactory(addSecondOptionVersion: true);
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var optionSet = (await client.GetConfigurationAsync()).OptionSets.Single();
        var source = optionSet.Versions.Single(value => value.Version == OllamaOptionContracts.Version);

        var response = await client.MigrateOptionsAsync(new(
            optionSet.Id,
            source.Version,
            source.SchemaDigest,
            "2.0.0",
            JsonSerializer.SerializeToElement(new { })));

        Assert.HasCount(1, optionSet.Migrations!);
        Assert.AreEqual(OllamaOptionContracts.Version, optionSet.Migrations![0].FromVersion);
        Assert.AreEqual("2.0.0", response.Options.Version);
        Assert.AreEqual(optionSet.Versions.Single(value => value.Version == "2.0.0").SchemaDigest, response.Options.SchemaDigest);
        Assert.AreEqual(JsonValueKind.Object, response.Options.Values.ValueKind);
    }

    [TestMethod]
    public async Task OptionMigrationCanTraverseValidatedIntermediateVersions()
    {
        await using var factory = new AepExtensionFactory(addSecondOptionVersion: true, addThirdOptionVersion: true);
        using var httpClient = factory.CreateClient();
        var client = new AepClient(httpClient);
        var optionSet = (await client.GetConfigurationAsync()).OptionSets.Single();
        var source = optionSet.Versions.Single(value => value.Version == OllamaOptionContracts.Version);

        var response = await client.MigrateOptionsAsync(new(
            optionSet.Id,
            source.Version,
            source.SchemaDigest,
            "3.0.0",
            JsonSerializer.SerializeToElement(new { })));

        Assert.HasCount(2, optionSet.Migrations!);
        Assert.AreEqual("3.0.0", response.Options.Version);
        Assert.AreEqual(optionSet.Versions.Single(value => value.Version == "3.0.0").SchemaDigest, response.Options.SchemaDigest);
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
            new ChatOptions { Instructions = "Follow system rules.", Temperature = 0.25f, MaxOutputTokens = 42, ResponseFormat = ChatResponseFormat.Json, Tools = [tool] });
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
            AdapterType = AepModelProvider.AdapterType,
            ContributionId = "test",
            Extension = new ResourceReference("test-extension"),
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

    [TestMethod]
    public async Task AepResolutionFailsClosedWhenPinnedOptionVersionWasRemoved()
    {
        await using var factory = new AepExtensionFactory();
        using var httpClient = factory.CreateClient();
        var provider = new AepModelProvider(new FixedHttpClientFactory(httpClient));
        var configuration = new ModelProviderConfiguration
        {
            Uid = Guid.NewGuid(),
            Namespace = ResourceNamespace.Default,
            Name = "test-local",
            AdapterType = AepModelProvider.AdapterType,
            ContributionId = "test",
            Extension = new ResourceReference("test-extension"),
            Endpoint = httpClient.BaseAddress!
        };
        var deployment = new ModelDeploymentConfiguration
        {
            Name = "profile",
            ProviderName = "test-local",
            ModelName = "test-model",
            ProviderOptions = new Dictionary<string, VersionedExtensionOptions>
            {
                ["test"] = new()
                {
                    OptionSet = OllamaOptionContracts.ModelProfileOptionSet,
                    Version = "0.9.0",
                    SchemaDigest = $"sha256:{new string('0', 64)}",
                    Values = JsonSerializer.SerializeToElement(new { })
                }
            }
        };

        var exception = await Assert.ThrowsAsync<ModelProviderConfigurationException>(() =>
            provider.ResolveCapabilitiesAsync(configuration, deployment).AsTask());

        StringAssert.Contains(exception.Message, "version '0.9.0' is not supported");
    }

    [TestMethod]
    public async Task ScopedAepCredentialIsResolvedForEveryRequestAndRotatesWithoutConfigurationChanges()
    {
        var resolver = new MutableSecretResolver("first-token");
        var handler = new CredentialRecordingHandler("extension.test");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://extension.test/") };
        var provider = new AepModelProvider(new FixedHttpClientFactory(httpClient), resolver);
        var configuration = AuthenticatedConfiguration("extension.test");

        _ = await provider.ListModelsAsync(configuration);
        resolver.Token = "second-token";
        _ = await provider.ListModelsAsync(configuration);

        CollectionAssert.AreEqual(
            new[] { "Bearer first-token", "Bearer first-token", "Bearer second-token", "Bearer second-token" },
            handler.Authorizations);
        Assert.AreEqual(4, resolver.ResolutionCount);
    }

    [TestMethod]
    public async Task EnrolledExtensionInspectionUsesItsScopedCredential()
    {
        var resolver = new MutableSecretResolver("enrollment-token");
        var handler = new CredentialRecordingHandler("extension.enrolled");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://extension.test/") };
        var provider = new AepModelProvider(new FixedHttpClientFactory(httpClient), resolver);
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var registration = new ExtensionRegistrationResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ExtensionRegistration,
            Metadata = new ResourceMetadata { Name = "paired-extension" },
            ScopeRef = scope,
            Definition = new ExtensionRegistrationProperties
            {
                DisplayName = "Paired extension",
                Endpoint = httpClient.BaseAddress!,
                ExpectedExtensionId = "extension.enrolled",
                AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
                EnrollmentMode = Agentstration.Management.Abstractions.AepEnrollmentMode.PairingCode,
                Credential = new ResourceReference("enrollment-secret", scope)
            }
        };

        var inspection = await provider.InspectAsync(registration);

        Assert.AreEqual("available", inspection.Status);
        Assert.HasCount(2, handler.Authorizations);
        Assert.IsTrue(handler.Authorizations.All(value => value == "Bearer enrollment-token"));
        Assert.AreEqual(2, resolver.ResolutionCount);
    }

    [TestMethod]
    public async Task MissingScopedCredentialFailsBeforeDiscoveryIsSent()
    {
        var resolver = new MutableSecretResolver(null);
        var handler = new CredentialRecordingHandler("extension.test");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://extension.test/") };
        var provider = new AepModelProvider(new FixedHttpClientFactory(httpClient), resolver);

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() =>
            provider.ListModelsAsync(AuthenticatedConfiguration("extension.test")).AsTask());

        Assert.AreEqual("credential_unavailable", exception.Code);
        Assert.IsEmpty(handler.Authorizations);
    }

    [TestMethod]
    public async Task ConcurrentExtensionsKeepScopedCredentialsSeparated()
    {
        var resolver = new NamedSecretResolver(new Dictionary<string, string>
        {
            ["first-secret"] = "first-token",
            ["second-secret"] = "second-token"
        });
        var firstHandler = new CredentialRecordingHandler("extension.first");
        var secondHandler = new CredentialRecordingHandler("extension.second");
        using var firstHttp = new HttpClient(firstHandler) { BaseAddress = new Uri("https://first.test/") };
        using var secondHttp = new HttpClient(secondHandler) { BaseAddress = new Uri("https://second.test/") };
        var first = new AepModelProvider(new FixedHttpClientFactory(firstHttp), resolver);
        var second = new AepModelProvider(new FixedHttpClientFactory(secondHttp), resolver);
        var firstConfiguration = AuthenticatedConfiguration("extension.first") with
        {
            Endpoint = firstHttp.BaseAddress!,
            Credential = new ResourceReference("first-secret")
        };
        var secondConfiguration = AuthenticatedConfiguration("extension.second") with
        {
            Endpoint = secondHttp.BaseAddress!,
            Credential = new ResourceReference("second-secret")
        };

        await Task.WhenAll(
            first.ListModelsAsync(firstConfiguration).AsTask(),
            second.ListModelsAsync(secondConfiguration).AsTask());

        Assert.HasCount(2, firstHandler.Authorizations);
        Assert.HasCount(2, secondHandler.Authorizations);
        Assert.IsTrue(firstHandler.Authorizations.All(value => value == "Bearer first-token"));
        Assert.IsTrue(secondHandler.Authorizations.All(value => value == "Bearer second-token"));
    }

    [TestMethod]
    public async Task ExpectedExtensionIdentityIsCheckedBeforeFunctionalRequest()
    {
        var handler = new CredentialRecordingHandler("extension.other");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://extension.test/") };
        var provider = new AepModelProvider(new FixedHttpClientFactory(httpClient));
        var configuration = AuthenticatedConfiguration("extension.expected") with
        {
            AuthenticationMode = AepTransportAuthenticationMode.None,
            Credential = null
        };

        var exception = await Assert.ThrowsAsync<AepProtocolException>(() => provider.ListModelsAsync(configuration).AsTask());

        Assert.AreEqual("extension_identity_mismatch", exception.Code);
        Assert.AreEqual(1, handler.RequestCount);
    }

    private static ModelProviderConfiguration AuthenticatedConfiguration(string expectedExtensionId)
    {
        var scope = ResourceScopeRef.Tenant(Guid.NewGuid());
        return new ModelProviderConfiguration
        {
            Uid = Guid.NewGuid(),
            Namespace = ResourceNamespace.Default,
            ScopeRef = scope,
            Name = "test-local",
            AdapterType = AepModelProvider.AdapterType,
            ContributionId = "test",
            Extension = new ResourceReference("test-extension", scope),
            ExtensionScopeRef = scope,
            Endpoint = new Uri("https://extension.test/"),
            ExpectedExtensionId = expectedExtensionId,
            AuthenticationMode = AepTransportAuthenticationMode.StaticBearer,
            Credential = new ResourceReference("test-secret", scope)
        };
    }

    private static AepChatRequest Request() => new("test-model", [new(AepRole.User, [AepContent.FromText("ping")])]);

    private sealed class AepExtensionFactory(bool addSecondOptionVersion = false, bool addThirdOptionVersion = false) : WebApplicationFactory<OllamaAepModelProvider>
    {
        public FakeProvider Provider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAepModelProvider>();
            services.AddSingleton<IAepModelProvider>(Provider);
            if (addSecondOptionVersion) services.AddSingleton<IAepOptionMigrator, TestOptionMigrator>();
            if (addThirdOptionVersion) services.AddSingleton<IAepOptionMigrator, ThirdOptionMigrator>();
            services.PostConfigure<AepExtensionOptions>(options =>
            {
                var original = options.OptionSets.Single() with { ContributionId = "test" };
                options.OptionSets.Clear();
                if (!addSecondOptionVersion)
                {
                    options.OptionSets.Add(original);
                    return;
                }
                var nextSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                });
                var versions = original.Versions.Append(AepOptionSetVersionDescriptor.Create("2.0.0", nextSchema));
                if (addThirdOptionVersion) versions = versions.Append(AepOptionSetVersionDescriptor.Create("3.0.0", nextSchema));
                options.OptionSets.Add(original with
                {
                    PreferredVersion = addThirdOptionVersion ? "3.0.0" : "2.0.0",
                    Versions = versions.ToArray()
                });
            });
        });
    }

    private sealed class TestOptionMigrator : IAepOptionMigrator
    {
        public string OptionSet => OllamaOptionContracts.ModelProfileOptionSet;
        public string FromVersion => OllamaOptionContracts.Version;
        public string ToVersion => "2.0.0";
        public ValueTask<JsonElement> MigrateAsync(JsonElement values, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));
        }
    }

    private sealed class ThirdOptionMigrator : IAepOptionMigrator
    {
        public string OptionSet => OllamaOptionContracts.ModelProfileOptionSet;
        public string FromVersion => "2.0.0";
        public string ToVersion => "3.0.0";
        public ValueTask<JsonElement> MigrateAsync(JsonElement values, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(values.Clone());
        }
    }

    private sealed class FakeProvider : IAepModelProvider
    {
        public int InvocationCount { get; private set; }
        public AepModelProviderDescriptor Descriptor { get; } = new("test", "Test", new(Tools: true, ModelDiscovery: true));
        public Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            Assert.AreEqual("test-model", request.Model);
            if (request.Options?.Temperature == 0.25f)
            {
                Assert.AreEqual(AepRole.System, request.Messages[0].Role);
                Assert.AreEqual("Follow system rules.", request.Messages[0].Contents.Single().Text);
                Assert.AreEqual("rules", request.Messages[1].Contents.Single().Text);
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

    private sealed class CredentialRecordingHandler(string extensionId) : HttpMessageHandler
    {
        public List<string> Authorizations { get; } = [];
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Headers.Authorization is { } authorization)
                Authorizations.Add(authorization.ToString());
            var value = request.RequestUri?.AbsolutePath switch
            {
                AepProtocol.DiscoveryPath => JsonSerializer.Serialize(new AepManifest(
                    AepProtocol.Version,
                    new(extensionId, "Test", "1.0.0"),
                    new Dictionary<string, AepCapabilityDescriptor>(),
                    new([new("test", "Test", new(ModelDiscovery: true))])), AepProtocol.JsonOptions),
                AepProtocol.ConfigurationPath => JsonSerializer.Serialize(new AepConfigurationCatalog([]), AepProtocol.JsonOptions),
                _ => JsonSerializer.Serialize(new[] { new AepModelDescriptor("test-model", "Test model") }, AepProtocol.JsonOptions)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MutableSecretResolver(string? token) : ISecretResolver
    {
        public string? Token { get; set; } = token;
        public int ResolutionCount { get; private set; }

        public Task<ResolvedSecret?> ResolveAsync(
            SecretReference secret,
            SecretResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolutionCount++;
            if (Token is null) return Task.FromResult<ResolvedSecret?>(null);
            var value = new SecretValue(Encoding.UTF8.GetBytes(Token));
            return Task.FromResult<ResolvedSecret?>(new ResolvedSecret(
                secret.Address,
                new ResourceAddress(secret.Address.Namespace, ResourceKinds.Vault, "test-vault"),
                value));
        }
    }

    private sealed class NamedSecretResolver(IReadOnlyDictionary<string, string> tokens) : ISecretResolver
    {
        public Task<ResolvedSecret?> ResolveAsync(
            SecretReference secret,
            SecretResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tokens.TryGetValue(secret.Address.Name, out var token)) return Task.FromResult<ResolvedSecret?>(null);
            return Task.FromResult<ResolvedSecret?>(new ResolvedSecret(
                secret.Address,
                new ResourceAddress(secret.Address.Namespace, ResourceKinds.Vault, "test-vault"),
                new SecretValue(Encoding.UTF8.GetBytes(token))));
        }
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
