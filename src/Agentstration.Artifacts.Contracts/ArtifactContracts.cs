using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Artifacts.Contracts;

public static class ArtifactResourceKinds
{
    public const string ArtifactStagingBinding = "ArtifactStagingBinding";
    public const string StagedArtifact = "StagedArtifact";
    public const string FlowRunArtifact = "FlowRunArtifact";
}

public static class ArtifactCapabilities
{
    public const string Create = "artifact.staging.create";
    public const string Write = "artifact.staging.write";
    public const string Read = "artifact.staging.read";
    public const string Stat = "artifact.staging.stat";
    public const string Delete = "artifact.staging.delete";
    public static IReadOnlyList<string> All { get; } = [Create, Write, Read, Stat, Delete];
}

public static class ArtifactFlowContracts
{
    public const string StorageWrite = "artifact.storage.write/v1";
    public const string StorageRead = "artifact.storage.read/v1";
    public const string Transform = "artifact.transform/v1";
}

public readonly record struct StagedArtifactId(Guid Value)
{
    public static StagedArtifactId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
    public static StagedArtifactId Parse(string value) => new(Guid.ParseExact(value, "N"));
}

public readonly record struct ArtifactLeaseId(Guid Value)
{
    public static ArtifactLeaseId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
    public static ArtifactLeaseId Parse(string value) => new(Guid.ParseExact(value, "N"));
}

public readonly record struct FlowRunArtifactId(Guid Value)
{
    public static FlowRunArtifactId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
    public static FlowRunArtifactId Parse(string value) => new(Guid.ParseExact(value, "N"));
}

public enum StagedArtifactStatus
{
    Open,
    Sealed,
    Persisting,
    Persisted,
    Expired,
    Discarded,
    Purging,
    Purged,
    Failed
}

public enum ArtifactProducerKind { FlowRun, RuntimeRun, Agent, Tool }
public enum ArtifactLeaseOperation { Inspect, Read, Persist, Consume }
public enum ArtifactHandoffMode { Reuse, Copy, Move, ReuseOrCopy }

