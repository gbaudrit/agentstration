using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Knowledge;

public sealed class KnowledgePlatformResourceProvisioner(IResourceStore store, FlowService flows, TimeProvider timeProvider)
{
    public const string IngestionToolSetName = "knowledge-ingestion-builtin";
    public const string RetrievalToolSetName = "knowledge-retrieval-builtin";
    public const string IngestionFlowName = "knowledge-ingestion-builtin";
    public const string RetrievalFlowName = "knowledge-retrieval-builtin";
    public const string Version = "1.0.0";

    public async Task EnsureAsync(ResourceScopeRef workspaceScope, CancellationToken cancellationToken)
    {
        if (workspaceScope is not { Kind: ResourceScopeKind.Workspace, TargetId: { } workspaceId })
            throw new InvalidOperationException("Built-in Knowledge resources require a Workspace scope.");
        await EnsureToolSetAsync(workspaceScope, IngestionToolSetName,
            "Knowledge ingestion · Built-in", "Local-first import of governed durable Artifacts.",
            [(KnowledgeFlowContracts.Ingestion, KnowledgeBuiltinToolNames.IngestionImport)], cancellationToken);
        await EnsureToolSetAsync(workspaceScope, RetrievalToolSetName,
            "Knowledge retrieval · Built-in", "Local-first deterministic search, query, and read over Snapshot Artifacts.",
            [
                (KnowledgeFlowContracts.Search, KnowledgeBuiltinToolNames.RetrievalSearch),
                (KnowledgeFlowContracts.Query, KnowledgeBuiltinToolNames.RetrievalQuery),
                (KnowledgeFlowContracts.Read, KnowledgeBuiltinToolNames.RetrievalRead)
            ], cancellationToken);
        await EnsureIngestionFlowAsync(new WorkspaceId(workspaceId), cancellationToken);
        await EnsureRetrievalFlowAsync(new WorkspaceId(workspaceId), cancellationToken);
    }

