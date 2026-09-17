namespace Agentstration.ResourcePlanning.Contracts;

public enum ResourceChangeSetApplicationStatus { Applying, Applied, PartiallyApplied, Failed }
public enum ResourceChangeApplicationOutcome { Applied, AlreadyApplied, Skipped, Failed }

public sealed record ResourceChangeApplicationOperation(
    int Order,
    string LogicalId,
    ResourceChangeOperation Operation,
    ResourceChangeApplicationOutcome Outcome,
    Guid? ResourceUid,
    long? ResourceRevision,
    string? ResourceETag,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CompletedAt);

public sealed record ResourceChangeSetApplication(
    Guid Id,
    ResourcePlanId PlanId,
    long PlanRevision,
    ResourceChangeSetId ChangeSetId,
    string ChangeSetDigest,
    Guid ValidationId,
    ResourcePlanScope Scope,
    ResourceChangeSetApplicationStatus Status,
    IReadOnlyList<ResourceChangeApplicationOperation> Operations,
    int Attempts,
    Guid RequestedByPrincipalId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LeaseUntil,
    DateTimeOffset? CompletedAt);

public sealed record ResourceChangeSetApplicationSnapshot(ResourceChangeSetApplication Value, string ETag);

public sealed record ApplyResourceChangeSetRequest(ResourcePlanId PlanId, long PlanRevision, string ChangeSetDigest, Guid ValidationId);
