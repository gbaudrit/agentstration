namespace Agentstration.Bootstrap.Contracts;

public sealed record BootstrapStartupFamily(
    string StepId, string Kind, string State, int Total, int Processed,
    int Created, int Skipped, int Failed, int Conflicts, int Waiting);

public sealed record BootstrapStartupOperation(string Code, string State, DateTimeOffset ObservedAt);

public sealed record BootstrapStartupResource(
    string Id, string StepId, string Profile, string Location, string Kind, string Name,
    string Namespace, string TargetLevel, string State, IReadOnlyList<BootstrapStartupOperation> Operations)
{
    public BootstrapStartupModelSubstitution? Substitution { get; init; }
}

public sealed record BootstrapStartupResourceQuery(
    Guid AttemptId, string? StepId = null, string? Kind = null, string? State = null,
    string? Search = null, int Offset = 0, int Limit = 25);

public sealed record BootstrapStartupResourcePage(
    Guid AttemptId, long Revision, int Total, int Offset, int Limit,
    IReadOnlyList<BootstrapStartupResource> Items);
