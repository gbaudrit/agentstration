using Agentstration.Resources;

namespace Agentstration.Work;

public sealed record WorkTaskExecutionEvidence(Guid EventId, long ObservedTaskVersion, string IntentDigest,
    string Summary, IReadOnlyList<WorkTaskResultId> ResultIds, IReadOnlyList<WorkTaskArtifactId> ArtifactIds,
    DateTimeOffset RecordedAt);

public sealed record WorkTaskExecutionReceipt(WorkspaceId WorkspaceId, Guid OwnerPrincipalId, WorkPlanId PlanId, WorkPlanAttempt Attempt);
public sealed record WorkTaskExecutionPage(IReadOnlyList<WorkTaskExecutionReceipt> Items, int Skip, int Take, int TotalCount)
{
    public bool HasMore => Skip + Items.Count < TotalCount;
}

public sealed record WorkTaskHistoryPage<T>(IReadOnlyList<T> Items, int Skip, int Take, int TotalCount)
{
    public bool HasMore => Skip + Items.Count < TotalCount;
}
