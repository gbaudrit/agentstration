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
        var tool = Tool(provider.Id);
        var adapter = Adapter(host);
        var catalog = new McpToolCatalog(new FakeStore(provider, tool), adapter);

        var discovery = await adapter.DiscoverAsync(provider, default);
        var runtime = (await catalog.ResolveAsync([tool.Id])).Single();
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
        var tool = Tool(provider.Id);

        await AssertCodeAsync("tool_provider_disabled", provider with { Properties = provider.Properties with { Enabled = false } }, tool);
        await AssertCodeAsync("tool_disabled", provider, tool with { Properties = tool.Properties with { Enabled = false } });
        await AssertCodeAsync("tool_unavailable", provider, tool with { Properties = tool.Properties with { Discovery = tool.Properties.Discovery! with { Available = false } } });

        async Task AssertCodeAsync(string code, ToolProviderResource currentProvider, ToolResource currentTool)
        {
            var catalog = new McpToolCatalog(new FakeStore(currentProvider, currentTool), adapter);
            var error = await Assert.ThrowsAsync<ToolResolutionException>(async () => await catalog.ResolveAsync([currentTool.Id]));
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
        Id = "/resourceGroups/default/providers/Agentstration.Tools/toolProviders/local", Name = "local", Type = AgentstrationResourceTypes.ToolProviders,
        ApiVersion = ManagementApiVersions.V20260801, ResourceGroup = "default",
        Properties = new ToolProviderProperties { DisplayName = "Local MCP", ProviderType = ToolProviderType.Mcp, Mcp = new McpToolProviderConfiguration { Transport = McpToolProviderTransport.StreamableHttp, Endpoint = new Uri("http://localhost/mcp") } }
    };

    private static ToolResource Tool(string providerId) => new()
    {
        Id = "/resourceGroups/default/providers/Agentstration.Tools/tools/local.list_workspaces", Name = "local.list_workspaces", Type = AgentstrationResourceTypes.Tools,
        ApiVersion = ManagementApiVersions.V20260801, ResourceGroup = "default",
        Properties = new ToolResourceProperties
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
        private readonly Dictionary<string, Resource> values = resources.ToDictionary(value => value.Id);
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredResource<T>?> GetAsync<T>(string resourceId, CancellationToken cancellationToken) where T : Resource => Task.FromResult(values.TryGetValue(resourceId, out var value) && value is T typed ? new StoredResource<T>(typed, "test", DateTimeOffset.UnixEpoch) : null);
        public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string resourceType, string? resourceGroup, int skip, int take, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken cancellationToken) where T : Resource => throw new NotSupportedException();
        public Task DeleteAsync(string resourceId, string? ifMatch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
