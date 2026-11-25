namespace Agentstration.Work.Contracts;

public sealed record RecordWorkTaskExecutionEvidenceRequest(Guid EventId, long ExpectedPlanVersion, long ExpectedTaskVersion,
    long AdmittedTaskVersion, string TaskDigest, string Summary, IReadOnlyList<Guid> ResultIds, IReadOnlyList<Guid> ArtifactIds);
