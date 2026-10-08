using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Knowledge.Contracts;

public static class KnowledgeResourceKinds
{
    public const string KnowledgeSource = "KnowledgeSource";
    public const string KnowledgeSourceToolExposure = "KnowledgeSourceToolExposure";
    public const string KnowledgeProjection = "KnowledgeProjection";
    public const string KnowledgeSnapshot = "KnowledgeSnapshot";
    public const string KnowledgeSnapshotObservedState = "KnowledgeSnapshotObservedState";
}

public static class KnowledgeFlowContracts
{
    public const string CapabilitiesMetadataKey = "knowledge.capabilities";
    public const string Projection = "knowledge.projection/v1";
    public const string ArtifactTransformation = "artifact.transform/v1";
    public const string Retrieval = "knowledge.retrieval/v1";
    public const string Search = "knowledge.search/v1";
    public const string Query = "knowledge.query/v1";
    public const string Read = "knowledge.read/v1";

    public static string Capability(KnowledgeSourceOperation operation) => operation switch
    {
        KnowledgeSourceOperation.Search => Search,
        KnowledgeSourceOperation.Query => Query,
        KnowledgeSourceOperation.Read => Read,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}

public enum KnowledgeSourceOperation { Search, Query, Read }

public readonly record struct KnowledgeSourceId(string Value, ResourceNamespace Namespace = default)
{
    public override string ToString() => $"{Namespace}/{Value}";
}

public readonly record struct KnowledgeSnapshotId(string Value, ResourceNamespace Namespace = default)
{
    public override string ToString() => $"{Namespace}/{Value}";
}

public sealed record KnowledgeFlowTarget
{
    public required string Name { get; init; }
    public ResourceNamespace? Namespace { get; init; }
    public string? Version { get; init; }
    public bool UseActiveVersion { get; init; } = true;
}

public sealed record KnowledgeSourceProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public KnowledgeFlowTarget? RetrievalFlow { get; init; }
    public IReadOnlyList<KnowledgeDataSourceBinding> DataSources { get; init; } = [];
    public KnowledgeFlowTarget? ProjectionFlow { get; init; }
}

public sealed record KnowledgeDataSourceBinding
{
    public required string Name { get; init; }
    public required ResourceReference DataSource { get; init; }
    public KnowledgeFlowTarget? TransformationFlow { get; init; }
    public JsonElement Configuration { get; init; } = JsonSerializer.SerializeToElement(new { });
    public bool Required { get; init; } = true;
    public TimeSpan? MaximumAge { get; init; }
}

public sealed record KnowledgeSourceResource : Resource
{
    public KnowledgeSourceProperties Definition { get; init; } = null!;
}

public sealed record ResolvedKnowledgeFlowBinding(
    string Name,
    ResourceNamespace Namespace,
    string Version,
    bool UsesActiveVersion,
    JsonElement? InputSchema,
    JsonElement? OutputSchema,
    string? Contract = null,
    IReadOnlyList<string>? Capabilities = null);

public sealed record KnowledgeSourceReadiness(
    bool Ready,
    bool Enabled,
    ResolvedKnowledgeFlowBinding? Retrieval,
    IReadOnlyList<string> Issues,
    ResolvedKnowledgeFlowBinding? Projection = null,
    IReadOnlyList<KnowledgeDataSourceBindingReadiness>? DataSources = null);

public sealed record KnowledgeDataSourceBindingReadiness(
    string Name,
    ResourceScopeRef? DataSourceScopeRef,
    Guid? DataSourceUid,
    bool Ready,
    string? Issue,
    ResolvedKnowledgeFlowBinding? Transformation);

public sealed record KnowledgeSourceToolOperationExposure
{
    public required KnowledgeSourceOperation Operation { get; init; }
    public required ResourceReference Tool { get; init; }
    public required string Capability { get; init; }
    public required string Route { get; init; }
}

