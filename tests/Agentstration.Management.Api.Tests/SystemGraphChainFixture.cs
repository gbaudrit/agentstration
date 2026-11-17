using System.Text.Json;
using Agentstration.Agents;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Management.Tests;

internal sealed record SystemGraphChainFixture(AgentResource Agent, AgentRevision Revision, ToolResource Tool,
    ToolProviderResource Provider, ToolSetResource ToolSet, ToolDefinitionVersionResource Relay,
    KnowledgeSourceResource Knowledge, DataSourceResource DataSource, ConnectedServiceResource Service)
{
    internal static async Task<SystemGraphChainFixture> SeedAsync(IResourceStore store, ResourceScopeRef owner, ResourceNamespace ns, CancellationToken ct)
    {
        var shared = new ResourceNamespace(ns.Value + "-shared");
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
        async Task<T> Save<T>(T value, bool immutable = false) where T : Resource
        {
            value = value with { ApiVersion = ResourceApiVersions.CoreV1, ScopeRef = owner, Generation = 1 };
            return immutable ? (await store.CreateImmutableAsync(value, ct)).Value
                : (await store.PutExactAsync(owner, value, null, true, ct)).Value;
        }
        var service = await Save(new ConnectedServiceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            Metadata = new() { Name = "origin-service", Namespace = shared },
            Definition = new()
            {
                DisplayName = "Origin service",
                Options = Json(new { privateValue = "private-connection-value" }),
                Type = new() { Type = new("fixture-type", owner, shared), TypeUid = Guid.NewGuid(), Version = "1.0.0", SchemaDigest = "type-digest" }
            }
        });
        var operation = await Save(new ConnectedServiceOperationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceOperation,
            Metadata = new() { Name = "origin-operation", Namespace = shared },
            Enabled = true,
            ObservedState = new() { Availability = ConnectedServiceOperationAvailability.Available, ObservedAt = DateTimeOffset.UnixEpoch },
            Definition = new()
            {
                DisplayName = "Origin operation",
                ExternalId = "fixture-operation",
                Definition = Json(new { }),
                DefinitionDigest = "operation-digest",
                Service = new() { Service = new(service.Address, owner), ServiceUid = service.Uid, Generation = service.Generation },
                Type = new() { Type = new("fixture-operation-type", owner, shared), TypeUid = Guid.NewGuid(), Version = "1.0.0", SchemaDigest = "operation-type-digest" },
                Provenance = new() { Discovery = new(owner, shared, "ConnectedServiceOperationDiscovery", "fixture"), DiscoveryUid = Guid.NewGuid(), ManifestDigest = "fixture-digest", DiscoveredAt = DateTimeOffset.UnixEpoch }
            }
        });
        var data = await Save(new DataSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSource,
            Metadata = new() { Name = "source-data", Namespace = ns },
            Definition = new()
            {
                DisplayName = "Source data",
                Dataset = new(null, new StorageContentPolicy(),
                    new(new(new(owner, shared, operation.Kind, operation.Name), operation.Uid, operation.Generation, operation.Definition.DefinitionDigest), Json(new { })))
            }
        });
        var knowledge = await Save(new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "source-knowledge", Namespace = ns },
            Definition = new() { DisplayName = "Source knowledge", DataSources = [new() { Name = "input", DataSource = new(data.Name, owner, ns) }] },
            Composition = new()
            {
                ProjectionFlow = new("fixture-projection", ns, "1.0.0", false, null, null),
                DependencyHash = "composition-digest",
                DataSources = [new() { BindingName = "input", DataSourceScopeRef = owner, DataSourceNamespace = ns,
                    DataSourceName = data.Name, DataSourceUid = data.Uid, DataSourceGeneration = data.Generation }]
            }
        });
        var provider = await Save(new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            Metadata = new() { Name = "paired-89ab01", Namespace = ns },
            Definition = new() { DisplayName = "Readable extension", ProviderType = ToolProviderType.Aep, Aep = new() { Extension = new("paired-89ab01", owner, ns) } }
        });
        var definition = await Save(new ToolDefinitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolDefinition,
            Metadata = new() { Name = "knowledge-access", Namespace = ns },
            Definition = new RelayToolDefinitionProperties
            {
                DisplayName = "Knowledge access",
                Version = "1.0.0",
                Target = new() { Name = "native-knowledge-access", ScopeRef = owner, Namespace = ns, Uid = Guid.NewGuid(), Generation = 1 },
                Bindings = [new LiteralToolArgumentBinding { Path = "/source", Value = Json(new { name = knowledge.Name, @namespace = ns.Value, scopeRef = owner.Value, uid = knowledge.Uid }) }]
            }
        });
        var relay = await Save(new ToolDefinitionVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolDefinitionVersion,
            Metadata = new() { Name = "knowledge-access-v1", Namespace = ns, Annotations = new Dictionary<string, string> { ["agentstration.io/knowledge-source-uid"] = knowledge.Uid.ToString("D") } },
            DefinitionUid = definition.Uid,
            DefinitionName = definition.Name,
            Definition = definition.Definition,
            PublishedAt = DateTimeOffset.UnixEpoch
        }, immutable: true);
        var tool = await Save(new ToolResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.Tool,
            Metadata = new() { Name = "paired-89ab01-access", Namespace = ns },
            Definition = new()
            {
                DisplayName = "Knowledge access Tool",
                Provider = new(provider.Name, owner, ns),
                Version = "1.0.0",
                Metadata = new Dictionary<string, JsonElement> { ["agentstration.toolDefinitionUid"] = Json(definition.Uid) }
            }
        });
        var toolSet = await Save(new ToolSetResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSet,
            Metadata = new() { Name = "knowledge-access-set", Namespace = ns },
            Definition = new() { DisplayName = "Knowledge access set", Version = "1.0.0", Members = [new() { Tool = new(tool.Name, owner, ns), Capability = "knowledge.read", Route = "read" }] }
        });
        _ = await Save(new ToolSetVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSetVersion,
            Metadata = new() { Name = "knowledge-access-set-v1", Namespace = ns },
            ToolSetUid = toolSet.Uid,
            ToolSetName = toolSet.Name,
            ToolSetGeneration = toolSet.Generation,
            Version = "1.0.0",
            DefinitionHash = "set-digest",
            PublishedAt = DateTimeOffset.UnixEpoch,
            Members = [new() { Capability = "knowledge.read", Route = "read", ToolName = tool.Name, ToolNamespace = ns, ToolScopeRef = owner,
                ToolUid = tool.Uid, ToolGeneration = tool.Generation, ProviderName = provider.Name, ProviderNamespace = ns, ProviderScopeRef = owner,
                ProviderUid = provider.Uid, ProviderGeneration = provider.Generation, ExternalToolId = "knowledge.read", InputSchema = Json(new { }) }]
        }, immutable: true);
        var agent = await Save(new AgentResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.Agent,
            Metadata = new() { Name = "consumer-agent", Namespace = ns },
            Definition = new()
            {
                DisplayName = "Consumer agent",
                Instructions = "private-agent-instructions",
                ModelProfile = new("fixture-model", owner, ns),
                Tools = [new(tool.Name, owner, ns)],
                ToolSets = [new() { ToolSet = new(toolSet.Name, owner, ns), Version = "1.0.0" }]
            }
        });
        var revision = await Save(new AgentRevision
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.AgentRevision,
            Metadata = new() { Name = "consumer-agent-v1", Namespace = ns },
            AgentUid = agent.Uid,
            AgentName = agent.Name,
            AgentNamespace = ns,
            AgentVersion = 1,
            DefinitionHash = "agent-digest",
            CreatedAt = DateTimeOffset.UnixEpoch,
            ProvisioningState = ProvisioningState.Succeeded,
            Definition = new()
            {
                AgentId = agent.Uid,
                AgentKey = agent.Name,
                DisplayName = "Consumer agent",
                Description = "",
                AgentVersion = 1,
                EffectiveInstructions = "private-agent-instructions",
                ModelProfileName = "fixture-model",
                RuntimeProfileName = "maf-builtin",
                EffectiveToolNames = [tool.Name],
                MiddlewareIds = [],
                ContextProviderIds = [],
                Capabilities = [],
                Handler = "prompt-agent",
                DefinitionHash = "agent-digest",
                ToolPins = [new(tool.Uid, owner, ns, tool.Kind, tool.Name, tool.Generation, "1.0.0")],
                ToolSetAssignments = [new() { ToolSetUid = toolSet.Uid, ToolSetScopeRef = owner, ToolSetName = toolSet.Name, ToolSetNamespace = ns,
                    ToolSetGeneration = toolSet.Generation, Version = "1.0.0", DefinitionHash = "set-digest", Members = ["read"] }]
            }
        }, immutable: true);
        // Sharing a dependency must not pull a peer Agent into another Agent's view.
        _ = await Save(agent with { Uid = Guid.Empty, Metadata = new() { Name = "unrelated-peer-agent", Namespace = ns } });
        return new(agent, revision, tool, provider, toolSet, relay, knowledge, data, service);
    }
}