public sealed record ArtifactProducer
{
    public required ArtifactProducerKind Kind { get; init; }
    public required string Id { get; init; }
    public string? FlowRunId { get; init; }
    public string? FlowStepId { get; init; }
    public string? RuntimeRunId { get; init; }
    public string? AgentId { get; init; }
    public string? ToolCallId { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();
}

public sealed record ArtifactBackendResolution
{
    public required string BindingName { get; init; }
    public ResourceNamespace BindingNamespace { get; init; } = ResourceNamespace.Default;
    public required string ToolSetName { get; init; }
    public ResourceNamespace ToolSetNamespace { get; init; } = ResourceNamespace.Default;
    public required string ToolSetVersion { get; init; }
    public required string BackendReference { get; init; }
}

public sealed record ArtifactLease
{
    public required ArtifactLeaseId Id { get; init; }
    public required string ConsumerKind { get; init; }
    public required string ConsumerId { get; init; }
    public IReadOnlyList<ArtifactLeaseOperation> Operations { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed record ArtifactStagingBindingProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsDefault { get; init; }
    public required ResourceReference ToolSet { get; init; }
    public required string ToolSetVersion { get; init; }
    public long MaximumArtifactBytes { get; init; } = 64 * 1024 * 1024;
    public TimeSpan DefaultRetention { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan MaximumRetention { get; init; } = TimeSpan.FromDays(7);
}

public sealed record ArtifactStagingBindingResource : Resource
{
    public ArtifactStagingBindingProperties Definition { get; init; } = null!;
}

public sealed record StagedArtifactResource : Resource
{
    public required Guid TenantId { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required StagedArtifactId ArtifactId { get; init; }
    public StagedArtifactId? SourceArtifactId { get; init; }
    public string? TransferOperationId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
    public long Length { get; init; }
    public string? Sha256 { get; init; }
    public required StagedArtifactStatus ArtifactStatus { get; init; }
    public required ArtifactProducer Producer { get; init; }
    public required ArtifactBackendResolution Backend { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SealedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? PersistedAt { get; init; }
    public DateTimeOffset? PurgedAt { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public IReadOnlyList<ArtifactLease> Leases { get; init; } = [];
}

public sealed record ArtifactStorageReceipt
{
    public required string StorageFlowRunId { get; init; }
    public required string OpaqueReference { get; init; }
    public required string MediaType { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();
}

public sealed record FlowRunArtifactResource : Resource, IImmutableResource
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required FlowRunArtifactId ArtifactId { get; init; }
    public required StagedArtifactId SourceArtifactId { get; init; }
    public required string ProducerFlowRunId { get; init; }
    public required string ProducerFlowStepId { get; init; }
    public required ArtifactStorageReceipt Receipt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record CreateArtifactStagingBindingRequest(
    string Name,
    string? Namespace,
    ArtifactStagingBindingProperties Properties);

public sealed record CreateStagedArtifactRequest(
    string FileName,
    string MediaType,
    ArtifactProducer Producer,
    ResourceReference? Binding = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record WriteStagedArtifactRequest(long Offset, string ContentBase64);
public sealed record CreateArtifactLeaseRequest(
    string ConsumerKind,
    string ConsumerId,
    IReadOnlyList<ArtifactLeaseOperation> Operations,
    DateTimeOffset ExpiresAt);
public sealed record ExtendArtifactRetentionRequest(DateTimeOffset ExpiresAt);
public sealed record RevokeArtifactLeaseRequest(string LeaseId);
public sealed record ArtifactHandoffRequest(
    string OperationId,
    ArtifactHandoffMode Mode,
    string ConsumerKind,
    string ConsumerId,
    IReadOnlyList<ArtifactLeaseOperation> Operations,
    DateTimeOffset LeaseExpiresAt,
    ResourceReference? TargetBinding = null);
public sealed record ArtifactHandoffResult(StagedArtifactView Artifact, ArtifactLease Lease, bool Reused, bool Recovered);
public sealed record CompleteFlowRunArtifactRequest(
    string ProducerFlowRunId,
    string ProducerFlowStepId,
    ArtifactStorageReceipt Receipt);

public sealed record MaterializeFlowRunArtifactRequest(
    string? FlowName = null,
    string? FlowNamespace = null,
    string? FlowVersion = null,
    string? IdempotencyKey = null);

public sealed record FlowRunArtifactMaterialization(
    string FlowRunId,
    string Status,
    StagedArtifactId? StagedArtifactId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool StagedArtifactAvailable = false,
    DateTimeOffset? StagedArtifactExpiresAt = null);

public sealed record ArtifactContentChunk(long Offset, string ContentBase64, bool EndOfContent);

public sealed record StagedArtifactView(
    StagedArtifactId ArtifactId,
    StagedArtifactId? SourceArtifactId,
    string FileName,
    string MediaType,
    long Length,
    string? Sha256,
    StagedArtifactStatus Status,
    ArtifactProducer Producer,
    string BindingName,
    ResourceNamespace BindingNamespace,
    string ToolSetName,
    ResourceNamespace ToolSetNamespace,
    string ToolSetVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SealedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? PersistedAt,
    DateTimeOffset? PurgedAt,
    string? FailureCode,
    string? FailureMessage,
    IReadOnlyList<ArtifactLease> Leases);

public static class ArtifactViews
{
    public static StagedArtifactView Staged(StagedArtifactResource value) => new(
        value.ArtifactId, value.SourceArtifactId, value.FileName, value.MediaType, value.Length, value.Sha256,
        value.ArtifactStatus, value.Producer, value.Backend.BindingName, value.Backend.BindingNamespace,
        value.Backend.ToolSetName, value.Backend.ToolSetNamespace, value.Backend.ToolSetVersion,
        value.CreatedAt, value.SealedAt, value.ExpiresAt, value.PersistedAt, value.PurgedAt,
        value.FailureCode, value.FailureMessage, value.Leases);
}
public sealed record ArtifactStorageWriteInput(string StagedArtifactId, string ProducerFlowRunId, string ProducerFlowStepId);
public sealed record ArtifactStorageReadInput(string FlowRunArtifactId);

public sealed record ArtifactBackendCommandContext(
    Guid TenantId,
    WorkspaceId WorkspaceId,
    Guid PrincipalId,
    string CallId,
    string? CorrelationId,
    string? RunId,
    string? FlowStepId);

public sealed record ArtifactBackendCreateResult(string BackendReference);
public sealed record ArtifactBackendStat(long Length, string Sha256);

public interface IArtifactStagingToolExecutor
{
    Task ValidateAsync(ArtifactStagingBindingResource binding, ArtifactBackendCommandContext context, CancellationToken cancellationToken);
    Task<ArtifactBackendCreateResult> CreateAsync(ArtifactStagingBindingResource binding, StagedArtifactId artifactId,
        string mediaType, ArtifactBackendCommandContext context, CancellationToken cancellationToken);
    Task<long> WriteAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend, long offset,
        ReadOnlyMemory<byte> content, ArtifactBackendCommandContext context, CancellationToken cancellationToken);
    Task<ArtifactContentChunk> ReadAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
        long offset, int length, ArtifactBackendCommandContext context, CancellationToken cancellationToken);
    Task<ArtifactBackendStat> StatAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
        ArtifactBackendCommandContext context, CancellationToken cancellationToken);
    Task DeleteAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
        ArtifactBackendCommandContext context, CancellationToken cancellationToken);
}

public interface IArtifactContentStore
{
    Task<string> CreateAsync(WorkspaceId workspaceId, StagedArtifactId artifactId, CancellationToken cancellationToken);
    Task<long> WriteAsync(WorkspaceId workspaceId, string backendReference, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    Task<ReadOnlyMemory<byte>> ReadAsync(WorkspaceId workspaceId, string backendReference, long offset, int length, CancellationToken cancellationToken);
    Task<ArtifactBackendStat> StatAsync(WorkspaceId workspaceId, string backendReference, CancellationToken cancellationToken);
    Task DeleteAsync(WorkspaceId workspaceId, string backendReference, CancellationToken cancellationToken);
}

public interface IArtifactDurableStore
{
    Task ResetAsync(WorkspaceId workspaceId, string opaqueReference, CancellationToken cancellationToken);
    Task<long> WriteAsync(WorkspaceId workspaceId, string opaqueReference, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    Task<ReadOnlyMemory<byte>> ReadAsync(WorkspaceId workspaceId, string opaqueReference, long offset, int length, CancellationToken cancellationToken);
    Task<ArtifactBackendStat> StatAsync(WorkspaceId workspaceId, string opaqueReference, CancellationToken cancellationToken);
}

public sealed class ArtifactValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
