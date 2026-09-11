using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Contracts;

public sealed record SourceConsoleChannelView(
    SourceChannelDefinition Definition,
    SourceChannelStatusView Status,
    IReadOnlyList<SourceChannelSnapshotResource> Snapshots,
    SourceChannelSnapshotResource? CurrentSnapshot,
    SourceChannelSnapshotVerificationView? Verification,
    IReadOnlyList<SourceCatalogView> Catalogs,
    SourceConsoleCatalogFailure? CatalogFailure);

public sealed record SourceConsoleCatalogFailure(string Code, string Message);

public sealed record SourceConsoleListItem(SourceView Source, SourceVersionResource? LatestVersion);

public sealed record SourceConsoleProviderCandidate(
    string Key,
    string RegistrationName,
    ResourceNamespace RegistrationNamespace,
    ResourceScopeRef RegistrationScopeRef,
    string DisplayName,
    string ContributionId);

public sealed record SourceConsoleBindingSelection(
    string Name,
    string TargetKind,
    ResourceReference? Target,
    SourceConsoleProviderCandidate? Candidate);

public sealed record SourceConsoleDetailView(
    SourceView Source,
    IReadOnlyList<SourceVersionResource> Versions,
    SourceVersionResource SelectedVersion,
    SourceDefinitionVerificationView Verification,
    SourceBindingStatusView Bindings,
    IReadOnlyList<StoredResource<SourceProviderResource>> Providers,
    IReadOnlyList<SourceConsoleProviderCandidate> ProviderCandidates,
    IReadOnlyList<SourceConsoleChannelView> Channels);
