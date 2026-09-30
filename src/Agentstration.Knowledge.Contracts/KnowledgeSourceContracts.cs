using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Knowledge.Contracts;

public static class KnowledgeResourceKinds
{
    public const string KnowledgeSource = "KnowledgeSource";
    public const string KnowledgeSourceToolExposure = "KnowledgeSourceToolExposure";
    public const string KnowledgeAcquisition = "KnowledgeAcquisition";
}

public static class KnowledgeFlowContracts
{
    public const string MetadataKey = "knowledge.contract";
    public const string Ingestion = "knowledge.ingestion/v1";
}

public enum KnowledgeSourceOperation { Search, Query, Read }

public readonly record struct KnowledgeSourceId(string Value, ResourceNamespace Namespace = default)
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
    public KnowledgeFlowTarget? IngestionFlow { get; init; }
    public KnowledgeFlowTarget? RetrievalFlow { get; init; }
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
    string? Contract = null);

public sealed record KnowledgeSourceReadiness(
    bool Ready,
    bool Enabled,
    ResolvedKnowledgeFlowBinding? Ingestion,
    ResolvedKnowledgeFlowBinding? Retrieval,
    IReadOnlyList<string> Issues);

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

public enum KnowledgeAcquisitionState
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

public sealed record KnowledgeAcquisitionArtifact
{
    public required string ArtifactId { get; init; }
    public required KnowledgeArtifactKind Kind { get; init; }
    public required KnowledgeArtifactDisposition Disposition { get; init; }
    public string? Name { get; init; }
    public string? MediaType { get; init; }
    public string? Digest { get; init; }
}

public sealed record KnowledgeAcquisitionManifest
{
    public IReadOnlyList<KnowledgeAcquisitionArtifact> Artifacts { get; init; } = [];
}

public sealed record KnowledgeAcquisitionCaller(
    [property: JsonPropertyName("principalId")] Guid PrincipalId,
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId);

public sealed record KnowledgeIngestionInput
{
    [JsonPropertyName("knowledgeSourceId")]
    public required string KnowledgeSourceId { get; init; }
    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("caller")]
    public required KnowledgeAcquisitionCaller Caller { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    [JsonPropertyName("acquisitionId")]
    public required string AcquisitionId { get; init; }
}

public sealed record KnowledgeAcquisitionResource : Resource
{
    public required Guid KnowledgeSourceUid { get; init; }
    public required string KnowledgeSourceName { get; init; }
    public required ResourceNamespace KnowledgeSourceNamespace { get; init; }
    public required long KnowledgeSourceGeneration { get; init; }
    public required ResolvedKnowledgeFlowBinding IngestionFlow { get; init; }
    public required string FlowRunId { get; init; }
    public required KnowledgeAcquisitionState State { get; init; }
    public required string CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string RequestHash { get; init; }
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public required Guid CreatedBy { get; init; }
    public required Guid TenantId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int Attempt { get; init; } = 1;
    public string? RetriedFrom { get; init; }
    public KnowledgeAcquisitionManifest? Manifest { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record StartKnowledgeAcquisitionRequest
{
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string? CorrelationId { get; init; }
}

public sealed record RetryKnowledgeAcquisitionRequest
{
    public string? CorrelationId { get; init; }
}
