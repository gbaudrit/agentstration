using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;

namespace Agentstration.Bootstrap.Contracts;

public sealed record BootstrapProfileSummary(
    string Name,
    string DisplayName,
    string? Description,
    BootstrapProfileScope Scope,
    int FileCount,
    int ResourceCount,
    string Digest,
    IReadOnlyList<BootstrapProfileBinding> Bindings,
    bool Valid = true,
    string? Error = null)
{
    public IReadOnlyList<BootstrapTopologyResource> Topology { get; init; } = [];
}

public sealed record BootstrapProfileBinding(
    string Name,
    BootstrapBindingTargetKind TargetKind,
    string DisplayName,
    string? Description,
    bool Required,
    ResourceReference? DefaultTarget = null,
    BootstrapContributionSelector? ExtensionTarget = null);

public sealed record BootstrapCatalogSnapshot(
    string? Path,
    bool InitialBootstrapEnabled,
    IReadOnlyList<string> InitialProfiles,
    IReadOnlyList<BootstrapProfileSummary> Profiles,
    string? Error = null);

public sealed record BootstrapProfileSelection(
    IReadOnlyList<string> Profiles,
    BootstrapApplicationTarget? Target = null,
    IReadOnlyList<BootstrapBindingSelection>? Bindings = null,
    BootstrapSourceProfileSelection? Source = null,
    IReadOnlyList<BootstrapProfileApplication>? Applications = null,
    IReadOnlyList<BootstrapInputSelection>? Inputs = null)
{
    public WorkspaceSeedSelection? Seed { get; init; }
}

public sealed record BootstrapResourcePreview(
    string Profile,
    string Location,
    string Kind,
    string Name,
    BootstrapResourceDisposition Disposition,
    string? Message = null,
    IReadOnlyList<BootstrapResourcePlanDetail>? Details = null)
{
    public string? TargetLabel { get; init; }
    public bool ValidationDeferred { get; init; }
}

public sealed record BootstrapCompositionPreview(
    IReadOnlyList<BootstrapProfileSummary> Profiles,
    BootstrapProfileScope Scope,
    BootstrapApplicationTarget? Target,
    IReadOnlyList<BootstrapBindingSelection> Bindings,
    string Digest,
    IReadOnlyList<BootstrapResourcePreview> Resources,
    BootstrapSourceProvenance? SourceProvenance = null)
{
    public IReadOnlyList<BootstrapConfigurationInput> Inputs { get; init; } = [];
    public IReadOnlyList<BootstrapProfileApplicationPreview> Applications { get; init; } = [];
    public WorkspaceSeedProvenance? SeedProvenance { get; init; }
    public IReadOnlyList<WorkspaceSeedPrerequisite> SeedPrerequisites { get; init; } = [];
    public bool CanApply => Inputs.All(input => input.Configured) && SeedPrerequisites.All(item => item.Available)
        && Resources.All(resource => resource.Disposition != BootstrapResourceDisposition.Invalid
            && (SeedProvenance is null || resource.Disposition != BootstrapResourceDisposition.Conflict));
}

public sealed record BootstrapConfigurationInput(string Profile, string Resource, string Configuration,
    string ValueType, bool Secured, bool Configured, ResourceScopeRef ScopeRef, ResourceNamespace Namespace)
{
    public bool AllowsParameter { get; init; }
    public ResourceScopeRef? ParameterCreationScope { get; init; }
}

public sealed record BootstrapExecutionResult(
    BootstrapCompositionPreview Preview,
    IReadOnlyList<BootstrapAppliedResource> Resources,
    string? Error = null);

public sealed record BootstrapTargetWorkspace(
    Guid TenantId,
    string TenantName,
    string TenantDisplayName,
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspaceDisplayName);

public sealed record BootstrapTargetTenant(Guid Id, string Name, string DisplayName);

public sealed record BootstrapBindingTargetOption(
    string Name,
    string Namespace,
    string DisplayName,
    bool Planned = false,
    ResourceScopeRef? ScopeRef = null,
    string? TargetProfile = null);

public sealed record BootstrapManagementView(
    BootstrapCatalogSnapshot Catalog,
    IReadOnlyList<BootstrapTargetTenant> Tenants,
    IReadOnlyList<BootstrapTargetWorkspace> Workspaces,
    IReadOnlyList<BootstrapApplicationResource> Applications);

public sealed record BootstrapProfilePreviewRequest(
    IReadOnlyList<string> Profiles,
    BootstrapApplicationTarget? Target = null,
    IReadOnlyList<BootstrapBindingSelection>? Bindings = null,
    BootstrapSourceProfileSelection? Source = null,
    IReadOnlyList<BootstrapProfileApplication>? Applications = null,
    IReadOnlyList<BootstrapInputSelection>? Inputs = null)
{
    public WorkspaceSeedSelection? Seed { get; init; }
}

public sealed record ApplyBootstrapProfilesRequest(
    IReadOnlyList<string> Profiles,
    string ExpectedDigest,
    BootstrapApplicationTarget? Target = null,
    IReadOnlyList<BootstrapBindingSelection>? Bindings = null,
    BootstrapSourceProfileSelection? Source = null,
    Guid? RequestId = null,
    IReadOnlyList<BootstrapProfileApplication>? Applications = null,
    IReadOnlyList<BootstrapInputSelection>? Inputs = null)
{
    public WorkspaceSeedSelection? Seed { get; init; }
}

public sealed record BootstrapExecutionProgress(
    BootstrapCompositionPreview Preview,
    IReadOnlyList<BootstrapAppliedResource> Resources,
    BootstrapResourcePreview? CurrentResource)
{
    public BootstrapOperationProgress? Operation { get; init; }
}

public sealed record BootstrapOperationProgress(string Stage, DateTimeOffset StartedAt, string? FlowRunId = null);

public sealed record BootstrapBindingTargetsRequest(
    BootstrapApplicationTarget? Target,
    BootstrapBindingTargetKind TargetKind,
    IReadOnlyList<string> Profiles,
    BootstrapSourceProfileSelection? Source = null,
    IReadOnlyList<BootstrapProfileApplication>? Applications = null,
    string? ConsumerProfile = null)
{
    public WorkspaceSeedSelection? Seed { get; init; }
}

public sealed record BootstrapTopologyResource(string Kind, string Name, string? TenantName = null);
public sealed record BootstrapPlannedTarget(string TenantName, string? WorkspaceName = null);
public sealed record BootstrapProfileApplication(string Profile, BootstrapApplicationTarget? Target = null,
    IReadOnlyList<BootstrapBindingSelection>? Bindings = null, BootstrapPlannedTarget? PlannedTarget = null);
public sealed record BootstrapProfileApplicationPreview(string Profile, BootstrapProfileScope Scope, string TargetLabel, bool ValidationDeferred, string? Digest = null)
{
    public BootstrapApplicationTarget? ResolvedTarget { get; init; }
}
