using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;

namespace Agentstration.Knowledge;

public sealed partial class KnowledgeAccessService
{
    private static KnowledgeSnapshotResource SelectContribution(KnowledgeSnapshotResource snapshot, KnowledgeContributionSelection? selection)
    {
        if (selection is null) return snapshot;
        if (selection.BindingName is not { Length: > 0 and <= 200 } || selection.DataSourceUid == Guid.Empty)
            throw Error("knowledge_contribution_invalid");
        var input = snapshot.ProjectionInputs.SingleOrDefault(i => i.BindingName == selection.BindingName && i.DataSourceUid == selection.DataSourceUid);
        if (input is null && !(selection.BindingName == "derived" && selection.DataSourceUid == snapshot.KnowledgeSourceUid))
            throw Error("knowledge_contribution_outside_snapshot");
        if (snapshot.Artifacts.Any(a => KnowledgeContributions.FromSnapshot(snapshot, a) is null))
            throw Error("knowledge_contribution_evidence_unavailable");
        var artifacts = snapshot.Artifacts.Where(a => KnowledgeContributions.FromSnapshot(snapshot, a) == selection).ToArray();
        return snapshot with { Artifacts = artifacts };
    }

    private static KnowledgeSnapshotResource SelectReadableContribution(KnowledgeSnapshotResource snapshot,
        KnowledgeContributionSelection? selection, bool singleDocument)
    {
        var selected = SelectContribution(snapshot, selection);
        return selection is not null && !singleDocument
            ? selected with { Artifacts = selected.Artifacts.Where(a => IsText(a.MediaType)).ToArray() } : selected;
    }

    private static void ContinueContribution(AccessToken token, KnowledgeContributionSelection? selection)
    {
        if (token.Contribution != selection) throw Error("knowledge_access_cursor_contribution_mismatch");
    }

    public async Task<KnowledgeStructureDescription> DescribeAsync(KnowledgeSourceId id, KnowledgeDescribeRequest request, CancellationToken ct, IReadOnlyCollection<ResourceReference>? exposedTools = null, bool agent = false)
    {
        var source = await SourceAsync(id, false, ct);
        var address = new ScopedResourceAddress(request.ExposureScope is null ? Consumer() : ResourceScopeRef.Parse(request.ExposureScope), source.Namespace, KnowledgeResourceKinds.KnowledgeToolExposureVersion, request.Exposure);
        var exposure = (await resources.GetExactAsync<KnowledgeToolExposureVersion>(address, ct))?.Value;
        if (exposure is null || exposure.SourceUid != source.Uid) throw Error("knowledge_exposure_not_found");
        // Retained metadata only. No preparation, lease, provider inspection or content read.
        var views = await snapshots.ListAsync(Id(source), ct);
        var view = request.SnapshotId is null
            ? await snapshots.GetActiveAsync(Id(source), ct)
            : views.SingleOrDefault(v => v.Snapshot.Name == request.SnapshotId) ?? throw Error("knowledge_access_snapshot_not_found");
        if (agent && exposedTools is null) throw Error("knowledge_assignment_evidence_unavailable");
        var operations = exposure.Operations.Where(o => exposedTools is null || exposedTools.Any(t => t.Name == o.Tool.Name
            && t.ScopeRef == o.Tool.ScopeRef && (t.Namespace ?? source.Namespace) == (o.Tool.Namespace ?? source.Namespace))).ToArray();
        var available = exposure.Proposal.Tools.Where(t => t.Issue is null && t.Change != "removed"
            && (exposedTools is null || operations.Any(o => o.Route == t.Route))).ToArray();
        var contributions = new List<KnowledgeContributionDescription>();
        foreach (var selection in available.Where(t => t.Contribution is not null)
            .Select(t => t.Contribution!).Distinct())
        {
            var input = view?.Snapshot.ProjectionInputs.SingleOrDefault(i => i.BindingName == selection.BindingName && i.DataSourceUid == selection.DataSourceUid);
            var artifacts = view?.Snapshot.Artifacts.Where(a => KnowledgeContributions.FromSnapshot(view.Snapshot, a) == selection).ToArray() ?? [];
            contributions.Add(new(selection, view is null ? "not_published" : artifacts.Length == 0 ? "no_matching_evidence" : "published",
                artifacts.Select(a => a.ArtifactId).ToArray(), artifacts.Where(a => a.DataContract?.Schema is not null).Select(a => a.DataContract!.Schema!.Value).DistinctBy(schema => schema.GetRawText()).ToArray(),
                input?.AcquisitionId, input?.AcquiredAt)
            { MediaTypes = artifacts.Select(a => a.MediaType).Distinct(StringComparer.Ordinal).ToArray() });
        }
        var result = new KnowledgeStructureDescription(exposure.DisplayName, exposure.Description, exposure.Version, exposure.Proposal.Digest, view?.Snapshot.Name,
            !source.Definition.Enabled ? "disabled" : view is null ? "not_published" : view.LifecycleState.ToString().ToLowerInvariant(),
            view?.ContentAvailability ?? KnowledgeContentAvailability.Unknown,
            available, operations, contributions);
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > 196608)
            throw Error("knowledge_structure_limit");
        return result;
    }
}
