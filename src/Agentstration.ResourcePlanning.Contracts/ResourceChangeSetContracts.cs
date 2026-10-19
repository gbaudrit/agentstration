namespace Agentstration.ResourcePlanning.Contracts;

public readonly record struct ResourceChangeSetId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum ResourceChangeOperation { Create, Update, Delete, NoOp }
public enum ResourceChangeSetStatus { Proposed, Validated, Applying, Applied, PartiallyApplied, Failed, Superseded }

public sealed record ResourceChange(
    int Order,
    string LogicalId,
    ResourceChangeOperation Operation,
    PlannedResourceDocument Proposed,
    CurrentResourceEvidence? Current,
    IReadOnlyList<string> DependsOn,
    string ProposedDigest);

public sealed record ResourceChangeSet(
    ResourceChangeSetId Id,
    ResourcePlanId PlanId,
    long PlanRevision,
    ResourcePlanScope Scope,
    string ContractVersion,
    string MaterializerVersion,
    string MaterializationDigest,
    string Digest,
    ResourceChangeSetStatus Status,
    IReadOnlyList<ResourceChange> Changes,
    IReadOnlyList<ResourcePlanMaterializationDiagnostic> Diagnostics,
    Guid CreatedByPrincipalId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ResourcePlanResolvedBinding>? ResolvedBindings = null);

public sealed record ResourceChangeSetSnapshot(ResourceChangeSet Value, string ETag);
public sealed record ResourceChangeSetPage(IReadOnlyList<ResourceChangeSetSnapshot> Items, bool HasMore);
