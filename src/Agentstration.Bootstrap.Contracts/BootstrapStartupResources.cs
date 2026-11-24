namespace Agentstration.Bootstrap.Contracts;

public sealed record BootstrapStartupFamily(
    string StepId, string Kind, string State, int Total, int Processed,
    int Created, int Skipped, int Failed, int Conflicts, int Waiting)
{
    public string? GroupId { get; init; }
    public string? Profile { get; init; }
}

public sealed record BootstrapStartupOperation(string Code, string State, DateTimeOffset ObservedAt);

public sealed record BootstrapStartupDependency(string ExtensionId, string ContributionKind, string ContributionId,
    string? Version, Guid? InstanceId, string ConsumerKind, string ConsumerName);

public sealed record BootstrapStartupResource(
    string Id, string StepId, string Profile, string Location, string Kind, string Name,
    string Namespace, string TargetLevel, string State, IReadOnlyList<BootstrapStartupOperation> Operations)
{
    public string? GroupId { get; init; }
    public BootstrapFailure? Failure { get; init; }
    public BootstrapStartupModelSubstitution? Substitution { get; init; }
    public BootstrapStartupDependency? Dependency { get; init; }
    public string? WaitReason { get; init; }
}

public sealed record BootstrapStartupResourceQuery(
    Guid AttemptId, string? StepId = null, string? Kind = null, string? State = null,
    string? Search = null, int Offset = 0, int Limit = 25, string? GroupId = null, string? FollowResourceId = null);

public sealed record BootstrapStartupResourcePage(
    Guid AttemptId, long Revision, int Total, int Offset, int Limit,
    IReadOnlyList<BootstrapStartupResource> Items);
