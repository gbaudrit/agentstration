namespace Agentstration.Work.Contracts;

public sealed record BeginWorkTaskOperationRequest(Guid Id, long ExpectedProjectVersion, string BindingName,
    ExternalTaskOperationInput Input, Guid? PlanId = null, long? PlanRevision = null, string? PlanDigest = null);
public sealed record WorkTaskOperationResponse(Guid Id, Guid ProjectId, long ProjectVersion, Guid? PlanId, long? PlanRevision,
    string? PlanDigest, ExternalTaskOperationInput Input, string IntentDigest, string OperationKey, WorkTaskProviderRoute Route,
    WorkTaskOperationStatus Status, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ExternalTaskSnapshot? Task, string? ErrorCode);
public sealed record WorkTaskOperationPageResponse(IReadOnlyList<WorkTaskOperationResponse> Items, bool HasMore, int Skip, int Take);