    private async Task EnsureToolSetAsync(ResourceScopeRef scope, string name, string displayName, string description,
        IReadOnlyList<(string Capability, string Tool)> routes, CancellationToken cancellationToken)
    {
        var address = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ToolResourceKinds.ToolSet, name);
        var toolSet = await store.GetExactAsync<ToolSetResource>(address, cancellationToken);
        if (toolSet is null)
        {
            toolSet = await CreateOrReadAsync(new ToolSetResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolSet,
                Metadata = new ResourceMetadata
                {
                    Name = name,
                    Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
                },
                ScopeRef = scope,
                Generation = 1,
                Status = Succeeded(),
                Definition = new ToolSetProperties
                {
                    DisplayName = displayName,
                    Description = description,
                    Category = new ResourceReference("knowledge-source", scope, ResourceNamespace.Default),
                    Version = Version,
                    Publish = true,
                    Members = routes.Select(route => new ToolSetMember
                    {
                        Tool = new ResourceReference(AgentstrationToolProvider.ToolResourceName(route.Tool), scope, ResourceNamespace.Default),
                        Capability = route.Capability,
                        Route = "default"
                    }).ToArray()
                }
            }, scope, cancellationToken);
        }

        var versionName = VersionResourceName(name, Version);
        var versionAddress = ScopedResourceAddress.Create(scope, ResourceNamespace.Default, ToolResourceKinds.ToolSetVersion, versionName);
        if (await store.GetExactAsync<ToolSetVersionResource>(versionAddress, cancellationToken) is not null) return;
        var members = new List<PublishedToolSetMember>(routes.Count);
        foreach (var route in routes)
        {
            var toolName = AgentstrationToolProvider.ToolResourceName(route.Tool);
            var tool = await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(
                scope, ResourceNamespace.Default, ToolResourceKinds.Tool, toolName), cancellationToken)
                ?? throw new InvalidOperationException($"Built-in Knowledge Tool '{toolName}' was not projected.");
            var provider = tool.Value.Definition.Provider?.Resolve(ResourceNamespace.Default, ToolResourceKinds.ToolProvider)
                ?? throw new InvalidOperationException($"Built-in Knowledge Tool '{toolName}' has no provider.");
            members.Add(new PublishedToolSetMember
            {
                Capability = route.Capability,
                Route = "default",
                ToolName = tool.Value.Name,
                ToolNamespace = tool.Value.Namespace,
                ToolUid = tool.Value.Uid,
                ToolGeneration = tool.Value.Generation,
                ProviderName = provider.Name,
                ProviderNamespace = provider.Namespace,
                ExternalToolId = tool.Value.Definition.ExternalId!,
                InputSchema = tool.Value.Definition.Schema!.Input.Clone(),
                OutputSchema = tool.Value.Definition.Schema.Output?.Clone(),
                RequiresApproval = tool.Value.Definition.RequiresApproval
            });
        }
        _ = await CreateOrReadAsync(new ToolSetVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolSetVersion,
            Metadata = new ResourceMetadata
            {
                Name = versionName,
                Annotations = new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            ScopeRef = scope,
            Generation = 1,
            Status = Succeeded(),
            ToolSetUid = toolSet.Value.Uid,
            ToolSetName = name,
            ToolSetGeneration = toolSet.Value.Generation,
            Version = Version,
            DefinitionHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(members))),
            PublishedAt = timeProvider.GetUtcNow(),
            Members = members
        }, scope, cancellationToken);
    }

    private async Task EnsureIngestionFlowAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        if (await flows.GetAsync(workspaceId, new(IngestionFlowName), cancellationToken) is not null) return;
        var input = KnowledgeBuiltinSchemas.IngestionInput;
        var output = KnowledgeBuiltinSchemas.IngestionOutput;
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = input,
            OutputSchema = output,
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", DisplayName = "Acquisition request", Schema = input },
                new ToolRouteFlowStepDefinition
                {
                    Name = "ingest", DisplayName = "Route ingestion",
                    ToolSet = new(IngestionToolSetName, Version),
                    Capability = KnowledgeFlowContracts.Ingestion,
                    Route = "default",
                    ArgumentsMapping = JsonSerializer.SerializeToElement("${input}")
                },
                new OutputFlowStepDefinition { Name = "output", DisplayName = "Acquisition manifest",
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.ingest.output}") }
            ],
            Transitions = [new("input-ingest", "input", "completed", "ingest"), new("ingest-output", "ingest", "completed", "output")]
        };
        await CreateAndPublishFlowAsync(workspaceId, IngestionFlowName, "Knowledge ingestion · Built-in",
            KnowledgeFlowContracts.Ingestion, null, graph, cancellationToken);
    }

    private async Task EnsureRetrievalFlowAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        if (await flows.GetAsync(workspaceId, new(RetrievalFlowName), cancellationToken) is not null) return;
        var input = KnowledgeBuiltinSchemas.RetrievalInput;
        var output = KnowledgeBuiltinSchemas.RetrievalOutput;
        ToolRouteFlowStepDefinition Route(string name, string displayName, string capability) => new()
        {
            Name = name, DisplayName = displayName, ToolSet = new(RetrievalToolSetName, Version),
            Capability = capability, Route = "default", ArgumentsMapping = JsonSerializer.SerializeToElement("${input}")
        };
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = input,
            OutputSchema = output,
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", DisplayName = "Retrieval request", Schema = input },
                new ConditionFlowStepDefinition { Name = "is-search", DisplayName = "Route search", Left = "${input.operation}", Operator = "equals", Right = "search" },
                Route("search", "Search Snapshot", KnowledgeFlowContracts.Search),
                new ConditionFlowStepDefinition { Name = "is-query", DisplayName = "Route query", Left = "${input.operation}", Operator = "equals", Right = "query" },
                Route("query", "Query Snapshot", KnowledgeFlowContracts.Query),
                Route("read", "Read Snapshot Artifact", KnowledgeFlowContracts.Read),
                new OutputFlowStepDefinition { Name = "search-output", OutputMapping = JsonSerializer.SerializeToElement("${steps.search.output}") },
                new OutputFlowStepDefinition { Name = "query-output", OutputMapping = JsonSerializer.SerializeToElement("${steps.query.output}") },
                new OutputFlowStepDefinition { Name = "read-output", OutputMapping = JsonSerializer.SerializeToElement("${steps.read.output}") }
            ],
            Transitions =
            [
                new("input-route", "input", "completed", "is-search"),
                new("route-search", "is-search", "true", "search"),
                new("route-non-search", "is-search", "false", "is-query"),
                new("route-query", "is-query", "true", "query"),
                new("route-read", "is-query", "false", "read"),
                new("search-output", "search", "completed", "search-output"),
                new("query-output", "query", "completed", "query-output"),
                new("read-output", "read", "completed", "read-output")
            ]
        };
        await CreateAndPublishFlowAsync(workspaceId, RetrievalFlowName, "Knowledge retrieval · Built-in",
            KnowledgeFlowContracts.Retrieval,
            string.Join(',', KnowledgeFlowContracts.Search, KnowledgeFlowContracts.Query, KnowledgeFlowContracts.Read), graph, cancellationToken);
    }

    private async Task CreateAndPublishFlowAsync(WorkspaceId workspaceId, string name, string displayName, string contract,
        string? capabilities, FlowGraphDefinition graph, CancellationToken cancellationToken)
    {
        var nodes = graph.Steps.Select(step => new FlowNode(step.Name, step switch
        {
            InputFlowStepDefinition => FlowNodeKind.Input,
            ConditionFlowStepDefinition => FlowNodeKind.Condition,
            ToolRouteFlowStepDefinition => FlowNodeKind.Function,
            OutputFlowStepDefinition => FlowNodeKind.Output,
            _ => FlowNodeKind.Custom
        })).ToArray();
        var metadata = new Dictionary<string, string>
        {
            ["systemManaged"] = "true",
            ["systemKind"] = "KnowledgeFlow",
            [ResourceProvenanceAnnotations.BuiltIn] = "true",
            [KnowledgeFlowContracts.MetadataKey] = contract
        };
        if (capabilities is not null) metadata[KnowledgeFlowContracts.CapabilitiesMetadataKey] = capabilities;
        try
        {
            _ = await flows.CreateAsync(workspaceId, new CreateFlowCommand(name,
                $"Built-in local-first implementation of {contract}.", Version, true,
                new WorkflowFlowDefinition(graph.EntryStep, nodes,
                    graph.Transitions.Select(value => new FlowEdge(value.FromStep, value.ToStep)).ToArray(),
                    graph.Steps.OfType<OutputFlowStepDefinition>().Select(value => value.Name).ToArray()),
                metadata, graph, displayName), cancellationToken);
            _ = await flows.PublishVersionAsync(workspaceId, new(name), Version, true, cancellationToken,
                "Built-in local-first Knowledge contract implementation.");
        }
        catch (FlowConcurrencyException)
        {
            if (await flows.GetAsync(workspaceId, new(name), cancellationToken) is null) throw;
        }
    }

    private async Task<StoredResource<T>> CreateOrReadAsync<T>(T resource, ResourceScopeRef scope, CancellationToken cancellationToken)
        where T : Resource
    {
        try { return await store.PutExactAsync(scope, resource, null, true, cancellationToken); }
        catch (ResourceConcurrencyException)
        {
            var existing = await store.GetExactAsync<T>(ScopedResourceAddress.Create(
                scope, resource.Namespace, resource.Kind, resource.Name), cancellationToken);
            if (existing is null) throw;
            return existing;
        }
    }

    private static string VersionResourceName(string name, string version) =>
        $"{name}--{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..16]}";
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
}
