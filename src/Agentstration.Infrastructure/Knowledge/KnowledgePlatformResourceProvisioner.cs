using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Knowledge;

public sealed class KnowledgePlatformResourceProvisioner(
    IResourceStore store,
    FlowService flows,
    DataSourceProfileService dataSourceProfiles,
    TimeProvider timeProvider)
{
    public const string RetrievalToolSetName = "knowledge-retrieval-builtin";
    public const string ProjectionFlowName = "knowledge-projection-builtin";
    public const string RetrievalFlowName = "knowledge-retrieval-builtin";
    public const string WebAcquisitionFlowName = "data-source-web-acquisition-builtin";
    public const string RestAcquisitionFlowName = "data-source-rest-acquisition-builtin";
    public const string ArtifactImportAcquisitionFlowName = "data-source-artifact-import-acquisition-builtin";
    public const string ToolSetVersion = "1.0.0";
    public const string RetrievalFlowVersion = "1.0.0";
    public const string ProjectionFlowVersion = "1.0.0";
    public const string ProfileFlowVersion = "1.0.0";
    public const string ProfileVersion = "1.0.0";

    public async Task EnsureAsync(ResourceScopeRef workspaceScope, CancellationToken cancellationToken)
    {
        if (workspaceScope is not { Kind: ResourceScopeKind.Workspace, TargetId: { } workspaceId })
            throw new InvalidOperationException("Built-in Knowledge resources require a Workspace scope.");
        await EnsureToolSetAsync(workspaceScope, RetrievalToolSetName,
            "Knowledge retrieval · Built-in", "Local-first deterministic search, query, and read over Snapshot Artifacts.",
            [
                (KnowledgeFlowContracts.Search, KnowledgeBuiltinToolNames.RetrievalSearch),
                (KnowledgeFlowContracts.Query, KnowledgeBuiltinToolNames.RetrievalQuery),
                (KnowledgeFlowContracts.Read, KnowledgeBuiltinToolNames.RetrievalRead)
            ], cancellationToken);
        await EnsureProjectionFlowAsync(new WorkspaceId(workspaceId), cancellationToken);
        await EnsureRetrievalFlowAsync(new WorkspaceId(workspaceId), cancellationToken);
        await EnsureHttpAcquisitionFlowAsync(new WorkspaceId(workspaceId), WebAcquisitionFlowName,
            "Web resource acquisition · Built-in", WebFetchMcpTool.ToolName, cancellationToken);
        await EnsureHttpAcquisitionFlowAsync(new WorkspaceId(workspaceId), RestAcquisitionFlowName,
            "REST resource acquisition · Built-in", RestGetMcpTool.ToolName, cancellationToken);
        await EnsureArtifactImportFlowAsync(new WorkspaceId(workspaceId), cancellationToken);
        await EnsureDataSourceProfilesAsync(workspaceScope, cancellationToken);
    }

    private async Task EnsureDataSourceProfilesAsync(ResourceScopeRef scope, CancellationToken cancellationToken)
    {
        await EnsureDataSourceProfileAsync(Profile(scope, "web-builtin", "Web · Built-in",
            "Fetches one bounded public HTTP(S) Web resource.", WebAcquisitionFlowName,
            WebFetchMcpTool.ToolName, "web.fetch", UrlSchema()), cancellationToken);
        await EnsureDataSourceProfileAsync(Profile(scope, "rest-builtin", "REST · Built-in",
            "Fetches one bounded public unauthenticated HTTP(S) GET response.", RestAcquisitionFlowName,
            RestGetMcpTool.ToolName, "rest.get", UrlSchema()), cancellationToken);
        await EnsureDataSourceProfileAsync(Profile(scope, "artifact-import-builtin",
            "Artifact import · Built-in", "Imports selected governed durable Artifacts.", ArtifactImportAcquisitionFlowName,
            DataSourceBuiltinToolNames.ArtifactImport, "artifact.import", ArtifactImportSchema()), cancellationToken);
    }

    private static DataSourceProfileResource Profile(
        ResourceScopeRef scope,
        string name,
        string displayName,
        string description,
        string acquisitionFlow,
        string externalTool,
        string capability,
        JsonElement configurationSchema) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSourceProfile,
        Metadata = new ResourceMetadata
        {
            Name = name,
            Annotations = new Dictionary<string, string>
            {
                [ResourceProvenanceAnnotations.BuiltIn] = "true",
                [ResourceProvenanceAnnotations.Origin] = "agentstration.core",
                [ResourceProvenanceAnnotations.Owner] = "agentstration"
            }
        },
        ScopeRef = scope,
        Generation = 1,
        Status = Succeeded(),
        Definition = new DataSourceProfileProperties
        {
            DisplayName = displayName,
            Description = description,
            Version = ProfileVersion,
            Enabled = true,
            ConfigurationSchema = configurationSchema,
            AcquisitionFlow = new() { Name = acquisitionFlow, Version = ProfileFlowVersion, UseActiveVersion = false },
            ToolBindings =
            [
                new()
                {
                    Name = "acquire",
                    Capability = capability,
                    Tool = new ResourceReference(AgentstrationToolProvider.ToolResourceName(externalTool), scope, ResourceNamespace.Default)
                }
            ],
            Limits = new Dictionary<string, JsonElement>
            {
                ["maximumBytes"] = JsonSerializer.SerializeToElement(1024 * 1024),
                ["maximumRedirects"] = JsonSerializer.SerializeToElement(3),
                ["timeoutSeconds"] = JsonSerializer.SerializeToElement(20)
            },
            Policies = new Dictionary<string, JsonElement>
            {
                ["publicNetworkOnly"] = JsonSerializer.SerializeToElement(true),
                ["credentialsAllowed"] = JsonSerializer.SerializeToElement(false)
            }
        }
    };

    private async Task EnsureDataSourceProfileAsync(
        DataSourceProfileResource desired,
        CancellationToken cancellationToken)
    {
        var existing = await dataSourceProfiles.GetAsync(desired.Namespace, desired.Name, desired.ScopeRef, cancellationToken);
        if (existing is null)
            existing = await dataSourceProfiles.CreateAsync(desired, cancellationToken);
        else if (!existing.Value.Metadata.Annotations.TryGetValue(ResourceProvenanceAnnotations.BuiltIn, out var builtIn)
            || !string.Equals(builtIn, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Reserved built-in Data Source Profile identity '{desired.Name}' is already in use.");

        var revision = await dataSourceProfiles.GetRevisionAsync(desired.Namespace, desired.Name,
            ProfileVersion, desired.ScopeRef, cancellationToken);
        if (revision is null)
            _ = await dataSourceProfiles.PublishAsync(desired.Namespace, desired.Name, desired.ScopeRef,
                new(ProfileVersion), cancellationToken);
        else if (!string.Equals(existing.Value.ActiveVersion, ProfileVersion, StringComparison.Ordinal))
            _ = await dataSourceProfiles.ActivateAsync(desired.Namespace, desired.Name, desired.ScopeRef,
                new(ProfileVersion), existing.ETag, cancellationToken);
    }

    private static JsonElement UrlSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { url = new { type = "string", format = "uri", minLength = 8, maxLength = 2048 } },
        required = new[] { "url" },
        additionalProperties = false
    });

    private static JsonElement ArtifactImportSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            artifactIds = new
            {
                type = "array",
                maxItems = 100,
                items = new { type = "string" }
            }
        },
        required = new[] { "artifactIds" },
        additionalProperties = false
    });

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
                    Version = ToolSetVersion,
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

        var versionName = VersionResourceName(name, ToolSetVersion);
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
            Version = ToolSetVersion,
            DefinitionHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(members))),
            PublishedAt = timeProvider.GetUtcNow(),
            Members = members
        }, scope, cancellationToken);
    }

    private async Task EnsureHttpAcquisitionFlowAsync(
        WorkspaceId workspaceId,
        string name,
        string displayName,
        string externalToolId,
        CancellationToken cancellationToken)
    {
        var input = KnowledgeBuiltinSchemas.DataSourceAcquisitionFlowInput;
        var output = KnowledgeBuiltinSchemas.ArtifactManifestOutput;
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = input,
            OutputSchema = output,
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", DisplayName = "Acquisition request", Schema = input },
                new ToolFlowStepDefinition
                {
                    Name = "fetch",
                    DisplayName = "Acquire source content",
                    Tool = new(AgentstrationToolProvider.ToolResourceName(externalToolId)),
                    ArgumentsMapping = JsonSerializer.SerializeToElement(new
                    {
                        sourceConfiguration = "${input.sourceConfiguration}"
                    })
                },
                new FlowCallStepDefinition
                {
                    Name = "persist",
                    DisplayName = "Persist acquired content",
                    Flow = new(ArtifactPlatformResourceProvisioner.StorageWriteFlowName,
                        FlowCallVersionStrategy.Exact, ArtifactPlatformResourceProvisioner.DefaultToolSetVersion),
                    InputMapping = JsonSerializer.SerializeToElement(new
                    {
                        stagedArtifactId = "${steps.fetch.output.stagedArtifactId}",
                        producerFlowRunId = "${steps.fetch.output.producerFlowRunId}",
                        producerFlowStepId = "${steps.fetch.output.producerFlowStepId}"
                    })
                },
                new OutputFlowStepDefinition
                {
                    Name = "output",
                    DisplayName = "Acquisition manifest",
                    OutputMapping = JsonSerializer.SerializeToElement(new
                    {
                        artifacts = new[]
                        {
                            new
                            {
                                artifactId = "${steps.persist.output.flowRunArtifactId}",
                                kind = "durable",
                                disposition = "publishable",
                                name = "${steps.fetch.output.fileName}",
                                mediaType = "${steps.fetch.output.mediaType}"
                            }
                        }
                    })
                },
                new FailureFlowStepDefinition { Name = "failure", Code = "acquisition_failed", Message = "Data Source acquisition failed.", DetailsExpression = "${transition.output}" }
            ],
            Transitions =
            [
                new("input-fetch", "input", "completed", "fetch"),
                new("fetch-persist", "fetch", "success", "persist"),
                new("fetch-failure", "fetch", "error", "failure"),
                new("persist-output", "persist", "completed", "output"),
                new("persist-failure", "persist", "error", "failure")
            ]
        };
        await CreateAndPublishFlowAsync(workspaceId, name, displayName,
            DataSourceFlowContracts.Acquisition, null, ProfileFlowVersion, graph, cancellationToken);
    }

    private async Task EnsureArtifactImportFlowAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        var input = KnowledgeBuiltinSchemas.DataSourceAcquisitionFlowInput;
        var output = KnowledgeBuiltinSchemas.ArtifactManifestOutput;
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = input,
            OutputSchema = output,
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", DisplayName = "Artifact import request", Schema = input },
                new ToolFlowStepDefinition
                {
                    Name = "import",
                    DisplayName = "Import durable Artifacts",
                    Tool = new(AgentstrationToolProvider.ToolResourceName(DataSourceBuiltinToolNames.ArtifactImport)),
                    ArgumentsMapping = JsonSerializer.SerializeToElement(new
                    {
                        sourceConfiguration = "${input.sourceConfiguration}"
                    })
                },
                new OutputFlowStepDefinition
                {
                    Name = "output",
                    DisplayName = "Acquisition manifest",
                    OutputMapping = JsonSerializer.SerializeToElement("${steps.import.output}")
                },
                new FailureFlowStepDefinition { Name = "failure", Code = "artifact_import_failed", Message = "Artifact import failed.", DetailsExpression = "${transition.output}" }
            ],
            Transitions =
            [
                new("input-import", "input", "completed", "import"),
                new("import-output", "import", "success", "output"),
                new("import-failure", "import", "error", "failure")
            ]
        };
        await CreateAndPublishFlowAsync(workspaceId, ArtifactImportAcquisitionFlowName,
            "Artifact import acquisition · Built-in", DataSourceFlowContracts.Acquisition, null,
            ProfileFlowVersion, graph, cancellationToken);
    }

    private async Task EnsureRetrievalFlowAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        var input = KnowledgeBuiltinSchemas.RetrievalInput;
        var output = KnowledgeBuiltinSchemas.RetrievalOutput;
        ToolRouteFlowStepDefinition Route(string name, string displayName, string capability) => new()
        {
            Name = name, DisplayName = displayName, ToolSet = new(RetrievalToolSetName, ToolSetVersion),
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
                new OutputFlowStepDefinition { Name = "read-output", OutputMapping = JsonSerializer.SerializeToElement("${steps.read.output}") },
                new FailureFlowStepDefinition { Name = "failure", Code = "knowledge_retrieval_failed", Message = "Knowledge retrieval failed.", DetailsExpression = "${transition.output}" }
            ],
            Transitions =
            [
                new("input-route", "input", "completed", "is-search"),
                new("route-search", "is-search", "true", "search"),
                new("route-non-search", "is-search", "false", "is-query"),
                new("route-query", "is-query", "true", "query"),
                new("route-read", "is-query", "false", "read"),
                new("search-output", "search", "success", "search-output"),
                new("search-failure", "search", "error", "failure"),
                new("query-output", "query", "success", "query-output"),
                new("query-failure", "query", "error", "failure"),
                new("read-output", "read", "success", "read-output"),
                new("read-failure", "read", "error", "failure")
            ]
        };
        await CreateAndPublishFlowAsync(workspaceId, RetrievalFlowName, "Knowledge retrieval · Built-in",
            KnowledgeFlowContracts.Retrieval,
            string.Join(',', KnowledgeFlowContracts.Search, KnowledgeFlowContracts.Query, KnowledgeFlowContracts.Read),
            RetrievalFlowVersion, graph, cancellationToken);
    }

    private async Task EnsureProjectionFlowAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        var input = KnowledgeBuiltinSchemas.ProjectionInput;
        var output = KnowledgeBuiltinSchemas.ArtifactManifestOutput;
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = input,
            OutputSchema = output,
            Steps =
            [
                new InputFlowStepDefinition
                {
                    Name = "input",
                    DisplayName = "Projection inputs",
                    Schema = input
                },
                new OutputFlowStepDefinition
                {
                    Name = "output",
                    DisplayName = "Projected artifacts",
                    OutputMapping = JsonSerializer.SerializeToElement(new { artifacts = "${input.artifacts}" })
                }
            ],
            Transitions = [new("input-output", "input", "completed", "output")]
        };
        await CreateAndPublishFlowAsync(workspaceId, ProjectionFlowName, "Knowledge projection · Built-in",
            KnowledgeFlowContracts.Projection, null, ProjectionFlowVersion, graph, cancellationToken);
    }

    private async Task CreateAndPublishFlowAsync(WorkspaceId workspaceId, string name, string displayName, string contract,
        string? capabilities, string version, FlowGraphDefinition graph, CancellationToken cancellationToken)
    {
        var nodes = graph.Steps.Select(step => new FlowNode(step.Name, step switch
        {
            InputFlowStepDefinition => FlowNodeKind.Input,
            ConditionFlowStepDefinition => FlowNodeKind.Condition,
            FlowCallStepDefinition => FlowNodeKind.Function,
            ToolFlowStepDefinition => FlowNodeKind.Function,
            ToolRouteFlowStepDefinition => FlowNodeKind.Function,
            OutputFlowStepDefinition => FlowNodeKind.Output,
            _ => FlowNodeKind.Custom
        })).ToArray();
        var metadata = new Dictionary<string, string>
        {
            ["systemManaged"] = "true",
            ["systemKind"] = "KnowledgeFlow",
            [ResourceProvenanceAnnotations.BuiltIn] = "true",
            [FlowMetadataKeys.Contract] = contract
        };
        if (capabilities is not null) metadata[KnowledgeFlowContracts.CapabilitiesMetadataKey] = capabilities;
        var id = new FlowId(name);
        var existing = await flows.GetAsync(workspaceId, id, cancellationToken);
        if (existing is not null
            && (!existing.Value.Metadata.TryGetValue(ResourceProvenanceAnnotations.BuiltIn, out var builtIn)
                || !string.Equals(builtIn, "true", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Reserved built-in Flow identity '{name}' is already in use.");
        if (existing is not null && await flows.GetVersionAsync(workspaceId, id, version, cancellationToken) is not null)
        {
            _ = await flows.ActivateVersionAsync(workspaceId, id, version, cancellationToken);
            return;
        }

        var description = $"Built-in local-first implementation of {contract}.";
        var definition = new WorkflowFlowDefinition(graph.EntryStep, nodes,
            graph.Transitions.Select(value => new FlowEdge(value.FromStep, value.ToStep)).ToArray(),
            graph.Steps.OfType<OutputFlowStepDefinition>().Select(value => value.Name).ToArray());
        try
        {
            if (existing is null)
                _ = await flows.CreateAsync(workspaceId, new CreateFlowCommand(name,
                    description, version, true, definition, metadata, graph, displayName), cancellationToken);
            else
            {
                _ = await flows.UpdateAsync(workspaceId, id, new UpdateFlowCommand(
                    description, version, true, definition, metadata, graph, displayName), existing.ETag, cancellationToken);
            }
            _ = await flows.PublishVersionAsync(workspaceId, id, version, true, cancellationToken,
                "Built-in local-first Knowledge contract implementation.");
        }
        catch (FlowConcurrencyException)
        {
            if (await flows.GetVersionAsync(workspaceId, id, version, cancellationToken) is null) throw;
            _ = await flows.ActivateVersionAsync(workspaceId, id, version, cancellationToken);
        }
        catch (FlowValidationException exception) when (exception.Code == "flow_version_already_published")
        {
            _ = await flows.ActivateVersionAsync(workspaceId, id, version, cancellationToken);
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
