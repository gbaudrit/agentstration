using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.SystemOperations.Contracts;

public sealed record SystemOperationsQuery(string Root, ResourceNamespace? Namespace = null, int WindowMinutes = 15, int MaximumRuns = 50);
public sealed record SystemWorkerEvidence(Guid WorkerId, Guid SessionId, Guid AttemptId, long FencingGeneration,
    string AttemptState, DateTimeOffset AcquiredAt, DateTimeOffset LastHeartbeatAt, DateTimeOffset LeaseExpiresAt,
    DateTimeOffset? CompletedAt, string Presence);
public sealed record SystemPlacementEvidence(Guid AssignmentId, string State, IReadOnlyList<SystemWorkerEvidence> Attempts);
public sealed record SystemRunEvidence(string Family, string RunId, ResourceScopeRef InvocationScope, ResourceScopeRef DataScope,
    ResourceScopeRef DefinitionOwner, ResourceNamespace DefinitionNamespace, Guid ResourceUid, Guid? RevisionUid, string? Publication, string? DefinitionHash,
    string State, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    long? InputTokens, long? OutputTokens, string TokenCoverage, IReadOnlyList<SystemPlacementEvidence> Placements,
    string? WorkItemId = null, string? WorkTaskId = null);
public sealed record SystemRunMetrics(int ObservedActive, int ObservedSucceeded, int ObservedFailed, int ObservedCancelled,
    double? MeanCompletedDurationMilliseconds, long? ReportedInputTokens, long? ReportedOutputTokens, string TokenCoverage);
public sealed record SystemOperationalFacts(string NodeId, SystemGraphIdentity Identity, string PublicationState,
    string ProvisioningState, string DependencyState, string WorkerPresence, SystemRunMetrics? Metrics,
    IReadOnlyList<SystemRunEvidence> Runs, ResourceScopeRef InvocationScope, ResourceScopeRef DataScope,
    DateTimeOffset WindowStart, DateTimeOffset SampledAt, string Coverage, bool CanInspectWorkerDetails,
    IReadOnlyList<string> Limitations);
public sealed record SystemOperationalSnapshot(SystemOperationalFacts? Facts, DateTimeOffset SampledAt, string Coverage);
public sealed record SystemOperationsFrame(Guid Epoch, long Sequence, long BaseSequence, string Kind,
    DateTimeOffset SampledAt, SystemOperationalFacts? Upsert = null, bool Clear = false);

public interface ISystemOperationsService
{
    Task<SystemOperationalSnapshot> SnapshotAsync(SystemOperationsQuery query, CancellationToken ct);
}

// Server-only ports: current authorization and family-owned evidence, never client-supplied authority.
public sealed record SystemOperationsReadContext(Guid ActorId, ResourceScopeRef InvocationScope, ResourceScopeRef DataScope,
    Guid TenantId, Guid WorkspaceId, bool CanReadRuns, bool CanReadWork, bool CanInspectWorkers, string AuthorizationKey);
public interface ISystemOperationsAccess
{
    Task<SystemOperationsReadContext> GetAsync(CancellationToken ct);
    Task<bool> CanDeliverAsync(SystemGraphIdentity identity, SystemOperationsReadContext expected, bool inspectBindings, CancellationToken ct);
}
public sealed record SystemRunEvidencePage(IReadOnlyList<SystemRunEvidence> Runs, bool HasMore, string Coverage);
public interface ISystemOperationsEvidenceReader
{
    Task<SystemRunEvidencePage> ReadAsync(SystemGraphNode root, SystemOperationsReadContext context, DateTimeOffset windowStart, int take, CancellationToken ct);
    Task<long?> CurrentGenerationAsync(SystemGraphIdentity identity, CancellationToken ct);
    Task<string> ProvisioningAsync(SystemGraphNode root, CancellationToken ct);
}
