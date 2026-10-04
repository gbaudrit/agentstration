using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Knowledge;

public sealed class KnowledgeSnapshotException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record KnowledgeDurableArtifactEvidence(
    string ArtifactId,
    string ProducerFlowRunId,
    string ProducerFlowStepId,
    string StorageFlowRunId,
    string MediaType,
    long Length,
    string Sha256,
    IReadOnlyDictionary<string, string> Provenance);

public interface IKnowledgeSnapshotArtifactResolver
{
    Task<IReadOnlyList<KnowledgeDurableArtifactEvidence>> ResolveAsync(
        Guid workspaceId,
        IReadOnlyList<string> artifactIds,
        CancellationToken cancellationToken);
}

public sealed class KnowledgeSnapshotService(
    IResourceStore store,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    IKnowledgeFlowResolver flows,
    IKnowledgeAcquisitionFlowGateway flowRuns,
    IKnowledgeSnapshotArtifactResolver artifacts,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<(Guid WorkspaceId, string PublicationName), SemaphoreSlim> publicationGates = new();

    public const int MaximumIdempotencyKeyLength = 200;
    public const int MaximumSnapshotArtifacts = KnowledgeAcquisitionService.MaximumManifestArtifacts;

    public async Task<StoredResource<KnowledgeSnapshotResource>> PublishAsync(
        string acquisitionId,
        ResourceNamespace @namespace,
        PublishKnowledgeSnapshotRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        if (idempotencyKey?.Length > MaximumIdempotencyKeyLength)
            throw Error("knowledge_snapshot_idempotency_key_too_long",
                $"Idempotency keys cannot exceed {MaximumIdempotencyKeyLength} characters.");
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var acquisition = await RequireAcquisitionAsync(scopeRef, acquisitionId, @namespace, cancellationToken);
        var artifactIds = SelectArtifactIds(acquisition.Value, request.ArtifactIds ?? []);
        var requestHash = Hash(JsonSerializer.SerializeToElement(new
        {
            acquisition = acquisition.Value.Uid,
            artifactIds,
            request.Activate
        }));
        var publicationName = "publication-" + HashText(string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"{context.WorkspaceId:D}:{requestHash}"
            : $"{context.WorkspaceId:D}:{acquisition.Value.KnowledgeSourceUid:D}:{idempotencyKey}")[..32];
        var gate = publicationGates.GetOrAdd((context.WorkspaceId, publicationName), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var publicationAddress = Scoped(scopeRef, @namespace,
                KnowledgeResourceKinds.KnowledgeSnapshotPublication, publicationName);
            var publication = await store.GetExactAsync<KnowledgeSnapshotPublicationResource>(publicationAddress, cancellationToken);
            if (publication is not null && !string.Equals(publication.Value.RequestHash, requestHash, StringComparison.Ordinal))
                throw Error("knowledge_snapshot_idempotency_conflict",
                    "The idempotency identity is already bound to another snapshot publication request.");
            if (publication?.Value.PublicationState == KnowledgeSnapshotPublicationState.Succeeded)
                return await RequireSnapshotAsync(scopeRef, publication.Value.SnapshotName!, @namespace, cancellationToken);

            if (publication is null)
            {
                publication = await store.PutExactAsync(scopeRef, new KnowledgeSnapshotPublicationResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = KnowledgeResourceKinds.KnowledgeSnapshotPublication,
                    Metadata = new() { Name = publicationName, Namespace = @namespace },
                    ScopeRef = scopeRef,
                    Generation = 1,
                    Status = new() { ProvisioningState = ProvisioningState.Accepted },
                    AcquisitionId = acquisition.Value.Name,
                    AcquisitionUid = acquisition.Value.Uid,
                    KnowledgeSourceUid = acquisition.Value.KnowledgeSourceUid,
                    KnowledgeSourceName = acquisition.Value.KnowledgeSourceName,
                    KnowledgeSourceNamespace = acquisition.Value.KnowledgeSourceNamespace,
                    RequestHash = requestHash,
                    IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                    ArtifactIds = artifactIds,
                    Activate = request.Activate,
                    PublicationState = KnowledgeSnapshotPublicationState.Pending,
                    TenantId = context.TenantId,
                    CreatedBy = context.PrincipalId,
                    CreatedAt = timeProvider.GetUtcNow()
                }, null, true, cancellationToken);
            }
            else if (publication.Value.PublicationState == KnowledgeSnapshotPublicationState.Failed)
            {
                publication = await store.PutExactAsync(scopeRef, publication.Value with
                {
                    Generation = checked(publication.Value.Generation + 1),
                    PublicationState = KnowledgeSnapshotPublicationState.Pending,
                    ErrorCode = null,
                    ErrorMessage = null,
                    CompletedAt = null,
                    Status = new() { ProvisioningState = ProvisioningState.Accepted }
                }, publication.ETag, false, cancellationToken);
            }

            try
            {
                var snapshot = await CompletePublicationAsync(publication, acquisition, context, cancellationToken);
                await audit.WriteAsync(new(SecurityAuditActions.KnowledgeSnapshotPublished,
                    ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
                    WorkspaceId: context.WorkspaceId, ReasonCode: snapshot.Value.Name), cancellationToken);
                return snapshot;
            }
            catch (KnowledgeSnapshotException exception)
            {
                await audit.WriteAsync(new(SecurityAuditActions.KnowledgeSnapshotPublicationFailed,
                    ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
                    WorkspaceId: context.WorkspaceId, ReasonCode: exception.Code), cancellationToken);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<KnowledgeSnapshotView> GetAsync(
        KnowledgeSnapshotId id,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var snapshot = await RequireSnapshotAsync(scopeRef, id.Value, id.Namespace, cancellationToken);
        return await ViewAsync(snapshot.Value, scopeRef, cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeSnapshotView>> ListAsync(
        KnowledgeSourceId sourceId,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await RequireSourceAsync(scopeRef, sourceId.Value, sourceId.Namespace, cancellationToken);
        var values = await store.ListExactAsync<KnowledgeSnapshotResource>(scopeRef,
            KnowledgeResourceKinds.KnowledgeSnapshot, 0, 1000, cancellationToken);
        var result = new List<KnowledgeSnapshotView>();
        foreach (var snapshot in values.Where(value => value.Value.KnowledgeSourceUid == source.Value.Uid)
                     .OrderByDescending(value => value.Value.PublishedAt))
            result.Add(await ViewAsync(snapshot.Value, scopeRef, cancellationToken));
        return result;
    }

    public async Task<KnowledgeSnapshotView?> GetActiveAsync(
        KnowledgeSourceId sourceId,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await RequireSourceAsync(scopeRef, sourceId.Value, sourceId.Namespace, cancellationToken);
        var observed = await LoadObservedAsync(scopeRef, source.Value, cancellationToken);
        if (observed?.Value.ActiveSnapshotName is null) return null;
        var snapshot = await store.GetExactAsync<KnowledgeSnapshotResource>(Scoped(scopeRef, sourceId.Namespace,
            KnowledgeResourceKinds.KnowledgeSnapshot, observed.Value.ActiveSnapshotName), cancellationToken);
        return snapshot is null
            ? throw Error("knowledge_snapshot_active_unavailable", "The active Knowledge Snapshot record is unavailable.")
            : await ViewAsync(snapshot.Value, scopeRef, cancellationToken);
    }

    public async Task<KnowledgeSnapshotResource> ResolveForRetrievalAsync(
        KnowledgeSourceId sourceId,
        string? snapshotName,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await RequireSourceAsync(scopeRef, sourceId.Value, sourceId.Namespace, cancellationToken);
        StoredResource<KnowledgeSnapshotResource> snapshot;
        if (string.IsNullOrWhiteSpace(snapshotName))
        {
            var observed = await LoadObservedAsync(scopeRef, source.Value, cancellationToken);
            if (observed?.Value.ActiveSnapshotName is null)
                throw Error("knowledge_snapshot_active_not_found",
                    $"KnowledgeSource '{source.Value.Address}' has no active Knowledge Snapshot.");
            snapshot = await RequireSnapshotAsync(scopeRef, observed.Value.ActiveSnapshotName,
                sourceId.Namespace, cancellationToken);
        }
        else
        {
            snapshot = await RequireSnapshotAsync(scopeRef, snapshotName.Trim(), sourceId.Namespace, cancellationToken);
        }
        if (snapshot.Value.KnowledgeSourceUid != source.Value.Uid)
            throw Error("knowledge_snapshot_source_mismatch",
                "The selected Knowledge Snapshot belongs to another KnowledgeSource.");
        _ = await artifacts.ResolveAsync(context.WorkspaceId,
            snapshot.Value.Artifacts.Select(value => value.ArtifactId).ToArray(), cancellationToken);
        return snapshot.Value;
    }

    public async Task<KnowledgeSnapshotView> SelectActiveAsync(
        KnowledgeSourceId sourceId,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.ResourcesWrite, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var source = await RequireSourceAsync(scopeRef, sourceId.Value, sourceId.Namespace, cancellationToken);
        var snapshot = await RequireSnapshotAsync(scopeRef, snapshotName, sourceId.Namespace, cancellationToken);
        if (snapshot.Value.KnowledgeSourceUid != source.Value.Uid)
            throw Error("knowledge_snapshot_source_mismatch", "The Knowledge Snapshot belongs to another KnowledgeSource.");
        _ = await artifacts.ResolveAsync(context.WorkspaceId,
            snapshot.Value.Artifacts.Select(value => value.ArtifactId).ToArray(), cancellationToken);
        await SaveObservedAsync(scopeRef, source.Value, snapshot.Value, null, cancellationToken);
        await audit.WriteAsync(new(SecurityAuditActions.KnowledgeSnapshotActivated,
            ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
            WorkspaceId: context.WorkspaceId, ReasonCode: snapshotName), cancellationToken);
        return new(snapshot.Value, KnowledgeSnapshotLifecycleState.Active);
    }

    public async Task<IReadOnlyList<KnowledgeSnapshotPublicationResource>> ListPublicationsAsync(
        string acquisitionId,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsRead, cancellationToken);
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        _ = await RequireAcquisitionAsync(scopeRef, acquisitionId, @namespace, cancellationToken);
        return (await store.ListExactAsync<KnowledgeSnapshotPublicationResource>(scopeRef,
                KnowledgeResourceKinds.KnowledgeSnapshotPublication, 0, 1000, cancellationToken))
            .Select(value => value.Value)
            .Where(value => value.AcquisitionId == acquisitionId && value.Namespace == @namespace)
            .OrderByDescending(value => value.CreatedAt)
            .ToArray();
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var pending = (await store.ListAllAsync<KnowledgeSnapshotPublicationResource>(
            KnowledgeResourceKinds.KnowledgeSnapshotPublication, cancellationToken))
            .Where(value => value.Value.PublicationState == KnowledgeSnapshotPublicationState.Pending)
            .ToArray();
        foreach (var publication in pending)
        {
            var value = publication.Value;
            if (value.ScopeRef is not { Kind: ResourceScopeKind.Workspace, TargetId: { } workspaceId }) continue;
            var acquisition = await store.GetExactAsync<KnowledgeAcquisitionResource>(Scoped(value.ScopeRef.Value,
                value.Namespace, KnowledgeResourceKinds.KnowledgeAcquisition, value.AcquisitionId), cancellationToken);
            if (acquisition is null)
            {
                await FailPublicationAsync(publication, "knowledge_snapshot_acquisition_not_found",
                    $"Knowledge acquisition '{value.AcquisitionId}' was not found during recovery.", null, cancellationToken);
                continue;
            }
            try
            {
                _ = await CompletePublicationAsync(publication, acquisition,
                    new RequestContext(value.CreatedBy, value.TenantId, workspaceId), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
        }
    }

    private async Task<StoredResource<KnowledgeSnapshotResource>> CompletePublicationAsync(
        StoredResource<KnowledgeSnapshotPublicationResource> publication,
        StoredResource<KnowledgeAcquisitionResource> acquisition,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var scopeRef = publication.Value.ScopeRef!.Value;
        try
        {
            ValidateAcquisition(acquisition.Value);
            var source = await RequireSourceAsync(scopeRef, acquisition.Value.KnowledgeSourceName,
                acquisition.Value.KnowledgeSourceNamespace, cancellationToken);
            if (source.Value.Uid != acquisition.Value.KnowledgeSourceUid
                || source.Value.Generation != acquisition.Value.KnowledgeSourceGeneration)
                throw Error("knowledge_snapshot_source_revision_unavailable",
                    "The exact KnowledgeSource revision used by the acquisition is no longer current.");
            var resolved = await flows.ResolveAsync(scopeRef, acquisition.Value.KnowledgeSourceNamespace,
                new KnowledgeFlowTarget
                {
                    Name = acquisition.Value.IngestionFlow.Name,
                    Namespace = acquisition.Value.IngestionFlow.Namespace,
                    Version = acquisition.Value.IngestionFlow.Version,
                    UseActiveVersion = false
                }, cancellationToken);
            if (!string.Equals(resolved.Version, acquisition.Value.IngestionFlow.Version, StringComparison.Ordinal)
                || !string.Equals(resolved.Contract, KnowledgeFlowContracts.Ingestion, StringComparison.Ordinal))
                throw Error("knowledge_snapshot_flow_revision_unavailable",
                    "The exact ingestion Flow revision used by the acquisition is unavailable.");
            var run = await flowRuns.GetAsync(context.WorkspaceId, acquisition.Value.FlowRunId,
                context.TenantId, context.PrincipalId, cancellationToken);
            if (run is null || run.State != KnowledgeAcquisitionState.Succeeded)
                throw Error("knowledge_snapshot_flow_run_unavailable",
                    "The successful ingestion FlowRun used by the acquisition is unavailable.");

            var evidence = await artifacts.ResolveAsync(context.WorkspaceId, publication.Value.ArtifactIds, cancellationToken);
            var manifest = acquisition.Value.Manifest!.Artifacts.ToDictionary(value => value.ArtifactId, StringComparer.Ordinal);
            var normalized = new List<KnowledgeSnapshotArtifact>(evidence.Count);
            foreach (var artifact in evidence)
            {
                if (!manifest.TryGetValue(artifact.ArtifactId, out var item)
                    || item.Kind != KnowledgeArtifactKind.Durable
                    || item.Disposition != KnowledgeArtifactDisposition.Publishable)
                    throw Error("knowledge_snapshot_artifact_not_publishable",
                        $"Artifact '{artifact.ArtifactId}' is not a publishable durable output of the acquisition.");
                if (!string.Equals(artifact.ProducerFlowRunId, acquisition.Value.FlowRunId, StringComparison.Ordinal))
                    throw Error("knowledge_snapshot_artifact_lineage_invalid",
                        $"Artifact '{artifact.ArtifactId}' was not produced by the acquisition FlowRun.");
                if (artifact.Length < 0 || string.IsNullOrWhiteSpace(artifact.MediaType)
                    || string.IsNullOrWhiteSpace(artifact.Sha256)
                    || !string.IsNullOrWhiteSpace(item.Digest)
                    && !SameDigest(item.Digest, artifact.Sha256))
                    throw Error("knowledge_snapshot_artifact_integrity_invalid",
                        $"Artifact '{artifact.ArtifactId}' has invalid or inconsistent integrity metadata.");
                normalized.Add(new()
                {
                    ArtifactId = artifact.ArtifactId,
                    ProducerFlowRunId = artifact.ProducerFlowRunId,
                    ProducerFlowStepId = artifact.ProducerFlowStepId,
                    StorageFlowRunId = artifact.StorageFlowRunId,
                    MediaType = artifact.MediaType,
                    Length = artifact.Length,
                    Sha256 = artifact.Sha256,
                    Provenance = new Dictionary<string, string>(artifact.Provenance, StringComparer.Ordinal)
                });
            }

            var contentHash = Hash(JsonSerializer.SerializeToElement(new
            {
                acquisition = acquisition.Value.Uid,
                artifacts = normalized.Select(value => new { value.ArtifactId, value.Sha256 }).ToArray()
            }));
            var snapshotName = "snapshot-" + contentHash[..32];
            var snapshotAddress = Scoped(scopeRef, acquisition.Value.KnowledgeSourceNamespace,
                KnowledgeResourceKinds.KnowledgeSnapshot, snapshotName);
            var snapshot = await store.GetExactAsync<KnowledgeSnapshotResource>(snapshotAddress, cancellationToken);
            if (snapshot is null)
            {
                try
                {
                    snapshot = await store.CreateImmutableAsync(new KnowledgeSnapshotResource
                    {
                        ApiVersion = ResourceApiVersions.CoreV1,
                        Kind = KnowledgeResourceKinds.KnowledgeSnapshot,
                        Metadata = new() { Name = snapshotName, Namespace = acquisition.Value.KnowledgeSourceNamespace },
                        ScopeRef = scopeRef,
                        Generation = 1,
                        Status = new() { ProvisioningState = ProvisioningState.Succeeded },
                        KnowledgeSourceUid = acquisition.Value.KnowledgeSourceUid,
                        KnowledgeSourceName = acquisition.Value.KnowledgeSourceName,
                        KnowledgeSourceNamespace = acquisition.Value.KnowledgeSourceNamespace,
                        KnowledgeSourceGeneration = acquisition.Value.KnowledgeSourceGeneration,
                        AcquisitionId = acquisition.Value.Name,
                        AcquisitionUid = acquisition.Value.Uid,
                        AcquiredAt = acquisition.Value.CompletedAt!.Value,
                        IngestionFlow = acquisition.Value.IngestionFlow,
                        Profile = acquisition.Value.Profile,
                        IngestionFlowRunId = acquisition.Value.FlowRunId,
                        PublicationId = publication.Value.Name,
                        RequestHash = contentHash,
                        PublishedAt = timeProvider.GetUtcNow(),
                        PublishedBy = context.PrincipalId,
                        Artifacts = normalized
                    }, cancellationToken);
                }
                catch (ResourceConcurrencyException)
                {
                    snapshot = await store.GetExactAsync<KnowledgeSnapshotResource>(snapshotAddress, cancellationToken)
                        ?? throw new ResourceConcurrencyException(
                            $"Knowledge Snapshot '{snapshotName}' was created concurrently but could not be reloaded.");
                }
            }
            if (!string.Equals(snapshot.Value.RequestHash, contentHash, StringComparison.Ordinal))
                throw Error("knowledge_snapshot_immutable_conflict", "The immutable Knowledge Snapshot identity has conflicting content.");
            await SaveObservedAsync(scopeRef, source.Value,
                publication.Value.Activate ? snapshot.Value : null, publication.Value.Name, cancellationToken);
            var completed = publication.Value with
            {
                Generation = checked(publication.Value.Generation + 1),
                PublicationState = KnowledgeSnapshotPublicationState.Succeeded,
                SnapshotName = snapshot.Value.Name,
                CompletedAt = timeProvider.GetUtcNow(),
                ErrorCode = null,
                ErrorMessage = null,
                Status = new() { ProvisioningState = ProvisioningState.Succeeded }
            };
            _ = await store.PutExactAsync(scopeRef, completed, publication.ETag, false, cancellationToken);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var mapped = exception as KnowledgeSnapshotException
                ?? new KnowledgeSnapshotException("knowledge_snapshot_publication_failed", exception.Message, exception);
            await FailPublicationAsync(publication, mapped.Code, mapped.Message, acquisition.Value, cancellationToken);
            throw mapped;
        }
    }

    private async Task FailPublicationAsync(
        StoredResource<KnowledgeSnapshotPublicationResource> publication,
        string code,
        string message,
        KnowledgeAcquisitionResource? acquisition,
        CancellationToken cancellationToken)
    {
        var failed = publication.Value with
        {
            Generation = checked(publication.Value.Generation + 1),
            PublicationState = KnowledgeSnapshotPublicationState.Failed,
            CompletedAt = timeProvider.GetUtcNow(),
            ErrorCode = code,
            ErrorMessage = message,
            Status = new() { ProvisioningState = ProvisioningState.Failed }
        };
        try { _ = await store.PutExactAsync(publication.Value.ScopeRef!.Value, failed, publication.ETag, false, cancellationToken); }
        catch (ResourceConcurrencyException) { }
        if (acquisition is null) return;
        var source = await store.GetExactAsync<KnowledgeSourceResource>(Scoped(publication.Value.ScopeRef!.Value,
            acquisition.KnowledgeSourceNamespace, KnowledgeResourceKinds.KnowledgeSource,
            acquisition.KnowledgeSourceName), cancellationToken);
        if (source is not null)
            await SaveFailureObservedAsync(publication.Value.ScopeRef.Value, source.Value,
                publication.Value.Name, code, message, cancellationToken);
    }

    private async Task<KnowledgeSnapshotView> ViewAsync(
        KnowledgeSnapshotResource snapshot,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var observed = await LoadObservedAsync(scopeRef, new KnowledgeSourceResource
        {
            Metadata = new() { Name = snapshot.KnowledgeSourceName, Namespace = snapshot.KnowledgeSourceNamespace },
            Definition = new() { DisplayName = snapshot.KnowledgeSourceName },
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Uid = snapshot.KnowledgeSourceUid
        }, cancellationToken);
        try
        {
            _ = await artifacts.ResolveAsync(scopeRef.TargetId!.Value,
                snapshot.Artifacts.Select(value => value.ArtifactId).ToArray(), cancellationToken);
        }
        catch (KnowledgeSnapshotException)
        {
            return new(snapshot, KnowledgeSnapshotLifecycleState.Unavailable);
        }
        return new(snapshot, observed?.Value.ActiveSnapshotUid == snapshot.Uid
            ? KnowledgeSnapshotLifecycleState.Active
            : KnowledgeSnapshotLifecycleState.Superseded);
    }

    private async Task SaveObservedAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceResource source,
        KnowledgeSnapshotResource? active,
        string? publicationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await LoadObservedAsync(scopeRef, source, cancellationToken);
            var value = new KnowledgeSnapshotObservedResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSnapshotObservedState,
                Metadata = new() { Name = ObservedName(source.Uid), Namespace = source.Namespace },
                ScopeRef = scopeRef,
                Uid = current?.Value.Uid ?? Guid.Empty,
                Generation = current is null ? 1 : checked(current.Value.Generation + 1),
                Status = new() { ProvisioningState = ProvisioningState.Succeeded },
                KnowledgeSourceUid = source.Uid,
                ActiveSnapshotName = active?.Name ?? current?.Value.ActiveSnapshotName,
                ActiveSnapshotUid = active?.Uid ?? current?.Value.ActiveSnapshotUid,
                LastPublicationId = publicationId ?? current?.Value.LastPublicationId,
                LastPublishedAt = publicationId is null ? current?.Value.LastPublishedAt : timeProvider.GetUtcNow(),
                LastAttemptAt = timeProvider.GetUtcNow(),
                LastErrorCode = null,
                LastErrorMessage = null
            };
            try
            {
                _ = await store.PutExactAsync(scopeRef, value, current?.ETag, current is null, cancellationToken);
                return;
            }
            catch (ResourceConcurrencyException) when (attempt < 2) { }
        }
        throw Error("knowledge_snapshot_selection_conflict", "The active Knowledge Snapshot changed concurrently.");
    }

    private async Task SaveFailureObservedAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceResource source,
        string publicationId,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var current = await LoadObservedAsync(scopeRef, source, cancellationToken);
        var value = new KnowledgeSnapshotObservedResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSnapshotObservedState,
            Metadata = new() { Name = ObservedName(source.Uid), Namespace = source.Namespace },
            ScopeRef = scopeRef,
            Uid = current?.Value.Uid ?? Guid.Empty,
            Generation = current is null ? 1 : checked(current.Value.Generation + 1),
            Status = new() { ProvisioningState = ProvisioningState.Failed },
            KnowledgeSourceUid = source.Uid,
            ActiveSnapshotName = current?.Value.ActiveSnapshotName,
            ActiveSnapshotUid = current?.Value.ActiveSnapshotUid,
            LastPublicationId = publicationId,
            LastPublishedAt = current?.Value.LastPublishedAt,
            LastAttemptAt = timeProvider.GetUtcNow(),
            LastErrorCode = code,
            LastErrorMessage = message
        };
        try { _ = await store.PutExactAsync(scopeRef, value, current?.ETag, current is null, cancellationToken); }
        catch (ResourceConcurrencyException) { }
    }

    private Task<StoredResource<KnowledgeSnapshotObservedResource>?> LoadObservedAsync(
        ResourceScopeRef scopeRef,
        KnowledgeSourceResource source,
        CancellationToken cancellationToken) => store.GetExactAsync<KnowledgeSnapshotObservedResource>(Scoped(scopeRef,
            source.Namespace, KnowledgeResourceKinds.KnowledgeSnapshotObservedState, ObservedName(source.Uid)), cancellationToken);

    private async Task<StoredResource<KnowledgeAcquisitionResource>> RequireAcquisitionAsync(
        ResourceScopeRef scopeRef,
        string id,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken) => await store.GetExactAsync<KnowledgeAcquisitionResource>(Scoped(scopeRef,
            @namespace, KnowledgeResourceKinds.KnowledgeAcquisition, id), cancellationToken)
        ?? throw Error("knowledge_snapshot_acquisition_not_found", $"Knowledge acquisition '{id}' was not found.");

    private async Task<StoredResource<KnowledgeSourceResource>> RequireSourceAsync(
        ResourceScopeRef scopeRef,
        string name,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken) => await store.GetExactAsync<KnowledgeSourceResource>(Scoped(scopeRef,
            @namespace, KnowledgeResourceKinds.KnowledgeSource, name), cancellationToken)
        ?? throw Error("knowledge_snapshot_source_not_found", $"KnowledgeSource '{@namespace}/{name}' was not found.");

    private async Task<StoredResource<KnowledgeSnapshotResource>> RequireSnapshotAsync(
        ResourceScopeRef scopeRef,
        string name,
        ResourceNamespace @namespace,
        CancellationToken cancellationToken) => await store.GetExactAsync<KnowledgeSnapshotResource>(Scoped(scopeRef,
            @namespace, KnowledgeResourceKinds.KnowledgeSnapshot, name), cancellationToken)
        ?? throw Error("knowledge_snapshot_not_found", $"Knowledge Snapshot '{@namespace}/{name}' was not found.");

    private static IReadOnlyList<string> SelectArtifactIds(
        KnowledgeAcquisitionResource acquisition,
        IReadOnlyList<string> requested)
    {
        if (acquisition.State != KnowledgeAcquisitionState.Succeeded || acquisition.Manifest is null)
            throw Error("knowledge_snapshot_acquisition_not_publishable", "Only a successful acquisition with a manifest can be published.");
        var values = requested.Count == 0
            ? acquisition.Manifest.Artifacts.Where(value => value.Kind == KnowledgeArtifactKind.Durable
                && value.Disposition == KnowledgeArtifactDisposition.Publishable).Select(value => value.ArtifactId).ToArray()
            : requested.Select(value => value?.Trim() ?? string.Empty).ToArray();
        if (values.Length == 0 || values.Length > MaximumSnapshotArtifacts
            || values.Any(string.IsNullOrWhiteSpace)
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw Error("knowledge_snapshot_artifact_selection_invalid",
                $"Select 1 to {MaximumSnapshotArtifacts} distinct durable artifacts.");
        return values;
    }

    private static void ValidateAcquisition(KnowledgeAcquisitionResource acquisition)
    {
        if (acquisition.State != KnowledgeAcquisitionState.Succeeded || acquisition.Manifest is null
            || acquisition.CompletedAt is null)
            throw Error("knowledge_snapshot_acquisition_not_publishable",
                "Only a completed successful acquisition with a valid manifest can be published.");
    }

    private RequestContext RequireContext() => requestContext.IsInitialized
        ? requestContext.Current
        : throw Error("knowledge_snapshot_context_required", "A Workspace request context is required.");
    private static string ObservedName(Guid sourceUid) => $"snapshot-state-{sourceUid:N}";
    private static bool SameDigest(string left, string right) =>
        string.Equals(left.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase),
            right.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    private static string Hash(JsonElement value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
    private static string HashText(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ScopedResourceAddress Scoped(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string kind, string name) =>
        ScopedResourceAddress.Create(scopeRef, @namespace, kind, name);
    private static KnowledgeSnapshotException Error(string code, string message) => new(code, message);
}
