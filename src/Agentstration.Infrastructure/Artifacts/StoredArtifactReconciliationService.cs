using Agentstration.Artifacts.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.Extensions.Logging;

namespace Agentstration.Infrastructure.Artifacts;

/// <summary>Registers metadata for existing produced content without re-executing its producer or rewriting history.</summary>
public sealed class StoredArtifactReconciliationService(IResourceStore resources, DataSourceDatasetAcquisitionService datasets,
    KnowledgeSnapshotService snapshots, IStoredArtifactRegistrar artifacts, IRequestContextScopeFactory scopes,
    ILogger<StoredArtifactReconciliationService> logger)
{
    private int cursor;
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        using var system = scopes.PushSystem();
        var acquisitions = await resources.ListAllAsync<DataSourceAcquisitionResource>(DataSourceResourceKinds.DataSourceAcquisition, cancellationToken);
        var publications = await resources.ListAllAsync<KnowledgeSnapshotResource>(KnowledgeResourceKinds.KnowledgeSnapshot, cancellationToken);
        var catalog = (await resources.ListAllAsync<FlowRunArtifactResource>(ArtifactResourceKinds.FlowRunArtifact, cancellationToken))
            .Select(item => (item.Value.WorkspaceId.Value, item.Value.ArtifactId)).ToHashSet();
        var work = new List<Func<Task>>();
        foreach (var item in acquisitions.Where(item => item.Value.Dataset is not null && item.Value.Content is not null
            && item.Value.Manifest is null && item.Value.State == DataSourceAcquisitionState.Succeeded))
            work.Add(async () =>
            {
                var value = item.Value;
                using var caller = Caller(new(value.CreatedBy, value.TenantId, value.WorkspaceId), value.LogicalScopeRef);
                _ = await datasets.ReconcileArtifactAsync(item, cancellationToken);
            });
        foreach (var item in publications)
        {
            var snapshot = item.Value;
            var workspaceId = new WorkspaceId(snapshot.ScopeRef!.Value.TargetId!.Value);
            var missing = snapshot.Artifacts.Where(entry => entry.Content is not null
                && !catalog.Contains((workspaceId.Value, ArtifactStorageReferences.Identity(workspaceId,
                    ArtifactStorageReferences.PublicationIdentity(entry.Content!))))).ToArray();
            if (missing.Length == 0) continue;
            foreach (var entry in missing)
                work.Add(async () =>
                {
                    using var readSystem = scopes.PushSystem();
                    var projection = (await resources.GetExactAsync<KnowledgeProjectionResource>(new(snapshot.ScopeRef.Value,
                        snapshot.Namespace, KnowledgeResourceKinds.KnowledgeProjection, snapshot.ProjectionId), cancellationToken))?.Value;
                    if (projection is null || projection.Uid != snapshot.ProjectionUid || projection.CreatedBy != snapshot.PublishedBy) return;
                    using var caller = Caller(new(projection.CreatedBy, projection.TenantId, projection.WorkspaceId), projection.LogicalScopeRef);
                    _ = await snapshots.ResolveForRetrievalAsync(new(snapshot.KnowledgeSourceName, snapshot.KnowledgeSourceNamespace,
                        snapshot.KnowledgeSourceScopeRef), snapshot.Name, cancellationToken);
                    var reference = entry.Content!;
                    using var workspace = scopes.Push(new(projection.CreatedBy, projection.TenantId, projection.WorkspaceId));
                    _ = await artifacts.RegisterStoredAsync(new(ArtifactStorageReferences.PublicationIdentity(reference),
                        snapshot.ProjectionFlowRunId, new(snapshot.KnowledgeSourceScopeRef ?? reference.Target.Consumer.ConsumerScopeRef,
                            snapshot.KnowledgeSourceNamespace, KnowledgeResourceKinds.KnowledgeSource, snapshot.KnowledgeSourceName),
                        snapshot.ProjectionId, entry.MediaType, new(workspaceId, reference.Target, reference.Key, reference.Version,
                            reference.Length, reference.Sha256, reference), ProducerUid: snapshot.KnowledgeSourceUid,
                        ProducerGeneration: snapshot.KnowledgeSourceGeneration), cancellationToken);
                });
        }
        if (work.Count == 0) { cursor = 0; return; }
        var start = cursor % work.Count;
        for (var count = 0; count < Math.Min(32, work.Count); count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await work[(start + count) % work.Count](); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogWarning("Artifact metadata reconciliation refused ({ErrorType}); original content and history were preserved.", exception.GetType().Name); }
        }
        cursor = (start + Math.Min(32, work.Count)) % work.Count;
    }

    private IDisposable Caller(RequestContext caller, ResourceScopeRef? scope) => scope?.Kind switch
    {
        ResourceScopeKind.Instance => scopes.PushInstance(caller),
        ResourceScopeKind.Tenant => scopes.PushTenantAdministration(caller),
        _ => scopes.Push(caller)
    };
}
