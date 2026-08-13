using System.Net.Http;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class McpToolCatalogTests
{
    [TestMethod]
    public async Task GenericHttpProviderDiscoversAndInvokesOfficialMcpTool()
    {
        await using var host = new WebApplicationFactory<global::Program>();
        var provider = Provider();
        var tool = Tool(provider.Metadata.Name);
        var adapter = Adapter(host);
        var catalog = new McpToolCatalog(new FakeStore(provider, tool), adapter);

        var discovery = await adapter.DiscoverAsync(provider, default);
        var runtime = (await catalog.ResolveAsync([tool.Metadata.Name])).Single();
        var result = await runtime.InvokeAsync(null);

        Assert.IsTrue(discovery.Tools.Any(value => value.ExternalId == "list_workspaces"));
        Assert.IsTrue(discovery.Capabilities["tools"]);
        Assert.IsInstanceOfType<AITool>(runtime.GetService(typeof(AITool)));
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task GovernanceBlocksDisabledProviderToolAndUnavailableTool()
    {
        await using var host = new WebApplicationFactory<global::Program>();
        var adapter = Adapter(host);
        var provider = Provider();
        var tool = Tool(provider.Metadata.Name);

        await AssertCodeAsync("tool_provider_disabled", provider with { Definition = provider.Definition with { Enabled = false } }, tool);
        await AssertCodeAsync("tool_disabled", provider, tool with { Definition = tool.Definition with { Enabled = false } });
        await AssertCodeAsync("tool_unavailable", provider, tool with { Definition = tool.Definition with { Discovery = tool.Definition.Discovery! with { Available = false } } });

        async Task AssertCodeAsync(string code, ToolProviderResource currentProvider, ToolResource currentTool)
        {
            var catalog = new McpToolCatalog(new FakeStore(currentProvider, currentTool), adapter);
            var error = await Assert.ThrowsAsync<ToolResolutionException>(async () => await catalog.ResolveAsync([currentTool.Metadata.Name]));
            Assert.AreEqual(code, error.Code);
        }
    }

    private static ToolProviderAdapter Adapter(WebApplicationFactory<global::Program> host)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new ToolProviderAdapter(new ConfigurationAepExtensionEndpointResolver(configuration), new ConfigurationToolProviderEnvironmentResolver(configuration), new TestHttpClientFactory(host.Server.CreateHandler()), NullLoggerFactory.Instance);
    }

    private static ToolProviderResource Provider() => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = "local" },
        Definition = new ToolProviderProperties { DisplayName = "Local MCP", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration { Transport = McpToolProviderTransport.StreamableHttp, Endpoint = new Uri("http://localhost/mcp") } }
    };

    private static ToolResource Tool(string providerId) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.Tool,
        Metadata = new ResourceMetadata { Name = "local.list_workspaces" },
        Definition = new ToolResourceProperties
        {
            DisplayName = "List workspaces", Provider = new ResourceReference(providerId), ExternalId = "list_workspaces", Enabled = true,
            Discovery = new ToolDiscoveryState { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch },
            Schema = new ToolSchema { Input = JsonSerializer.SerializeToElement(new { type = "object" }) }
        }
    };

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class FakeStore(params Resource[] resources) : IControlPlaneStore
    {
        private readonly Dictionary<ResourceKey, Resource> values = resources.ToDictionary(value => new ResourceKey(value.Kind, value.Metadata.Name));
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken cancellationToken) where T : Resource => Task.FromResult(values.TryGetValue(key, out var value) && value is T typed ? new StoredResource<T>(typed, "test", DateTimeOffset.UnixEpoch) : null);
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
