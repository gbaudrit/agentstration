using Agentstration.Resources;
using Agentstration.Sources.Contracts;

namespace Agentstration.Packs.Contracts;

public static class PackCatalogKinds
{
    public const string Pack = "PackCatalog";
}

public sealed record PackCatalogEntry
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Path { get; init; }
}

public sealed record PackCatalogProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<PackCatalogEntry> Entries { get; init; } = [];
}

public sealed record PackCatalogManifest
{
    public required string ApiVersion { get; init; }
    public required string Kind { get; init; }
    public ResourceMetadata Metadata { get; init; } = new();
    public required PackCatalogProperties Definition { get; init; }
}

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
