using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Knowledge;

/// <summary>Retires producer-owned content after replacement, without deleting immutable provenance.</summary>
public sealed class SourceHistoryMaintenance(IResourceStore resources, ICurrentRequestContext current,
    IRequestContextScopeFactory scopes, IAuthorizationService authorization, IResourceAccessPolicyService access,
    IStorageContentGateway content, IStoragePublicationGateway publications, TimeProvider clock, IFlowRepository flowRuns)
    : IDataSourceHistoryMaintenance, IKnowledgeHistoryMaintenance, IArtifactMaterializationAdmission
{
    public async Task MaintainAsync(DataSourceAcquisitionResource completed, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try { await MaintainDatasetAsync(completed, budget.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception exception) when (Expected(exception)) { }
    }
    private async Task MaintainDatasetAsync(DataSourceAcquisitionResource completed, CancellationToken ct)
    {
        var context = Context(); var consumer = Consumer();
        if (context.WorkspaceId != completed.WorkspaceId || context.TenantId != completed.TenantId) return;
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, ct);
        StoredResource<DataSourceResource>? source;
        using (scopes.PushSystem()) source = await resources.GetExactAsync<DataSourceResource>(new(completed.DataSourceScopeRef,
            completed.DataSourceNamespace, DataSourceResourceKinds.DataSource, completed.DataSourceName), ct);
        if (source?.Value.Uid != completed.DataSourceUid || source.Value.Definition.Dataset?.Lifecycle is not { } policy) return;
        await EnsureUseAsync(source.Value, consumer, ct);
        var values = await WorkspaceListAsync<DataSourceAcquisitionResource>(DataSourceResourceKinds.DataSourceAcquisition, ct);
        var succeeded = values.Where(value => value.Value.DataSourceUid == source.Value.Uid && value.Value.State == DataSourceAcquisitionState.Succeeded
            && value.Value.Content is not null && value.Value.ContentRetiredAt is null).OrderByDescending(value => value.Value.CompletedAt)
            .ThenByDescending(value => value.Value.Name, StringComparer.Ordinal).ToArray();
        var hasCurrentSuccess = values.Any(value => value.Value.DataSourceUid == source.Value.Uid
            && value.Value.DataSourceGeneration == source.Value.Generation && value.Value.State == DataSourceAcquisitionState.Succeeded);
        var latest = succeeded.FirstOrDefault(value => value.Value.DataSourceGeneration == source.Value.Generation)
            ?? (hasCurrentSuccess ? null : succeeded.FirstOrDefault());
        if (succeeded.Length == 0) return;
        if (await HasActiveFlowAsync(ct)) return;
        var projections = await WorkspaceListAsync<KnowledgeProjectionResource>(KnowledgeResourceKinds.KnowledgeProjection, ct);
        foreach (var candidate in succeeded.Where(value => (latest is null || value.Value.Uid != latest.Value.Uid)
            && (!policy.KeepHistory || value.Value.CompletedAt + policy.HistoryRetention <= clock.GetUtcNow())).Take(16))
        {
            var item = candidate.Value;
            if (item.ProjectionLeases.Any(pair => pair.Value > clock.GetUtcNow())
                || projections.Any(projection => projection.Value.State == KnowledgeProjectionState.Running
                    && projection.Value.Inputs.Any(input => input.AcquisitionUid == item.Uid))) continue;
            try
            {
                var claimed = await InWorkspaceAsync(() => resources.PutExactAsync(item.ScopeRef!.Value, item with
                { Generation = checked(item.Generation + 1), ContentRetiringAt = clock.GetUtcNow() }, candidate.ETag, false, ct));
                if (await HasActiveFlowAsync(ct))
                {
                    _ = await InWorkspaceAsync(() => resources.PutExactAsync(item.ScopeRef!.Value, claimed.Value with
                    { Generation = checked(claimed.Value.Generation + 1), ContentRetiringAt = null, ContentRetirementPending = true }, claimed.ETag, false, ct));
                    continue;
                }
                var target = DataSourceDatasetCompositionService.Target(item, consumer);
                var retired = await content.RetireAsync(target, item.Content!, ct);
                _ = await InWorkspaceAsync(() => resources.PutExactAsync(item.ScopeRef!.Value, claimed.Value with
                {
                    Generation = checked(claimed.Value.Generation + 1),
                    ContentRetiringAt = null,
                    ContentRetiredAt = retired ? clock.GetUtcNow() : null,
                    ContentRetirementPending = !retired
                }, claimed.ETag, false, ct));
            }
            catch (Exception exception) when (Expected(exception)) { }
        }
    }

    public async Task MaintainAsync(KnowledgeSourceResource source, CancellationToken ct)
    {
        if (source.Definition.Lifecycle is not { } policy) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try { await MaintainSnapshotsAsync(source, policy, budget.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception exception) when (Expected(exception)) { }
    }
    private async Task MaintainSnapshotsAsync(KnowledgeSourceResource source, KnowledgeProjectionPolicy policy, CancellationToken ct)
    {
        var context = Context();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, ct);
        await EnsureUseAsync(source, Consumer(), ct);
        if (await HasActiveFlowAsync(ct)) return;
        var all = await WorkspaceListAsync<KnowledgeSnapshotResource>(KnowledgeResourceKinds.KnowledgeSnapshot, ct);
        var states = await WorkspaceListAsync<KnowledgeSnapshotObservedResource>(KnowledgeResourceKinds.KnowledgeSnapshotObservedState, ct);
        var retired = states.SelectMany(state => state.Value.RetiredSnapshots.Keys).ToHashSet();
        foreach (var snapshot in all.Where(value => value.Value.KnowledgeSourceUid == source.Uid && !retired.Contains(value.Value.Uid))
            .OrderBy(value => value.Value.PublishedAt).Take(16))
        {
            if (policy.KeepHistory && snapshot.Value.PublishedAt + policy.HistoryRetention > clock.GetUtcNow()) continue;
            try { await RetireSnapshotAsync(source, snapshot.Value, all.Select(value => value.Value).ToArray(), ct); }
            catch (Exception exception) when (Expected(exception)) { }
        }
    }
    private async Task RetireSnapshotAsync(KnowledgeSourceResource source, KnowledgeSnapshotResource snapshot,
        IReadOnlyList<KnowledgeSnapshotResource> all, CancellationToken ct)
    {
        var scope = ResourceScopeRef.Workspace(Context().WorkspaceId);
        var key = new ScopedResourceAddress(scope, source.Namespace, KnowledgeResourceKinds.KnowledgeSnapshotObservedState, $"snapshot-state-{source.Uid:N}");
        var observed = await InWorkspaceAsync(() => resources.GetExactAsync<KnowledgeSnapshotObservedResource>(key, ct));
        if (observed is null || observed.Value.ActiveSnapshotUid == snapshot.Uid || observed.Value.RetiredSnapshots.ContainsKey(snapshot.Uid)) return;
        var pendingBeforeClaim = await WorkspaceListAsync<KnowledgeProjectionResource>(KnowledgeResourceKinds.KnowledgeProjection, ct);
        if (pendingBeforeClaim.Any(value => value.Value.Name == snapshot.ProjectionId && value.Value.State == KnowledgeProjectionState.Running)) return;
        var claimed = observed.Value.RetiringSnapshots.Contains(snapshot.Uid) ? observed
            : await InWorkspaceAsync(() => resources.PutExactAsync(scope, observed.Value with
            { Generation = checked(observed.Value.Generation + 1), RetiringSnapshots = [.. observed.Value.RetiringSnapshots, snapshot.Uid] }, observed.ETag, false, ct));
        if (await HasActiveFlowAsync(ct))
        {
            _ = await InWorkspaceAsync(() => resources.PutExactAsync(scope, claimed.Value with
            { Generation = checked(claimed.Value.Generation + 1), RetiringSnapshots = claimed.Value.RetiringSnapshots.Where(id => id != snapshot.Uid).ToArray() }, claimed.ETag, false, ct));
            return;
        }
        // The claim blocks new sessions. A session that crossed the claim must check again after its conditional write.
        var leases = await WorkspaceListAsync<KnowledgeReadSessionResource>(KnowledgeAccessService.SessionKind, ct);
        if (leases.Any(value => value.Value.SnapshotUid == snapshot.Uid && value.Value.ExpiresAt > clock.GetUtcNow())) return;
        var projections = await WorkspaceListAsync<KnowledgeProjectionResource>(KnowledgeResourceKinds.KnowledgeProjection, ct);
        if (projections.Any(value => value.Value.Name == snapshot.ProjectionId && value.Value.State == KnowledgeProjectionState.Running)) return;
        var states = await WorkspaceListAsync<KnowledgeSnapshotObservedResource>(KnowledgeResourceKinds.KnowledgeSnapshotObservedState, ct);
        var retiredIds = states.SelectMany(value => value.Value.RetiredSnapshots.Keys).ToHashSet();
        var success = true;
        foreach (var artifact in snapshot.Artifacts)
        {
            if (artifact.Content is not { } reference) { success = false; continue; }
            if (all.Any(other => other.Uid != snapshot.Uid && !retiredIds.Contains(other.Uid)
                && other.Artifacts.Any(item => item.Content is { } retained && retained.Key == reference.Key
                    && retained.Target.Storage.StorageUid == reference.Target.Storage.StorageUid && retained.Version == reference.Version
                    && retained.Document?.Publication.Publication == reference.Document?.Publication.Publication))) continue;
            var validationTarget = reference.Target with { Consumer = reference.Target.Consumer with { ConsumerScopeRef = Consumer() } };
            if (reference.Document is null) await content.ValidateAsync(validationTarget, ct);
            else await publications.ValidateAsync(validationTarget, ct);
            success &= await publications.RetireAsync(reference, ct);
        }
        var latest = await InWorkspaceAsync(() => resources.GetExactAsync<KnowledgeSnapshotObservedResource>(key, ct));
        if (latest is null || latest.Value.ActiveSnapshotUid == snapshot.Uid) return;
        var retired = latest.Value.RetiredSnapshots.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (success) retired[snapshot.Uid] = clock.GetUtcNow();
        _ = await InWorkspaceAsync(() => resources.PutExactAsync(scope, latest.Value with
        {
            Generation = checked(latest.Value.Generation + 1),
            RetiredSnapshots = retired,
            RetiringSnapshots = latest.Value.RetiringSnapshots.Where(id => id != snapshot.Uid).ToArray(),
            RetirementPendingSnapshots = success ? latest.Value.RetirementPendingSnapshots.Where(id => id != snapshot.Uid).ToArray()
                : latest.Value.RetirementPendingSnapshots.Append(snapshot.Uid).Distinct().ToArray()
        }, latest.ETag, false, ct));
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        IReadOnlyList<StoredResource<DataSourceAcquisitionResource>> acquisitions;
        IReadOnlyList<StoredResource<KnowledgeProjectionResource>> projections;
        using (scopes.PushSystem())
        {
            acquisitions = await resources.ListAllAsync<DataSourceAcquisitionResource>(DataSourceResourceKinds.DataSourceAcquisition, ct);
            projections = await resources.ListAllAsync<KnowledgeProjectionResource>(KnowledgeResourceKinds.KnowledgeProjection, ct);
        }
        foreach (var group in acquisitions.Where(value => value.Value.Dataset?.Definition.Lifecycle is not null
                     && value.Value.State == DataSourceAcquisitionState.Succeeded)
                     .GroupBy(value => (value.Value.WorkspaceId, value.Value.DataSourceUid)).Take(32))
        {
            var item = group.OrderByDescending(value => value.Value.DataSourceGeneration).ThenByDescending(value => value.Value.CompletedAt).First().Value;
            if (item.CreatedBy == Guid.Empty || item.WorkspaceId == Guid.Empty || item.TenantId == Guid.Empty) continue;
            using var actor = Enter(new(item.CreatedBy, item.TenantId, item.WorkspaceId), item.LogicalScopeRef);
            await MaintainAsync(item, ct);
        }
        foreach (var group in projections.Where(value => value.Value.State == KnowledgeProjectionState.Succeeded)
                     .GroupBy(value => (value.Value.WorkspaceId, value.Value.KnowledgeSourceUid)).Take(32))
        {
            var item = group.OrderByDescending(value => value.Value.CreatedAt).First().Value;
            if (item.CreatedBy == Guid.Empty || item.WorkspaceId == Guid.Empty || item.TenantId == Guid.Empty || item.KnowledgeSourceScopeRef is not { } owner) continue;
            StoredResource<KnowledgeSourceResource>? source;
            using (scopes.PushSystem()) source = await resources.GetExactAsync<KnowledgeSourceResource>(new(owner,
                item.KnowledgeSourceNamespace, KnowledgeResourceKinds.KnowledgeSource, item.KnowledgeSourceName), ct);
            if (source?.Value.Uid != item.KnowledgeSourceUid || source.Value.Definition.Lifecycle is null) continue;
            using var actor = Enter(new(item.CreatedBy, item.TenantId, item.WorkspaceId), item.LogicalScopeRef);
            await MaintainAsync(source.Value, ct);
        }
    }
    private IDisposable Enter(RequestContext actor, ResourceScopeRef? logical) => logical?.Kind switch
    { ResourceScopeKind.Instance => scopes.PushInstance(actor), ResourceScopeKind.Tenant => scopes.PushTenantAdministration(actor), _ => scopes.Push(actor) };

    public async Task EnsureAsync(FlowRunArtifactId artifactId, CancellationToken cancellationToken)
    {
        var id = artifactId.ToString();
        var acquisitions = await WorkspaceListAsync<DataSourceAcquisitionResource>(DataSourceResourceKinds.DataSourceAcquisition, cancellationToken);
        if (acquisitions.Any(value => (value.Value.ContentRetiringAt is not null || value.Value.ContentRetiredAt is not null)
            && value.Value.Manifest?.Artifacts.Any(artifact => artifact.ArtifactId == id) == true))
            throw new ArtifactValidationException("artifact_source_content_retired", "This source result no longer accepts new local copies.");
        var snapshots = await WorkspaceListAsync<KnowledgeSnapshotResource>(KnowledgeResourceKinds.KnowledgeSnapshot, cancellationToken);
        var related = snapshots.Where(value => value.Value.Artifacts.Any(artifact => artifact.ArtifactId == id)).Select(value => value.Value.Uid).ToArray();
        if (related.Length == 0) return;
        var states = await WorkspaceListAsync<KnowledgeSnapshotObservedResource>(KnowledgeResourceKinds.KnowledgeSnapshotObservedState, cancellationToken);
        var closed = states.SelectMany(value => value.Value.RetiringSnapshots.Concat(value.Value.RetiredSnapshots.Keys)).ToHashSet();
        if (related.All(closed.Contains))
            throw new ArtifactValidationException("artifact_source_content_retired", "This Snapshot no longer accepts new local copies.");
    }
    private async Task<bool> HasActiveFlowAsync(CancellationToken ct)
    {
        FlowRunKey? after = null;
        var scope = ResourceScopeRef.Workspace(Context().WorkspaceId);
        for (var pageNumber = 0; pageNumber < 10; pageNumber++)
        {
            var page = await flowRuns.ListRecoverableRunsAsync(after, 200, ct);
            if (page.Any(key => key.ScopeRef == scope)) return true;
            if (page.Count < 200) return false;
            after = page[^1];
        }
        return true; // An incomplete inventory must never authorize destructive cleanup.
    }

    private async Task EnsureUseAsync(Resource source, ResourceScopeRef consumer, CancellationToken ct)
    {
        if (!(await access.EvaluateAsync(new(source.Uid, source.ScopeRef!.Value, source.Namespace, source.Kind, source.Generation),
            consumer, ResourceAccessAction.Use, ct)).Allowed) throw new StorageException("storage_access_denied", "Content history is not authorized.", 403);
    }
    private async Task<IReadOnlyList<StoredResource<T>>> WorkspaceListAsync<T>(string kind, CancellationToken ct) where T : Resource
    {
        var scope = ResourceScopeRef.Workspace(Context().WorkspaceId);
        var result = new List<StoredResource<T>>();
        for (var skip = 0; skip < 10_000; skip += 1000)
        {
            var page = await InWorkspaceAsync(() => resources.ListExactAsync<T>(scope, kind, skip, 1000, ct));
            result.AddRange(page);
            if (page.Count < 1000) return result;
        }
        throw new StorageException("content_history_capacity", "The content history maintenance scan is bounded.");
    }
    private RequestContext Context() => current.Current;
    private ResourceScopeRef Consumer() => current.AccessMode switch
    {
        ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
        ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(Context().TenantId),
        ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(Context().WorkspaceId),
        _ => throw new InvalidOperationException("A governed history context is required.")
    };
    private async Task<T> InWorkspaceAsync<T>(Func<Task<T>> action)
    { using var workspace = scopes.Push(Context()); return await action(); }
    private static bool Expected(Exception exception) => exception is StorageException or ResourceConcurrencyException
        or AuthorizationDeniedException or ResourceScopeAccessDeniedException or DataSourceAcquisitionException;
}
