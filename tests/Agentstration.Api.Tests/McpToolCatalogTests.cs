extern alias UtilitiesExtension;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class McpToolCatalogTests
{
    private static readonly ResourceScopeRef WorkspaceScope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [TestMethod]
    public void ServerMcpEndpointRemainsAvailableWithoutLegacyPlatformTools()
    {
        using var host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = host.CreateClient();
        var routes = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();
        Assert.IsTrue(routes.Any(route => route.StartsWith("/mcp", StringComparison.Ordinal)));

        var legacyTools = new[]
        {
            "list_workspaces", "list_inboxes", "ingest_text", "ingest_url", "search_memory",
            "create_mission", "get_mission", "list_mission_runs", "run_mission_now"
        };
        var publishedToolNames = typeof(global::Program).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.CustomAttributes)
            .Where(attribute => attribute.AttributeType.Name == "McpServerToolAttribute")
            .SelectMany(attribute => attribute.NamedArguments)
            .Where(argument => argument.MemberName == "Name")
            .Select(argument => argument.TypedValue.Value?.ToString() ?? string.Empty)
            .ToArray();
        Assert.IsFalse(publishedToolNames.Any(name => legacyTools.Contains(name, StringComparer.OrdinalIgnoreCase)));
        Assert.IsNull(typeof(global::Program).Assembly.GetType("Agentstration.Web.PlatformMcpTools"));
    }

    [TestMethod]
    public async Task GovernanceBlocksDisabledProviderToolAndUnavailableTool()
    {
        await using var host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var adapter = Adapter(host);
        var provider = Provider();
        var tool = Tool(provider.Metadata.Name);

        await AssertCodeAsync("tool_provider_disabled", provider with { Definition = provider.Definition with { Enabled = false } }, tool);
        await AssertCodeAsync("tool_disabled", provider, tool with { Definition = tool.Definition with { Enabled = false } });
        await AssertCodeAsync("tool_unavailable", provider, tool with { Definition = tool.Definition with { Discovery = tool.Definition.Discovery! with { Available = false } } });

        async Task AssertCodeAsync(string code, ToolProviderResource currentProvider, ToolResource currentTool)
        {
            var store = new FakeStore(currentProvider, currentTool);
            var catalog = new McpToolCatalog(store, adapter);
            var error = await Assert.ThrowsAsync<ToolResolutionException>(async () => await catalog.ResolveAsync([currentTool.Metadata.Name]));
            Assert.AreEqual(code, error.Code);
            var invocationError = await Assert.ThrowsAsync<ToolResolutionException>(async () =>
                await new ToolExecutionPipeline(new McpToolInvoker(store, adapter)).ExecuteAsync(new ToolExecutionContext
                {
                    ToolCallId = "governance-call",
                    InvocationId = "governance-invocation",
                    ToolId = currentTool.Metadata.Name,
                    ToolName = currentTool.Definition.ExternalId ?? currentTool.Metadata.Name,
                    ToolProviderId = currentProvider.Metadata.Name,
                    ExternalToolId = currentTool.Definition.ExternalId
                }));
            Assert.AreEqual(code, invocationError.Code);
        }
    }

    [TestMethod]
    public async Task ApprovalGovernanceIsRetainedAsProviderNeutralMetadata()
    {
        await using var host = new WebApplicationFactory<UtilitiesExtension::Program>();
        var provider = Provider();
        var baseline = Tool(provider.Metadata.Name);
        var tool = baseline with
        {
            Metadata = new ResourceMetadata { Name = "local.hash_compute" },
            Definition = baseline.Definition with { ExternalId = "hash_compute", RequiresApproval = true }
        };
        var configuration = new ConfigurationBuilder().Build();
        var adapter = new ToolProviderAdapter(
            new StubAepExtensionRegistrationResolver(),
            new ConfigurationToolProviderEnvironmentResolver(configuration),
            new TestHttpMessageHandlerFactory(host.Server.CreateHandler),
            NullLoggerFactory.Instance);
        var catalog = new McpToolCatalog(new FakeStore(provider, tool), adapter);

        var runtime = (await catalog.ResolveAsync([tool.Metadata.Name])).Single();

        Assert.IsTrue(runtime.RequiresApproval);
        Assert.IsFalse(runtime is AITool);
    }

    [TestMethod]
    public async Task AepContributionInvokesMcpThroughTheSameExecutionPipeline()
    {
        await using var host = new WebApplicationFactory<UtilitiesExtension::Program>();
        var provider = Provider() with
        {
            Metadata = new ResourceMetadata { Name = "utilities" },
            ScopeRef = WorkspaceScope,
            Definition = new ToolProviderProperties
            {
                DisplayName = "Utilities AEP",
                ProviderType = ToolProviderType.Aep,
                Aep = new AepToolProviderConfiguration { ExtensionId = "Agentstration.Extensions.Utilities" }
            }
        };
        var tool = Tool(provider.Metadata.Name) with
        {
            Metadata = new ResourceMetadata { Name = "utilities.hash.compute" },
            Definition = Tool(provider.Metadata.Name).Definition with { ExternalId = "hash.compute" }
        };
        var registration = Registration("utilities-extension", "Agentstration.Extensions.Utilities");
        var store = new FakeStore(provider, tool, registration);
        var adapter = new ToolProviderAdapter(
            new ResourceAepExtensionRegistrationResolver(store),
            new ConfigurationToolProviderEnvironmentResolver(new ConfigurationBuilder().Build()),
            new TestHttpMessageHandlerFactory(host.Server.CreateHandler),
            NullLoggerFactory.Instance);
        var descriptor = (await new McpToolCatalog(store, adapter).ResolveAsync([tool.Metadata.Name])).Single();
        var pipeline = new ToolExecutionPipeline(new McpToolInvoker(store, adapter));
        var context = Context(descriptor) with
        {
            Arguments = JsonSerializer.SerializeToElement(new { text = "agentstration" })
        };

        var result = await pipeline.ExecuteAsync(context);

        Assert.AreEqual("utilities", descriptor.ProviderId);
        Assert.AreEqual("hash.compute", descriptor.ExternalId);
        Assert.IsNotNull(result);
        StringAssert.Contains(result.Value.GetRawText(), UtilitiesExtension::Agentstration.Extensions.Utilities.UtilityTools.ComputeHash("agentstration"));
    }

    [TestMethod]
    public async Task AepMcpToolFailureIsPropagatedInsteadOfReturnedAsSuccessfulOutput()
    {
        await using var host = new WebApplicationFactory<UtilitiesExtension::Program>();
        var provider = AepProvider();
        var baseline = Tool(provider.Metadata.Name);
        var tool = baseline with
        {
            Metadata = new ResourceMetadata { Name = "utilities.json.compact" },
            ScopeRef = WorkspaceScope,
            Definition = baseline.Definition with { ExternalId = "json.compact" }
        };
        var registration = Registration("utilities-extension", provider.Definition.Aep!.ExtensionId);
        var store = new FakeStore(provider, tool, registration);
        var adapter = new ToolProviderAdapter(
            new ResourceAepExtensionRegistrationResolver(store),
            new ConfigurationToolProviderEnvironmentResolver(new ConfigurationBuilder().Build()),
            new TestHttpMessageHandlerFactory(host.Server.CreateHandler),
            NullLoggerFactory.Instance);
        var descriptor = (await new McpToolCatalog(store, adapter).ResolveAsync([tool.Metadata.Name])).Single();
        var pipeline = new ToolExecutionPipeline(new McpToolInvoker(store, adapter));

        var exception = await Assert.ThrowsAsync<ToolResolutionException>(async () =>
            await pipeline.ExecuteAsync(Context(descriptor) with
            {
                Arguments = JsonSerializer.SerializeToElement(new { json = "{" })
            }));

        Assert.AreEqual("mcp_tool_failed", exception.Code);
        StringAssert.Contains(exception.Message, "json_compact");
    }

    [TestMethod]
    public async Task AepDiscoveryAndMcpCallsUseThePairedRegistrationCredential()
    {
        await using var host = new WebApplicationFactory<UtilitiesExtension::Program>();
        const string token = "paired-extension-token";
        var provider = AepProvider();
        var registration = Registration(
            "utilities-extension",
            "Agentstration.Extensions.Utilities",
            AepTransportAuthenticationMode.StaticBearer,
            new ResourceReference("utilities-token", WorkspaceScope));
        var store = new FakeStore(provider, registration);
        var recorder = new AuthorizationRecorder(token);
        var secrets = new TestSecretResolver(token);
        var adapter = new ToolProviderAdapter(
            new ResourceAepExtensionRegistrationResolver(store, secrets),
            new ConfigurationToolProviderEnvironmentResolver(new ConfigurationBuilder().Build()),
            new TestHttpMessageHandlerFactory(() => new RecordingAuthorizationHandler(host.Server.CreateHandler(), recorder)),
            NullLoggerFactory.Instance);

        var result = await adapter.DiscoverAsync(provider, default);

        Assert.IsGreaterThan(0, result.Tools.Count);
        Assert.IsTrue(recorder.Paths.Contains("/.well-known/aep", StringComparer.Ordinal));
        Assert.IsTrue(recorder.Paths.Contains("/mcp", StringComparer.Ordinal));
        Assert.IsTrue(recorder.AllRequestsAuthenticated);
        Assert.IsGreaterThanOrEqualTo(2, secrets.ResolutionCount);
        Assert.AreEqual(ExtensionKinds.ExtensionRegistration, secrets.LastContext?.Consumer.Kind);
        Assert.AreEqual("utilities-extension", secrets.LastContext?.Consumer.Name);
    }

    [TestMethod]
    public async Task AepRegistrationResolutionFailsClosedForMissingDisabledOrAmbiguousMatches()
    {
        var provider = AepProvider();

        await AssertResolutionCodeAsync("extension_unavailable", new FakeStore());
        await AssertResolutionCodeAsync("extension_unavailable", new FakeStore(
            Registration("disabled", provider.Definition.Aep!.ExtensionId) with
            {
                Definition = Registration("disabled", provider.Definition.Aep.ExtensionId).Definition with { Enabled = false }
            }));
        await AssertResolutionCodeAsync("extension_unavailable", new FakeStore(
            Registration("sibling", provider.Definition.Aep!.ExtensionId) with
            {
                ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("22222222-2222-2222-2222-222222222222"))
            }));
        await AssertResolutionCodeAsync("extension_registration_ambiguous", new FakeStore(
            Registration("first", provider.Definition.Aep!.ExtensionId),
            Registration("second", provider.Definition.Aep.ExtensionId)));

        static async Task AssertResolutionCodeAsync(string expected, IResourceStore store)
        {
            var exception = await Assert.ThrowsAsync<ToolResolutionException>(async () =>
                await new ResourceAepExtensionRegistrationResolver(store).ResolveAsync(AepProvider(), default));
            Assert.AreEqual(expected, exception.Code);
        }
    }

    [TestMethod]
    public async Task AepDiscoveryRejectsAManifestWithAnotherExtensionIdentity()
    {
        await using var host = new WebApplicationFactory<UtilitiesExtension::Program>();
        var provider = AepProvider() with
        {
            Definition = AepProvider().Definition with
            {
                Aep = new AepToolProviderConfiguration { ExtensionId = "Agentstration.Extensions.Unexpected" }
            }
        };
        var store = new FakeStore(provider, Registration("unexpected", provider.Definition.Aep!.ExtensionId));
        var adapter = new ToolProviderAdapter(
            new ResourceAepExtensionRegistrationResolver(store),
            new ConfigurationToolProviderEnvironmentResolver(new ConfigurationBuilder().Build()),
            new TestHttpMessageHandlerFactory(host.Server.CreateHandler),
            NullLoggerFactory.Instance);

        var exception = await Assert.ThrowsAsync<AepProtocolException>(async () =>
            await adapter.DiscoverAsync(provider, default));

        Assert.AreEqual("extension_identity_mismatch", exception.Code);
    }

    [TestMethod]
    public async Task AepDiscoverySurfacesAuthenticationFailure()
    {
        var provider = AepProvider();
        var registration = Registration(
            "utilities-extension",
            provider.Definition.Aep!.ExtensionId,
            AepTransportAuthenticationMode.StaticBearer,
            new ResourceReference("utilities-token", WorkspaceScope));
        var store = new FakeStore(provider, registration);
        var adapter = new ToolProviderAdapter(
            new ResourceAepExtensionRegistrationResolver(store, new TestSecretResolver("rejected-token")),
            new ConfigurationToolProviderEnvironmentResolver(new ConfigurationBuilder().Build()),
            new TestHttpMessageHandlerFactory(() => new AuthenticationFailureHandler()),
            NullLoggerFactory.Instance);

        var exception = await Assert.ThrowsAsync<AepProtocolException>(async () =>
            await adapter.DiscoverAsync(provider, default));

        Assert.AreEqual("authentication_failed", exception.Code);
    }

    private static ToolExecutionContext Context(IAgentTool tool) => new()
    {
        ToolCallId = "call-1",
        InvocationId = "invocation-1",
        ToolId = tool.Id,
        ToolName = tool.Name,
        ToolProviderId = tool.ProviderId,
        ExternalToolId = tool.ExternalId
    };

    private static ToolProviderAdapter Adapter(WebApplicationFactory<global::Program> host)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new ToolProviderAdapter(new StubAepExtensionRegistrationResolver(), new ConfigurationToolProviderEnvironmentResolver(configuration), new TestHttpMessageHandlerFactory(host.Server.CreateHandler), NullLoggerFactory.Instance);
    }

    private static ToolProviderResource AepProvider() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = "utilities" },
        ScopeRef = WorkspaceScope,
        Definition = new ToolProviderProperties
        {
            DisplayName = "Utilities AEP",
            ProviderType = ToolProviderType.Aep,
            Aep = new AepToolProviderConfiguration { ExtensionId = "Agentstration.Extensions.Utilities" }
        }
    };

    private static ExtensionRegistrationResource Registration(
        string name,
        string extensionId,
        AepTransportAuthenticationMode authenticationMode = AepTransportAuthenticationMode.None,
        ResourceReference? credential = null) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ExtensionKinds.ExtensionRegistration,
        Metadata = new ResourceMetadata { Name = name },
        ScopeRef = WorkspaceScope,
        Definition = new ExtensionRegistrationProperties
        {
            DisplayName = name,
            Endpoint = new Uri("http://extension/"),
            ExpectedExtensionId = extensionId,
            Enabled = true,
            AuthenticationMode = authenticationMode,
            EnrollmentMode = AepEnrollmentMode.PairingCode,
            Credential = credential
        }
    };

    private static ToolProviderResource Provider() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = "local" },
        Definition = new ToolProviderProperties { DisplayName = "Local MCP", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration { Transport = McpToolProviderTransport.StreamableHttp, Endpoint = new Uri("http://localhost/mcp") } }
    };

    private static ToolResource Tool(string providerId) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.Tool,
        Metadata = new ResourceMetadata { Name = "local.sample_tool" },
        Definition = new ToolResourceProperties
        {
            DisplayName = "List workspaces",
            Provider = new ResourceReference(providerId),
            ExternalId = "sample_tool",
            Enabled = true,
            Discovery = new ToolDiscoveryState { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch },
            Schema = new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) }
        }
    };

    private sealed class TestHttpMessageHandlerFactory(Func<HttpMessageHandler> handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler();
    }

    private sealed class FakeStore(params Resource[] resources) : IResourceStore
    {
        private readonly IReadOnlyList<Resource> values = resources;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => Task.FromResult(values.OfType<T>().Where(value => value.Kind == key.Kind && value.Namespace == key.Namespace && value.Name == key.Name).Select(Stored).SingleOrDefault());
        public Task<IReadOnlyList<StoredResource<T>>> ListVisibleAsync<T>(ResourceScopeRef targetScopeRef, string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.OfType<T>().Where(value => value.Kind == kind && (value.ScopeRef == ResourceScopeRef.Instance || value.ScopeRef == targetScopeRef)).Skip(skip).Take(take).Select(Stored).ToArray());
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => Task.FromResult<IReadOnlyList<StoredResource<T>>>(values.OfType<T>().Where(value => value.Kind == kind).Skip(skip).Take(take).Select(Stored).ToArray());
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException();
        private static StoredResource<T> Stored<T>(T value) where T : Resource => new(value, "test", DateTimeOffset.UnixEpoch);
    }

    private sealed class StubAepExtensionRegistrationResolver : IAepExtensionRegistrationResolver
    {
        public Task<ResolvedAepExtension> ResolveAsync(ToolProviderResource provider, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestSecretResolver(string token) : ISecretResolver
    {
        public int ResolutionCount { get; private set; }
        public SecretResolutionContext? LastContext { get; private set; }

        public Task<ResolvedSecret?> ResolveAsync(SecretReference secret, SecretResolutionContext context, CancellationToken cancellationToken = default)
        {
            ResolutionCount++;
            LastContext = context;
            return Task.FromResult<ResolvedSecret?>(new ResolvedSecret(
                secret.Address,
                new ResourceAddress(ResourceNamespace.Default, "Vault", "test"),
                new SecretValue(Encoding.UTF8.GetBytes(token))));
        }
    }

    private sealed class AuthorizationRecorder(string expectedToken)
    {
        public List<string> Paths { get; } = [];
        public bool AllRequestsAuthenticated { get; private set; } = true;

        public void Record(HttpRequestMessage request)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            AllRequestsAuthenticated &= request.Headers.Authorization?.Scheme == "Bearer"
                && request.Headers.Authorization.Parameter == expectedToken;
        }
    }

    private sealed class RecordingAuthorizationHandler(HttpMessageHandler inner, AuthorizationRecorder recorder) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            recorder.Record(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class AuthenticationFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"authentication_failed\",\"message\":\"A valid AEP workload credential is required.\"}}",
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
