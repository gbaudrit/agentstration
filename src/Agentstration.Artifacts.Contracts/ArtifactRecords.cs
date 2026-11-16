using Agentstration.Schemas;

namespace Agentstration.Artifacts.Contracts;

public sealed record ArtifactRecordMember(string Id, int Ordinal, long Length, string Sha256);
public sealed record ArtifactRecordSetView(FlowRunArtifactId ArtifactId, ResolvedDataContract? RecordContract,
    IReadOnlyList<ArtifactRecordMember> Members);
public sealed record ArtifactRecordContent(string MemberId, long Offset, string ContentBase64, bool EndOfRecord);
