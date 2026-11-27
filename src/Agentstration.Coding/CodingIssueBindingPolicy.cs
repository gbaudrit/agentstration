using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public interface ICodingIssueBindingPolicy
{
    Task<Result<CodingIssueBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, WorkSharingIssueReference issue, CancellationToken cancellationToken);
    Result<CodingIssueObservation> Observe(ExternalTaskSnapshot snapshot, WorkSharingIssueReference issue, WorkParticipantId participant);
    ExternalTaskOperationInput Acknowledgment(WorkSharingIssueReference issue, WorkPlanId plan, WorkParticipantId participant, string revision);
}

public sealed record CodingIssueBinding(string Provider, WorkTaskProviderRoute Route);
public sealed record CodingIssueObservation(string Revision, bool AssociationValid, WorkPlanId? PlanId, bool Acknowledged,
    bool Open, bool Completed, string State, string? CompletionReason, IReadOnlyList<WorkTrackingItem> Dependencies);
