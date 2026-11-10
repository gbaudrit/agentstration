using Agentstration.Knowledge.Contracts;

namespace Agentstration.Knowledge;

public sealed partial class KnowledgeAccessService
{
    public async Task<KnowledgeAccessStatus> GetStatusAsync(KnowledgeSourceId id, KnowledgeStatusRequest request, CancellationToken ct)
    {
        var source = await SourceAsync(id, false, ct);
        var views = await snapshots.ListAsync(Id(source), ct);
        var active = await snapshots.GetActiveAsync(Id(source), ct);
        var latest = views.OrderByDescending(value => value.Snapshot.PublishedAt).ThenBy(value => value.Snapshot.Name, StringComparer.Ordinal).FirstOrDefault();
        if (request.SnapshotId is not null && !views.Any(value => value.Snapshot.Name == request.SnapshotId))
            throw Error("knowledge_access_snapshot_not_found");
        var history = (await projections.ListAsync(Id(source), ct)).OrderByDescending(value => value.CreatedAt).FirstOrDefault();
        var inputStatus = source.Definition.DataSources.Select(binding =>
        {
            var evidence = active?.Snapshot.ProjectionInputs.SingleOrDefault(value => value.BindingName == binding.Name);
            return new KnowledgeInputStatus(binding.Name, evidence?.AcquisitionId, evidence?.AcquiredAt,
                KnowledgeFreshness.IsFresh(binding, evidence?.AcquiredAt, clock),
                KnowledgeFreshness.RequiresRefresh(binding, evidence?.AcquiredAt, clock), binding.Required);
        }).ToArray();
        var unavailable = active?.LifecycleState == KnowledgeSnapshotLifecycleState.Unavailable;
        var required = unavailable || NeedsPreparation(source, active?.Snapshot);
        var state = !source.Definition.Enabled ? "disabled"
            : history?.State == KnowledgeProjectionState.Running ? "preparing"
            : unavailable ? "unavailable" : required ? "preparation_required" : "ready";
        return new()
        {
            SourceUid = source.Uid,
            State = state,
            CheckedAt = clock.GetUtcNow(),
            ActiveSnapshotId = active?.Snapshot.Name,
            ActivePublishedAt = active?.Snapshot.PublishedAt,
            LatestPublishedSnapshotId = latest?.Snapshot.Name,
            LatestPublishedAt = latest?.Snapshot.PublishedAt,
            ComparedSnapshotId = request.SnapshotId,
            IsActive = request.SnapshotId is null ? null : request.SnapshotId == active?.Snapshot.Name,
            IsLatestPublished = request.SnapshotId is null ? null : request.SnapshotId == latest?.Snapshot.Name,
            PreparationRequired = required,
            ProjectionId = history?.Name,
            ErrorCode = history?.State == KnowledgeProjectionState.Failed ? history.ErrorCode : null,
            Inputs = inputStatus,
            Documents = active?.Snapshot.Artifacts.Select(value => new KnowledgeAccessDocument(value.ArtifactId,
                value.MediaType, value.Length, IsText(value.MediaType))).ToArray() ?? []
        };
    }
}
