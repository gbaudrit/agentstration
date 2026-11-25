using Agentstration.Work;

namespace Agentstration.Work.Contracts;

public sealed record PublishWorkSharingRequest(Guid Id, long ExpectedProjectVersion, IReadOnlyList<WorkSharingChange> Changes);
public sealed record PullWorkSharingRequest(string? Revision = null);
public sealed record ImportWorkSharingPlanRequest(string ExpectedDigest);
public sealed record WorkSharingOperationResponse(Guid Id, Guid ProjectId, long ProjectVersion, WorkSharingOperationStatus Status,
    long Version, string IntentDigest, int Attempts, string? Revision, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record WorkSharingStateResponse(Guid ProjectId, long ProjectVersion, long Version, string? Revision,
    WorkSharingSpace Space, IReadOnlyList<WorkSharingRecord> Records, DateTimeOffset UpdatedAt);

public sealed record WorkSharingOperationPageResponse(IReadOnlyList<WorkSharingOperationResponse> Items, bool HasMore, int Skip, int Take);
