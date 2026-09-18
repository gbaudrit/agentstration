namespace Agentstration.ResourcePlanning.Contracts;

public enum ResourceChangeSetValidationSeverity { Warning, Error }
public enum ResourceChangeSetReadiness { Ready, Blocked }

public sealed record ResourceChangeSetValidationIssue(
    string Code,
    string Path,
    string Message,
    ResourceChangeSetValidationSeverity Severity = ResourceChangeSetValidationSeverity.Error);

public sealed record ResourceChangeSetValidation(
    Guid Id,
    ResourceChangeSetId ChangeSetId,
    string ChangeSetDigest,
    ResourcePlanId PlanId,
    long PlanRevision,
    ResourcePlanScope Scope,
    ResourceChangeSetReadiness Readiness,
    IReadOnlyList<ResourceChangeSetValidationIssue> Issues,
    Guid ValidatedByPrincipalId,
    DateTimeOffset ValidatedAt);
