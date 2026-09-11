using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Contracts;

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
    string? Error = null);

public sealed record BootstrapProfileBinding(
    string Name,
    BootstrapBindingTargetKind TargetKind,
    string DisplayName,
    string? Description,
    bool Required,
    ResourceReference? DefaultTarget = null);

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
    BootstrapSourceProfileSelection? Source = null);

public sealed record BootstrapResourcePreview(
    string Profile,
    string Location,
    string Kind,
    string Name,
    BootstrapResourceDisposition Disposition,
    string? Message = null,
    IReadOnlyList<BootstrapResourcePlanDetail>? Details = null);

public sealed record BootstrapCompositionPreview(
    IReadOnlyList<BootstrapProfileSummary> Profiles,
    BootstrapProfileScope Scope,
    BootstrapApplicationTarget? Target,
    IReadOnlyList<BootstrapBindingSelection> Bindings,
    string Digest,
    IReadOnlyList<BootstrapResourcePreview> Resources,
    BootstrapSourceProvenance? SourceProvenance = null)
{
    public bool CanApply => Resources.All(resource => resource.Disposition != BootstrapResourceDisposition.Invalid);
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
    bool Planned = false);

public sealed record BootstrapManagementView(
    BootstrapCatalogSnapshot Catalog,
    IReadOnlyList<BootstrapTargetTenant> Tenants,
    IReadOnlyList<BootstrapTargetWorkspace> Workspaces,
    IReadOnlyList<BootstrapApplicationResource> Applications);

public sealed record BootstrapProfilePreviewRequest(
    IReadOnlyList<string> Profiles,
    BootstrapApplicationTarget? Target = null,
    IReadOnlyList<BootstrapBindingSelection>? Bindings = null,
    BootstrapSourceProfileSelection? Source = null);

public sealed record ApplyBootstrapProfilesRequest(
    IReadOnlyList<string> Profiles,
    string ExpectedDigest,
    BootstrapApplicationTarget? Target = null,
    IReadOnlyList<BootstrapBindingSelection>? Bindings = null,
    BootstrapSourceProfileSelection? Source = null);

public sealed record BootstrapBindingTargetsRequest(
    BootstrapApplicationTarget? Target,
    BootstrapBindingTargetKind TargetKind,
    IReadOnlyList<string> Profiles,
    BootstrapSourceProfileSelection? Source = null);
