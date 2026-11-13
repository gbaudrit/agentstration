using System.Text.Json;
using Agentstration.Schemas.Contracts;
using System.Text.Json.Serialization;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

using Agentstration.Flows;

namespace Agentstration.DataSources.Contracts;

public static class DataSourceResourceKinds
{
    public const string DataSource = "DataSource";
    public const string DataSourceProfile = "DataSourceProfile";
    public const string DataSourceProfileRevision = "DataSourceProfileRevision";
    public const string DataSourceAcquisition = "DataSourceAcquisition";
    public const string DataSourceDiscovery = "DataSourceDiscovery";
    public const string DataSourceAcquisitionTarget = "DataSourceAcquisitionTarget";
}

public static class DataSourceFlowContracts
{
    public const string Acquisition = "datasource.acquisition/v1";
    public const string Discovery = "datasource.discovery/v1";
}

public sealed record DataSourceFlowTarget
{
    public required string Name { get; init; }
    public ResourceNamespace? Namespace { get; init; }
    public ResourceScopeRef? ScopeRef { get; init; }
    public string? Version { get; init; }
    public bool UseActiveVersion { get; init; } = true;
}

public sealed record DataSourceProfileToolBinding
{
    public required string Name { get; init; }
    public required string Capability { get; init; }
    public required ResourceReference Tool { get; init; }
    public ResourceReference? RequiredProvider { get; init; }
}