public sealed record KnowledgeSourceToolExposureResource : Resource
{
    public required Guid KnowledgeSourceUid { get; init; }
    public required string KnowledgeSourceName { get; init; }
    public required long KnowledgeSourceGeneration { get; init; }
    public required ResolvedKnowledgeFlowBinding RetrievalFlow { get; init; }
    public required ResourceReference ToolSet { get; init; }
    public required string ToolSetVersion { get; init; }
    public IReadOnlyList<KnowledgeSourceToolOperationExposure> Operations { get; init; } = [];
}

public sealed record CreateKnowledgeSourceRequest(
    string Name,
    KnowledgeSourceProperties Properties,
    string? Namespace = null);

public sealed record PutKnowledgeSourceRequest(KnowledgeSourceProperties Properties);
public sealed record SetKnowledgeSourceEnabledRequest(bool Enabled);
public sealed record PublishKnowledgeSourceToolExposureRequest
{
    public string Version { get; init; } = "1.0.0";
    public bool RequiresApproval { get; init; }
}

public enum KnowledgeProjectionState
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

public enum KnowledgeArtifactDisposition { Intermediate, Diagnostic, Publishable }
public enum KnowledgeArtifactKind { Staged, Durable }

public sealed record KnowledgeProjectionArtifact
{
    public required string ArtifactId { get; init; }
    public required KnowledgeArtifactKind Kind { get; init; }
    public required KnowledgeArtifactDisposition Disposition { get; init; }
    public string? Name { get; init; }
    public string? MediaType { get; init; }
    public string? Digest { get; init; }
}

public sealed record KnowledgeProjectionManifest
{
    public IReadOnlyList<KnowledgeProjectionArtifact> Artifacts { get; init; } = [];
}

