using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowDesignerResourceProviderTests
{
    [TestMethod]
    public async Task AgentListKeepsTechnicalIdSeparateFromDisplayName()
    {
        var client = new MockApiClient(TimeProvider.System);
        var provider = new FlowDesignerResourceProvider(client, client, null!, null!, null!);

        var scope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var agents = await provider.GetAgentsAsync(scope, CancellationToken.None);

        var agent = agents.Single(item => item.Name == "dotnet-expert");
        Assert.AreEqual(".NET Expert", agent.DisplayName);
        Assert.AreEqual(scope, agent.ScopeRef);
    }

    [TestMethod]
    public async Task AgentstrationToolKeepsItsExactSharedInputContractReference()
    {
        var client = new MockApiClient(TimeProvider.System);
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var reference = new SchemaVersionReference
        {
            Name = "notify-request",
            Version = "1.2.0",
            Namespace = new("shared.schemas"),
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"))
        };
        var tool = new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            ScopeRef = reference.ScopeRef,
            Metadata = new ResourceMetadata { Name = "agentstration.notify" },
            Definition = new ToolResourceProperties
            {
                DisplayName = "Notify",
                Provider = new ResourceReference(AgentstrationToolProvider.Name),
                ExternalId = "notify",
                Discovery = new ToolDiscoveryState
                {
                    Available = true,
                    FirstSeenAt = DateTimeOffset.UnixEpoch,
                    LastSeenAt = DateTimeOffset.UnixEpoch
                },
                Schema = new ToolSchema { Input = schema }
            }
        };
        var definition = new ToolDefinitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolDefinition,
            Metadata = new ResourceMetadata { Name = "notify" },
            Definition = new ToolDefinitionProperties
            {
                DisplayName = "Notify",
                InputSchema = schema,
                InputSchemaBinding = new SchemaBinding { Reference = reference },
                Flow = new ToolDefinitionFlowTarget { Name = "notify" }
            }
        };
        var provider = new FlowDesignerResourceProvider(
            client, client, new ToolsClientStub([tool]), new ToolDefinitionsClientStub([definition]), null!);

        var tools = await provider.GetToolsAsync(reference.ScopeRef!.Value, CancellationToken.None);

        Assert.AreEqual(reference, tools.Single().InputSchemaReference);
    }

    private sealed class ToolsClientStub(IReadOnlyList<ToolResource> tools) : IToolsClient
    {
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) => Task.FromResult(tools);
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ToolDefinitionsClientStub(IReadOnlyList<ToolDefinitionResource> definitions) : IToolDefinitionsClient
    {
        public Task<IReadOnlyList<ToolDefinitionResource>> GetAsync(CancellationToken cancellationToken) => Task.FromResult(definitions);
        public Task<ResourceSnapshot<ToolDefinitionResource>> GetAsync(string name, ResourceNamespace @namespace, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> CreateAsync(CreateToolDefinitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> UpdateAsync(string name, ResourceNamespace @namespace, PutToolDefinitionRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> SetEnabledAsync(string name, ResourceNamespace @namespace, bool enabled, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string name, ResourceNamespace @namespace, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
