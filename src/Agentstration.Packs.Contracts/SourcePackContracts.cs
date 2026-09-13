using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Packs.Contracts;

public sealed record SourcePackSelection(
    ResourceScopeRef ScopeRef,
    string SourcePublisher,
    string SourceName,
    Guid SourceVersionUid,
    string Channel,
    Guid SnapshotUid,
    string CatalogName,
    string EntryName);

public sealed record SourcePackPreviewRequest(
    bool ReplaceExisting = false,
    IReadOnlyList<PackBindingSelection>? Bindings = null);

public sealed record SourcePackInstallRequest(
    string ExpectedPreviewDigest,
    bool ReplaceExisting = false,
    IReadOnlyList<PackBindingSelection>? Bindings = null);

public sealed record SourcePackInstallationPin(
    Guid SourceVersionUid,
    string SourceVersion,
    string ManifestDigest,
    string Channel,
    Guid SnapshotUid,
    string SnapshotDigest,
    string CatalogName,
    string CatalogPath,
    string EntryName,
    string EntryPath,
    string PackArchiveDigest,
    SourceRegistryImportProvenance? Registry = null);

public sealed record SourcePackInstallationPreview(
    PackInstallationPreview Pack,
    SourcePackInstallationPin Pin,
    string PreviewDigest);
