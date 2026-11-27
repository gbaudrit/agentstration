using Agentstration.Work;

namespace Agentstration.Coding.Contracts;

public static class CodingIssueProtocol
{
    public const string MetadataKey = "agentstration.coding.issue-authority";
    public const string Version = "1.0";
}
public sealed record CodingIssueCapabilities(string Version, string Provider, bool NativeHierarchy, bool NativeDependencies,
    bool CompletionReason, bool ManagedLabels, bool RevisionObservation);
public sealed record AcknowledgeCodingIssueRequest(Guid Id, long ExpectedProjectVersion, Guid PlanId, string Repository,
    string IssueId, string Provider, string PlanLabelPrefix, string ParticipantLabelPrefix,
    long ExpectedSharedVersion, IReadOnlyList<WorkTaskAllocation> Allocation)
{
    public string TrackingConnection { get; init; } = "";
}
public enum CodingIssueIntakeStatus { Pending, Confirmed, Conflict }
public sealed record CodingIssueIntakeResponse(Guid Id, Guid ProjectId, Guid PlanId, CodingIssueIntakeStatus Status,
    Guid? PublicationOperationId, Guid? IssueOperationId, string? ErrorCode, string? IssueRevision, DateTimeOffset ObservedAt)
{
    public Guid? ConflictingPlanId { get; init; }
}
public sealed record CodingIssueWait(string Repository, string IssueId, Guid? PlanId, string Reason, string? State,
    string? CompletionReason, string? Revision);
public sealed record CodingIssueReadiness(Guid ProjectId, Guid PlanId, bool Eligible, string? Repository, string? IssueId,
    string? IssueRevision, IReadOnlyList<CodingIssueWait> Waits, DateTimeOffset ObservedAt)
{
    public IReadOnlyList<CodingIssuePrerequisite> Prerequisites { get; init; } = [];
    public string? ConnectionDigest { get; init; }
}
public sealed record CodingIssuePrerequisite(string Repository, string IssueId, Guid PlanId, string State,
    string? CompletionReason, string Revision, bool Satisfied);
