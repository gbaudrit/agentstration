using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.ResourcePlanning.Contracts;

public enum ResourcePlanMaterializationSeverity { Information, Warning, Error }
public enum ResourcePlanProposedOperation { Create, Update, NoOp }

public sealed record ResourcePlanAgentBinding(string LogicalId, ResourceReference ModelProfile, ResourceReference RuntimeProfile);

public sealed record ResourcePlanAgentBindingSelection(string LogicalId, ResourceReference? ModelProfile, ResourceReference? RuntimeProfile);
public sealed record ResourcePlanBindingDraft(ResourcePlanId PlanId, ResourcePlanScope Scope, long PlanRevision,
    IReadOnlyList<ResourcePlanAgentBindingSelection> Bindings, DateTimeOffset UpdatedAt);
public sealed record ResourcePlanBindingDraftSnapshot(ResourcePlanBindingDraft Value, string ETag);
public sealed record SaveResourcePlanBindingsRequest(long PlanRevision, IReadOnlyList<ResourcePlanAgentBindingSelection> Bindings);

public sealed record ResourcePlanMaterializationRequest(IReadOnlyList<ResourcePlanAgentBinding> Bindings, string? ExpectedDigest = null);

public sealed record ResourcePlanResolvedBinding(string LogicalId, string Field, ResourceReference Reference, Guid? Uid, long Revision, string? ETag, string Digest);

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
    string Digest,
    IReadOnlyList<ResourcePlanResolvedBinding>? ResolvedBindings = null)
{
    public bool CanCreateChangeSet => Diagnostics.All(value => value.Severity != ResourcePlanMaterializationSeverity.Error);
}