public sealed record StartKnowledgeProjectionRequest
{
    public IReadOnlyDictionary<string, string> AcquisitionIds { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string? CorrelationId { get; init; }
}

public sealed record KnowledgeProjectionInputEvidence
{
    public required string BindingName { get; init; }
    public required ResourceScopeRef DataSourceScopeRef { get; init; }
    public required Guid DataSourceUid { get; init; }
    public required string DataSourceName { get; init; }
    public required ResourceNamespace DataSourceNamespace { get; init; }
    public required long DataSourceGeneration { get; init; }
    public required string AcquisitionId { get; init; }
    public required Guid AcquisitionUid { get; init; }
    public required string AcquisitionFlowRunId { get; init; }
    public required DateTimeOffset AcquiredAt { get; init; }
    public JsonElement AcquisitionComposition { get; init; } = JsonSerializer.SerializeToElement(new { });
    public IReadOnlyList<KnowledgeProjectionArtifact> AcquiredArtifacts { get; init; } = [];
    public ResolvedKnowledgeFlowBinding? TransformationFlow { get; init; }
    public string? TransformationFlowRunId { get; init; }
    public IReadOnlyList<KnowledgeProjectionArtifact> PreparedArtifacts { get; init; } = [];
}

public sealed record KnowledgeProjectionResource : Resource
{
    public required Guid KnowledgeSourceUid { get; init; }
    public required string KnowledgeSourceName { get; init; }
    public required ResourceNamespace KnowledgeSourceNamespace { get; init; }
    public required long KnowledgeSourceGeneration { get; init; }
    public required ResolvedKnowledgeFlowBinding ProjectionFlow { get; init; }
    public required ResolvedKnowledgeFlowBinding RetrievalFlow { get; init; }
    public IReadOnlyList<KnowledgeProjectionInputEvidence> Inputs { get; init; } = [];
    public IReadOnlyList<KnowledgeProjectionInputIssue> InputIssues { get; init; } = [];
    public required string ProjectionFlowRunId { get; init; }
    public required KnowledgeProjectionState State { get; init; }
    public required string CorrelationId { get; init; }
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public required Guid CreatedBy { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public KnowledgeProjectionManifest? Manifest { get; init; }
    public string? SnapshotName { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record KnowledgeProjectionInputIssue(string BindingName, string Code, string Message);

public sealed record KnowledgeProjectionCaller(
    [property: JsonPropertyName("principalId")] Guid PrincipalId,
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId);

public sealed record KnowledgeProjectionFlowInput
{
    [JsonPropertyName("knowledgeSourceId")]
    public required string KnowledgeSourceId { get; init; }
    [JsonPropertyName("knowledgeSourceUid")]
    public required Guid KnowledgeSourceUid { get; init; }
    [JsonPropertyName("knowledgeSourceGeneration")]
    public required long KnowledgeSourceGeneration { get; init; }
    [JsonPropertyName("inputs")]
    public required IReadOnlyList<KnowledgeProjectionInputEvidence> Inputs { get; init; }
    [JsonPropertyName("artifacts")]
    public required IReadOnlyList<KnowledgeProjectionArtifact> Artifacts { get; init; }
    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("caller")]
    public required KnowledgeProjectionCaller Caller { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    [JsonPropertyName("projectionId")]
    public required string ProjectionId { get; init; }
}

public sealed record KnowledgeArtifactTransformationInput
{
    [JsonPropertyName("bindingName")]
    public required string BindingName { get; init; }
    [JsonPropertyName("dataSource")]
    public required KnowledgeProjectionInputEvidence DataSource { get; init; }
    [JsonPropertyName("artifacts")]
    public required IReadOnlyList<KnowledgeProjectionArtifact> Artifacts { get; init; }
    [JsonPropertyName("configuration")]
    public JsonElement Configuration { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("caller")]
    public required KnowledgeProjectionCaller Caller { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    [JsonPropertyName("transformationId")]
    public required string TransformationId { get; init; }
}

public enum KnowledgeSnapshotLifecycleState { Active, Superseded, Unavailable }

public sealed record KnowledgeSnapshotArtifact
{
    public required string ArtifactId { get; init; }
    public required string ProducerFlowRunId { get; init; }
    public required string ProducerFlowStepId { get; init; }
    public required string StorageFlowRunId { get; init; }
    public required string MediaType { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();
}

public sealed record KnowledgeSnapshotResource : Resource, IImmutableResource
{
    public required Guid KnowledgeSourceUid { get; init; }
    public required string KnowledgeSourceName { get; init; }
    public required ResourceNamespace KnowledgeSourceNamespace { get; init; }
    public required long KnowledgeSourceGeneration { get; init; }
    public required string RequestHash { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public required Guid PublishedBy { get; init; }
    public required string ProjectionId { get; init; }
    public required Guid ProjectionUid { get; init; }
    public required ResolvedKnowledgeFlowBinding ProjectionFlow { get; init; }
    public required string ProjectionFlowRunId { get; init; }
    public IReadOnlyList<KnowledgeProjectionInputEvidence> ProjectionInputs { get; init; } = [];
    public required ResolvedKnowledgeFlowBinding RetrievalFlow { get; init; }
    public IReadOnlyList<KnowledgeSnapshotArtifact> Artifacts { get; init; } = [];
}

public sealed record KnowledgeSnapshotObservedResource : Resource
{
    public required Guid KnowledgeSourceUid { get; init; }
    public string? ActiveSnapshotName { get; init; }
    public Guid? ActiveSnapshotUid { get; init; }
    public string? LastProjectionId { get; init; }
    public DateTimeOffset? LastPublishedAt { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
}

public sealed record KnowledgeSnapshotView(
    KnowledgeSnapshotResource Snapshot,
    KnowledgeSnapshotLifecycleState LifecycleState);

public sealed record SelectActiveKnowledgeSnapshotRequest(string SnapshotName);

public sealed record SearchKnowledgeRequest
{
    public required string Query { get; init; }
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>();
    public int Limit { get; init; } = 10;
    public string? ContinuationToken { get; init; }
    public string? SnapshotName { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record QueryKnowledgeRequest
{
    public required string Question { get; init; }
    public int MaximumItems { get; init; } = 10;
    public int MaximumOutputCharacters { get; init; } = 16_384;
    public string? SnapshotName { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record ReadKnowledgeRequest
{
    public required string ArtifactId { get; init; }
    public long Offset { get; init; }
    public int Length { get; init; } = 65_536;
    public string? SnapshotName { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record KnowledgeRetrievalSnapshotInput
{
    public required string Name { get; init; }
    public required Guid Uid { get; init; }
    public IReadOnlyList<KnowledgeSnapshotArtifact> Artifacts { get; init; } = [];
}

public sealed record KnowledgeRetrievalCaller
{
    [JsonPropertyName("principalId")]
    public required Guid PrincipalId { get; init; }
    [JsonPropertyName("tenantId")]
    public required Guid TenantId { get; init; }
    [JsonPropertyName("workspaceId")]
    public required Guid WorkspaceId { get; init; }
    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }
    [JsonPropertyName("agentRevisionId")]
    public string? AgentRevisionId { get; init; }
    [JsonPropertyName("runtimeRunId")]
    public string? RuntimeRunId { get; init; }
    [JsonPropertyName("flowRunId")]
    public string? FlowRunId { get; init; }
    [JsonPropertyName("flowStepId")]
    public string? FlowStepId { get; init; }
    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }
    [JsonPropertyName("toolInvocationId")]
    public string? ToolInvocationId { get; init; }
}

public sealed record KnowledgeRetrievalInput
{
    [JsonPropertyName("knowledgeSourceId")]
    public required string KnowledgeSourceId { get; init; }
    [JsonPropertyName("knowledgeSourceUid")]
    public required Guid KnowledgeSourceUid { get; init; }
    [JsonPropertyName("knowledgeSourceGeneration")]
    public required long KnowledgeSourceGeneration { get; init; }
    [JsonPropertyName("operation")]
    public required string Operation { get; init; }
    [JsonPropertyName("snapshot")]
    public required KnowledgeRetrievalSnapshotInput Snapshot { get; init; }
    [JsonPropertyName("request")]
    public required JsonElement Request { get; init; }
    [JsonPropertyName("caller")]
    public required KnowledgeRetrievalCaller Caller { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    [JsonPropertyName("retrievalId")]
    public required string RetrievalId { get; init; }
}

public sealed record KnowledgeRetrievalItem
{
    public required string Id { get; init; }
    public required string ArtifactId { get; init; }
    public string? Content { get; init; }
    public string? MediaType { get; init; }
    public double? Score { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record KnowledgeRetrievalCitation
{
    public required string ArtifactId { get; init; }
    public string? Locator { get; init; }
    public long? Start { get; init; }
    public long? End { get; init; }
    public string? Excerpt { get; init; }
}

public sealed record KnowledgeRetrievalFlowOutput
{
    public IReadOnlyList<KnowledgeRetrievalItem> Items { get; init; } = [];
    public IReadOnlyList<KnowledgeRetrievalCitation> Citations { get; init; } = [];
    public string? Answer { get; init; }
    public string? ContinuationToken { get; init; }
}

public sealed record KnowledgeRetrievalResult
{
    public required KnowledgeSourceOperation Operation { get; init; }
    public required string KnowledgeSourceId { get; init; }
    public required Guid KnowledgeSourceUid { get; init; }
    public required long KnowledgeSourceGeneration { get; init; }
    public required string SnapshotName { get; init; }
    public required Guid SnapshotUid { get; init; }
    public required ResolvedKnowledgeFlowBinding RetrievalFlow { get; init; }
    public required string FlowRunId { get; init; }
    public required string CorrelationId { get; init; }
    public IReadOnlyList<KnowledgeRetrievalItem> Items { get; init; } = [];
    public IReadOnlyList<KnowledgeRetrievalCitation> Citations { get; init; } = [];
    public string? Answer { get; init; }
    public string? ContinuationToken { get; init; }
}
