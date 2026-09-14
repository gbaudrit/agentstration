using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.ResourcePlanning.Contracts;

public enum ResourcePlanMaterializationSeverity { Information, Warning, Error }
public enum ResourcePlanProposedOperation { Create, Update, NoOp }

public sealed record PlannedResourceDocument(
    string ApiVersion,
    string Kind,
    ResourceScopeRef ScopeRef,
    ResourceMetadata Metadata,
    JsonElement Definition);

public sealed record CurrentResourceEvidence(
    Guid? Uid,
    long Revision,
    string? ETag,
    JsonElement Document,
    string Digest);

public sealed record MaterializedResourceProposal(
    string LogicalId,
    PlannedResourceDocument Resource,
    ResourcePlanProposedOperation Operation,
    CurrentResourceEvidence? Current,
    IReadOnlyList<string> DependsOn,
    string ProposedDigest);

public sealed record ResourcePlanMaterializationDiagnostic(
    string Code,
    string Path,
    string Message,
    ResourcePlanMaterializationSeverity Severity = ResourcePlanMaterializationSeverity.Error);

public sealed record ResourcePlanMaterialization(
    ResourcePlanId PlanId,
    long PlanRevision,
    string ContractVersion,
    string MaterializerVersion,
    ResourcePlanScope Scope,
    IReadOnlyList<MaterializedResourceProposal> Proposals,
    IReadOnlyList<ResourcePlanMaterializationDiagnostic> Diagnostics,
    string Digest)
{
    public bool CanCreateChangeSet => Diagnostics.All(value => value.Severity != ResourcePlanMaterializationSeverity.Error);
}
