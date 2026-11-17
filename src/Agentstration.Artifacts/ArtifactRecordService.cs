using Agentstration.Artifacts.Contracts;
using Agentstration.Storage.Contracts;

namespace Agentstration.Artifacts;

/// <summary>Enumerates stored membership without I/O; content access uses the exact authorized Artifact receipt.</summary>
public sealed class ArtifactRecordService(ArtifactManagementService artifacts, IArtifactDurableStore durable,
    IArtifactMaterializationAdmission? admission = null)
{
    public async Task<ArtifactRecordSetView> GetAsync(FlowRunArtifactId id, CancellationToken ct)
    {
        var artifact = (await artifacts.GetFlowRunArtifactAsync(id, ct))?.Value
            ?? throw Error("flow_run_artifact_not_found");
        var location = artifact.Receipt.Materialization;
        if (location?.Publication?.Records is null) return new(id, null, []);
        var members = StorageRecordSets.Members(location.Publication);
        return new(id, location.RecordContract, members.Select((member, index) =>
            new ArtifactRecordMember(location.Publication.Records.Members[index].Id, index, member.Length, member.Sha256)).ToArray());
    }

    public async Task<ArtifactRecordContent> ReadAsync(FlowRunArtifactId id, string memberId, long offset, int length, CancellationToken ct)
    {
        var artifact = (await artifacts.GetFlowRunArtifactForContentReadAsync(id, true, ct))?.Value
            ?? throw Error("flow_run_artifact_not_found");
        if (admission is not null) await admission.EnsureAsync(id, ct);
        if (offset < 0 || length < 1 || memberId.Length is < 1 or > 80)
            throw Error("artifact_record_range_invalid");
        var location = artifact.Receipt.Materialization;
        if (location?.Publication?.Records is not { } records) throw Error("artifact_records_unavailable");
        var members = StorageRecordSets.Members(location.Publication);
        var index = records.Members.ToList().FindIndex(member => member.Id == memberId);
        if (index < 0) throw Error("artifact_record_outside");
        var member = members[index];
        if (offset > member.Length) throw Error("artifact_record_range_invalid");
        var reference = ArtifactStorageReferences.Encode(new(location.WorkspaceId, member.Target, member.Key,
            member.Version, member.Length, member.Sha256, member));
        var bytes = await durable.ReadAsync(artifact.WorkspaceId, reference, offset, length, ct);
        return new(memberId, offset, Convert.ToBase64String(bytes.Span), offset + bytes.Length == member.Length);
    }
    private static ArtifactValidationException Error(string code) => new(code, "The selected Artifact record is unavailable or invalid.");
}
