extern alias UtilitiesExtension;

using System.Net.Http;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
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
            new ConfigurationAepExtensionEndpointResolver(configuration),
            new ConfigurationToolProviderEnvironmentResolver(configuration),
            new TestHttpClientFactory(host.Server.CreateHandler()),
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
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Extensions:Agentstration.Extensions.Utilities:Endpoint"] = "http://extension/"
        }).Build();
        var adapter = new ToolProviderAdapter(
            new ConfigurationAepExtensionEndpointResolver(configuration),
            new ConfigurationToolProviderEnvironmentResolver(configuration),
            new TestHttpClientFactory(host.Server.CreateHandler()),
            NullLoggerFactory.Instance);
        var provider = Provider() with
        {
            Metadata = new ResourceMetadata { Name = "utilities" },
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
        var store = new FakeStore(provider, tool);
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
            DisplayName = "List workspaces",
            Provider = new ResourceReference(providerId),
            ExternalId = "list_workspaces",
            Enabled = true,
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
