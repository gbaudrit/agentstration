using Agentstration.Knowledge.Contracts;

namespace Agentstration.Knowledge;

/// <summary>Publication-owned attribution; business query arguments cannot create membership.</summary>
public static class KnowledgeContributions
{
    public static KnowledgeContributionSelection ForArtifact(Guid sourceUid,
        IReadOnlyList<KnowledgeProjectionInputEvidence> inputs, string artifactId)
    {
        var matches = inputs.Where(i => i.PreparedArtifacts.Any(a => a.ArtifactId == artifactId)).ToArray();
        return matches.Length == 1 ? new(matches[0].BindingName, matches[0].DataSourceUid) : new("derived", sourceUid);
    }

    public static KnowledgeContributionSelection? FromSnapshot(KnowledgeSnapshotResource snapshot, KnowledgeSnapshotArtifact artifact)
    {
        if (artifact.Contribution is not null) return artifact.Contribution;
        var id = artifact.Provenance.GetValueOrDefault("sourceArtifactId") ?? artifact.ArtifactId;
        var matches = snapshot.ProjectionInputs.Where(i => i.PreparedArtifacts.Any(a => a.ArtifactId == id)).ToArray();
        return matches.Length == 1 ? new(matches[0].BindingName, matches[0].DataSourceUid) : null;
    }
}