public sealed record DataSourceProfileProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public string Version { get; init; } = "1.0.0";
    public JsonElement ConfigurationSchema { get; init; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = true
    });
    public required DataSourceFlowTarget AcquisitionFlow { get; init; }
    public IReadOnlyList<DataSourceProfileToolBinding> ToolBindings { get; init; } = [];
    public DataSourceFlowTarget? DiscoveryFlow { get; init; }
    public IReadOnlyList<DataSourceProfileToolBinding> DiscoveryToolBindings { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Limits { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> Policies { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record DataSourceProfileResource : Resource
{
    public DataSourceProfileProperties Definition { get; init; } = null!;
    public string? ActiveVersion { get; init; }
}

public sealed record DataSourceProfileRevisionResource : Resource, IImmutableResource
{
    public required Guid ProfileUid { get; init; }
    public required string ProfileName { get; init; }
    public required long ProfileGeneration { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required string DependencyHash { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public required Guid PublishedBy { get; init; }
    public required DataSourceProfileProperties Definition { get; init; }
    public required ResolvedDataSourceFlowBinding AcquisitionFlow { get; init; }
    public IReadOnlyList<ResolvedDataSourceToolBinding> ToolBindings { get; init; } = [];
    public ResolvedDataSourceFlowBinding? DiscoveryFlow { get; init; }
    public IReadOnlyList<ResolvedDataSourceToolBinding> DiscoveryToolBindings { get; init; } = [];
}

public sealed record DataSourceProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public ResourceReference? Profile { get; init; }
    public JsonElement Configuration { get; init; } = JsonSerializer.SerializeToElement(new { });
    public DataSourceDatasetProperties? Dataset { get; init; }
}

public sealed record DataSourceOperationAcquisitionBinding(
    ConnectedServiceOperationRevisionReference Operation, JsonElement Parameters)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaBinding? OutputSchemaBinding { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaBindingSnapshot? OutputSchemaSnapshot { get; init; }
}

/// <summary>A logical dataset rule. Its effective UID partition is assigned by the server, never by a caller.</summary>
public sealed record DataSourceDatasetProperties(
    StorageRevisionReference? Storage, StorageContentPolicy Rule,
    DataSourceOperationAcquisitionBinding Acquisition)
{
    /// <summary>Explicit structured acquisition target; null retains the content profile.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DocumentCollectionUid { get; init; }
    public DataSourceAcquisitionPolicy? Lifecycle { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaBinding? OutputSchemaBinding { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaBindingSnapshot? OutputSchemaSnapshot { get; init; }
    public FlowCallReference? Orchestration { get; init; }
    public IReadOnlyList<FlowCompositionCall> Transformations { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<FlowCompositionCall>> Compositions { get; init; }
        = new Dictionary<string, IReadOnlyList<FlowCompositionCall>>();
}

public sealed record ResolvedDataSourceDataset(
    DataSourceDatasetProperties Definition, ConnectedServiceOperationResource Operation,
    ConnectedServiceOperationTypeVersionDescriptor OperationType,
    JsonElement InputSchema, JsonElement OutputSchema, string Partition,
    ResolvedDataSourceFlowBinding Flow)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? OperationOutputSchema { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaBindingSnapshot? OutputSchemaSnapshot { get; init; }
    public ResolvedDataSourceFlowBinding? OperationFlow { get; init; }
    public ResolvedFlowComposition? Composition { get; init; }
}

public sealed record DataSourceResource : Resource
{
    public DataSourceProperties Definition { get; init; } = null!;
    public Guid? ProfileUid { get; init; }
}

public sealed record ResolvedDataSourceProfile
{
    public required string Name { get; init; }
    public required ResourceNamespace Namespace { get; init; }
    public required ResourceScopeRef ScopeRef { get; init; }
    public required Guid Uid { get; init; }
    public required long Generation { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required string DependencyHash { get; init; }
    public required DataSourceProfileProperties Definition { get; init; }
    public required ResolvedDataSourceFlowBinding AcquisitionFlow { get; init; }
    public IReadOnlyList<ResolvedDataSourceToolBinding> ToolBindings { get; init; } = [];
    public ResolvedDataSourceFlowBinding? DiscoveryFlow { get; init; }
    public IReadOnlyList<ResolvedDataSourceToolBinding> DiscoveryToolBindings { get; init; } = [];
}

public sealed record ResolvedDataSourceFlowBinding(
    string Name,
    ResourceNamespace Namespace,
    string Version,
    bool UsesActiveVersion,
    JsonElement? InputSchema,
    JsonElement? OutputSchema,
    string? Contract,
    string DefinitionHash)
{
    public ResourceScopeRef? ScopeRef { get; init; }
}

public sealed record ResolvedDataSourceToolBinding
{
    public required string BindingName { get; init; }
    public required string Capability { get; init; }
    public required ResourceScopeRef ToolScopeRef { get; init; }
    public required string ToolName { get; init; }
    public required ResourceNamespace ToolNamespace { get; init; }
    public required Guid ToolUid { get; init; }
    public required long ToolGeneration { get; init; }
    public required ResourceScopeRef ProviderScopeRef { get; init; }
    public required string ProviderName { get; init; }
    public required ResourceNamespace ProviderNamespace { get; init; }
    public required Guid ProviderUid { get; init; }
    public required long ProviderGeneration { get; init; }
    public required string ExternalToolId { get; init; }
}

public sealed record ResolvedDataSourceProfileDependencies(
    ResolvedDataSourceFlowBinding AcquisitionFlow,
    IReadOnlyList<ResolvedDataSourceToolBinding> ToolBindings,
    ResolvedDataSourceFlowBinding? DiscoveryFlow,
    IReadOnlyList<ResolvedDataSourceToolBinding> DiscoveryToolBindings);

public sealed record ResolvedDataSourceAcquisitionComposition
{
    public required ResolvedDataSourceProfile Profile { get; init; }
    public required ResolvedDataSourceFlowBinding Flow { get; init; }
    public IReadOnlyList<ResolvedDataSourceToolBinding> Tools { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Limits { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> Policies { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record ResolvedDataSourceDiscoveryComposition
{
    public required ResolvedDataSourceProfile Profile { get; init; }
    public required ResolvedDataSourceFlowBinding Flow { get; init; }
    public IReadOnlyList<ResolvedDataSourceToolBinding> Tools { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Limits { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> Policies { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record DataSourceReadiness(
    bool Ready,
    bool Enabled,
    ResolvedDataSourceProfile? Profile,
    IReadOnlyList<string> Issues,
    ResolvedDataSourceFlowBinding? Acquisition = null,
    ResolvedDataSourceFlowBinding? Discovery = null)
{
    public ResolvedDataSourceDataset? Dataset { get; init; }
    public bool CanAcquire { get; init; }
}

public sealed record CreateDataSourceProfileRequest(
    string Name,
    DataSourceProfileProperties Properties,
    string? Namespace = null,
    ResourceScopeRef? ScopeRef = null);

public sealed record PutDataSourceProfileRequest(DataSourceProfileProperties Properties);
public sealed record PublishDataSourceProfileRequest(string Version, bool Activate = true);
public sealed record ActivateDataSourceProfileRequest(string Version);

public sealed record CreateDataSourceRequest(
    string Name,
    DataSourceProperties Properties,
    string? Namespace = null,
    ResourceScopeRef? ScopeRef = null);

public sealed record PutDataSourceRequest(DataSourceProperties Properties);
public sealed record SetDataSourceEnabledRequest(bool Enabled);

public enum DataSourceAcquisitionState
{
    Pending,
    Running,
    WaitingForInput,
    WaitingForChild,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public enum DataSourceDiscoveryState
{
    Pending,
    Running,
    WaitingForInput,
    WaitingForChild,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public enum DataSourceArtifactDisposition { Intermediate, Diagnostic, Publishable }
public enum DataSourceArtifactKind { Staged, Durable }

public sealed record DataSourceAcquisitionArtifact
{
    public required string ArtifactId { get; init; }
    public required DataSourceArtifactKind Kind { get; init; }
    public required DataSourceArtifactDisposition Disposition { get; init; }
    public string? Name { get; init; }
    public string? MediaType { get; init; }
    public string? Digest { get; init; }
}

public sealed record DataSourceAcquisitionManifest
{
    public IReadOnlyList<DataSourceAcquisitionArtifact> Artifacts { get; init; } = [];
}

public sealed record DataSourceAcquisitionCaller(
    [property: JsonPropertyName("principalId")] Guid PrincipalId,
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId);

public sealed record DataSourceDiscoveryChoice
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public JsonElement Selector { get; init; } = JsonSerializer.SerializeToElement(new { });
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public bool Eligible { get; init; } = true;
    public string? IneligibleReason { get; init; }
}

public sealed record DataSourceDiscoveryManifest
{
    public IReadOnlyList<DataSourceAcquisitionArtifact> Artifacts { get; init; } = [];
    public IReadOnlyList<DataSourceDiscoveryChoice> Choices { get; init; } = [];
}

public sealed record DataSourceDiscoveryInput
{
    [JsonPropertyName("dataSourceId")] public required string DataSourceId { get; init; }
    [JsonPropertyName("dataSourceUid")] public required Guid DataSourceUid { get; init; }
    [JsonPropertyName("dataSourceGeneration")] public required long DataSourceGeneration { get; init; }
    [JsonPropertyName("profile")] public required ResolvedDataSourceProfile Profile { get; init; }
    [JsonPropertyName("sourceConfiguration")] public JsonElement SourceConfiguration { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("parameters")] public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("caller")] public required DataSourceAcquisitionCaller Caller { get; init; }
    [JsonPropertyName("correlationId")] public required string CorrelationId { get; init; }
    [JsonPropertyName("discoveryId")] public required string DiscoveryId { get; init; }
}

public sealed record DataSourceDiscoveryResource : Resource
{
    public required ResourceScopeRef DataSourceScopeRef { get; init; }
    public required Guid DataSourceUid { get; init; }
    public required string DataSourceName { get; init; }
    public required ResourceNamespace DataSourceNamespace { get; init; }
    public required long DataSourceGeneration { get; init; }
    public required ResolvedDataSourceDiscoveryComposition Composition { get; init; }
    public required string FlowRunId { get; init; }
    public required DataSourceDiscoveryState State { get; init; }
    public required string CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string RequestHash { get; init; }
    public JsonElement SourceConfiguration { get; init; } = JsonSerializer.SerializeToElement(new { });
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public required Guid CreatedBy { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public ResourceScopeRef? LogicalScopeRef { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DataSourceDiscoveryManifest? Manifest { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record DataSourceAcquisitionTargetResource : Resource
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public required ResourceScopeRef DataSourceScopeRef { get; init; }
    public required Guid DataSourceUid { get; init; }
    public required string DataSourceName { get; init; }
    public required ResourceNamespace DataSourceNamespace { get; init; }
    public required long DataSourceGeneration { get; init; }
    public required string DiscoveryId { get; init; }
    public required Guid DiscoveryUid { get; init; }
    public required string ChoiceId { get; init; }
    public required string ChoiceDisplayName { get; init; }
    public JsonElement Selector { get; init; } = JsonSerializer.SerializeToElement(new { });
    public IReadOnlyDictionary<string, string> ChoiceMetadata { get; init; } = new Dictionary<string, string>();
    public required ResolvedDataSourceProfile Profile { get; init; }
    public required DateTimeOffset PromotedAt { get; init; }
    public required Guid PromotedBy { get; init; }
}

public sealed record DataSourceAcquisitionTargetView(DataSourceAcquisitionTargetResource Target, bool Stale, IReadOnlyList<string> StalenessReasons);
public sealed record StartDataSourceDiscoveryRequest
{
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string? CorrelationId { get; init; }
    public ResourceScopeRef? DataSourceScopeRef { get; init; }
}
public sealed record PromoteDataSourceAcquisitionTargetRequest(string Name, string ChoiceId, string? DisplayName = null, string? Description = null);
public sealed record PutDataSourceAcquisitionTargetRequest(string DisplayName, string? Description, bool Enabled);
public sealed record ReconcileDataSourceAcquisitionTargetRequest(string DiscoveryId, string ChoiceId);

public sealed record DataSourceAcquisitionInput
{
    [JsonPropertyName("dataSourceId")]
    public required string DataSourceId { get; init; }
    [JsonPropertyName("dataSourceUid")]
    public required Guid DataSourceUid { get; init; }
    [JsonPropertyName("dataSourceGeneration")]
    public required long DataSourceGeneration { get; init; }
    [JsonPropertyName("profile")]
    public required ResolvedDataSourceProfile Profile { get; init; }
    [JsonPropertyName("sourceConfiguration")]
    public JsonElement SourceConfiguration { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("caller")]
    public required DataSourceAcquisitionCaller Caller { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    [JsonPropertyName("acquisitionId")]
    public required string AcquisitionId { get; init; }
    [JsonPropertyName("target"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DataSourceAcquisitionTargetResource? Target { get; init; }
}

public sealed record DataSourceAcquisitionResource : Resource
{
    public required ResourceScopeRef DataSourceScopeRef { get; init; }
    public required Guid DataSourceUid { get; init; }
    public required string DataSourceName { get; init; }
    public required ResourceNamespace DataSourceNamespace { get; init; }
    public required long DataSourceGeneration { get; init; }
    public DataSourceAcquisitionTargetResource? Target { get; init; }
    public ResolvedDataSourceAcquisitionComposition? Composition { get; init; }
    public ResolvedDataSourceDataset? Dataset { get; init; }
    public StorageObject? Content { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StoragePublicationReference? Publication { get; init; }
    public DirectDataSourceResult? DirectResult { get; init; }
    public DateTimeOffset? ContentRetiredAt { get; init; }
    public DateTimeOffset? ContentRetiringAt { get; init; }
    public bool ContentRetirementPending { get; init; }
    public IReadOnlyDictionary<string, DateTimeOffset> ProjectionLeases { get; init; } = new Dictionary<string, DateTimeOffset>();
    public DateTimeOffset? WriteNotAfter { get; init; }
    public required string FlowRunId { get; init; }
    public required DataSourceAcquisitionState State { get; init; }
    public required string CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string RequestHash { get; init; }
    public JsonElement SourceConfiguration { get; init; } = JsonSerializer.SerializeToElement(new { });
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public required Guid CreatedBy { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public ResourceScopeRef? LogicalScopeRef { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int Attempt { get; init; } = 1;
    public string? RetriedFrom { get; init; }
    public DataSourceAcquisitionManifest? Manifest { get; init; }
    public Agentstration.Schemas.DataContractFailure? ContractFailure { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record StartDataSourceAcquisitionRequest
{
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string? CorrelationId { get; init; }
    public ResourceScopeRef? DataSourceScopeRef { get; init; }
    public string? TargetName { get; init; }
}

public sealed record RetryDataSourceAcquisitionRequest(string? CorrelationId = null);
