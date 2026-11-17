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
        var view = request.SnapshotId is null
            ? await snapshots.GetActiveAsync(Id(source), ct)
            : (await snapshots.ListAsync(Id(source), ct)).SingleOrDefault(v => v.Snapshot.Name == request.SnapshotId) ?? throw Error("knowledge_access_snapshot_not_found");
        if (agent && exposedTools is null) throw Error("knowledge_assignment_evidence_unavailable");
        var operations = exposure.Operations.Where(o => exposedTools is null || exposedTools.Any(t => t.Name == o.Tool.Name
            && t.ScopeRef == o.Tool.ScopeRef && (t.Namespace ?? source.Namespace) == (o.Tool.Namespace ?? source.Namespace))).ToArray();
        var available = exposure.Proposal.Tools.Where(t => t.Issue is null && t.Change != "removed"
            && (exposedTools is null || operations.Any(o => o.Route == t.Route))).ToArray();
        if (request.Input is { } requested && (string.IsNullOrWhiteSpace(requested) || requested.Length > 200))
            throw Error("knowledge_describe_input_invalid");
        if (request.Input is not null && !available.Any(t => t.Contribution?.BindingName == request.Input))
            throw Error("knowledge_describe_input_not_exposed");
        var detailed = request.Input is not null;
        available = available.Where(t => request.Input is null || t.Contribution?.BindingName == request.Input).ToArray();
        if (metadata is not null)
        {
            var presented = new List<KnowledgeToolProposal>();
            foreach (var tool in available) presented.Add(await metadata.PresentAsync(source, tool, ct));
            available = presented.ToArray();
        }
        var contributions = new List<KnowledgeContributionDescription>();
        foreach (var selection in available.Where(t => t.Contribution is not null)
            .Select(t => t.Contribution!).Distinct())
        {
            var input = view?.Snapshot.ProjectionInputs.SingleOrDefault(i => i.BindingName == selection.BindingName && i.DataSourceUid == selection.DataSourceUid);
            var artifacts = view?.Snapshot.Artifacts.Where(a => KnowledgeContributions.FromSnapshot(view.Snapshot, a) == selection).ToArray() ?? [];
            var candidate = available.First(t => t.Contribution == selection);
            var business = metadata is null ? new KnowledgeBusinessDescription(candidate.BusinessDescription, "published")
                : await metadata.DescriptionAsync(source, selection, candidate.BusinessDescription, ct);
            contributions.Add(new(selection, view is null ? "not_published" : artifacts.Length == 0 ? "no_matching_evidence" : "published",
                detailed ? artifacts.Select(a => a.ArtifactId).ToArray() : [],
                detailed ? artifacts.Where(a => a.DataContract?.Schema is not null).Select(a => a.DataContract!.Schema!.Value).DistinctBy(schema => schema.GetRawText()).ToArray() : [],
                detailed ? input?.AcquisitionId : null, detailed ? input?.AcquiredAt : null)
            {
                RecordSchemas = detailed ? artifacts.Where(a => a.RecordContract?.Schema is not null)
                    .Select(a => a.RecordContract!.Schema!.Value).DistinctBy(schema => schema.GetRawText()).ToArray() : [],
                Description = business.Description,
                DescriptionSource = business.Origin,
                MediaTypes = artifacts.Select(a => a.MediaType).Distinct(StringComparer.Ordinal).ToArray()
            });
        }
        var result = new KnowledgeStructureDescription(source.Definition.DisplayName, source.Definition.Description,
            exposure.Version, exposure.Proposal.Digest, view?.Snapshot.Name,
            !source.Definition.Enabled ? "disabled" : view is null ? "not_published" : view.LifecycleState.ToString().ToLowerInvariant(),
            view?.ContentAvailability ?? KnowledgeContentAvailability.Unknown,
            detailed ? available : [],
            operations.Where(o => request.Input is null || o.Proposal?.Contribution?.BindingName == request.Input)
                .Select(o => o with { Proposal = null }).ToArray(), contributions)
        {
            DetailLevel = detailed ? "input" : "overview",
            InvocationInstructions = KnowledgeToolMetadataService.ArgumentGuidance.Trim(),
            ToolSummaries = available.Select(t => new KnowledgeToolSummary(t.Route, t.Operation, t.DisplayName, null, t.Contribution)).ToArray()
        };
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > 196608)
            throw Error("knowledge_structure_limit");
        return result;
    }
}
