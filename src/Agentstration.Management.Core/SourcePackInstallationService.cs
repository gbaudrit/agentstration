using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class SourcePackInstallationService(
    SourceManagementService sources,
    SourceChannelSnapshotService snapshots,
    SourceCatalogService catalogs,
    ISourceSnapshotContentReader contentReader,
    IPackArchiveReader archiveReader,
    PackManagementService packs)
{
    private const int MaximumArchiveBytes = 8 * 1024 * 1024;

    public async Task<PackInstallationPreview> PreviewAsync(
        SourcePackSelection selection,
        IReadOnlyList<PackBindingSelection> bindings,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(selection, cancellationToken);
        return await packs.PreviewAsync(resolved.Archive, bindings, cancellationToken);
    }

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(
        SourcePackSelection selection,
        bool replaceExisting,
        IReadOnlyList<PackBindingSelection> bindings,
        PackRemovalOptions removalOptions,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(selection, cancellationToken);
        return await packs.InstallAsync(
            resolved.Archive,
            replaceExisting,
            bindings,
            removalOptions,
            resolved.Provenance,
            cancellationToken);
    }

    private async Task<ResolvedPack> ResolveAsync(SourcePackSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var source = (await sources.GetExactAsync(
            selection.SourceScope, selection.Publisher, selection.SourceName, cancellationToken))?.Source
            ?? throw new ControlPlaneResourceNotFoundException(
                new(ResourceKinds.Source, selection.SourceName, new(selection.Publisher)));
        var version = await sources.GetVersionExactAsync(
            selection.SourceScope, selection.Publisher, selection.SourceName, selection.SourceVersionUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(
                new(ResourceKinds.SourceVersion, selection.SourceVersionUid.ToString("D")));
        var snapshot = await snapshots.GetAsync(
            selection.SourceScope, selection.Publisher, selection.SourceName, selection.SourceVersionUid,
            selection.Channel, selection.SnapshotUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(
                new(ResourceKinds.SourceChannelSnapshot, selection.SnapshotUid.ToString("D")));

        var discovered = await catalogs.BrowseAsync(
            selection.SourceScope, selection.Publisher, selection.SourceName, selection.SourceVersionUid,
            selection.Channel, selection.SnapshotUid, null, cancellationToken);
        var catalog = discovered.SingleOrDefault(value =>
            value.Provenance.CatalogKind == SourceCatalogKinds.Pack
            && string.Equals(value.Provenance.CatalogName, selection.CatalogName, StringComparison.Ordinal))
            ?? throw Invalid("source_pack_catalog_missing", $"Pinned snapshot has no Pack catalog named '{selection.CatalogName}'.");
        var entry = catalog.PackEntries.SingleOrDefault(value =>
            string.Equals(value.Name, selection.EntryName, StringComparison.Ordinal))
            ?? throw Invalid("source_pack_entry_missing", $"Pack catalog '{selection.CatalogName}' has no entry named '{selection.EntryName}'.");
        if (!string.Equals(entry.Path, selection.Path, StringComparison.Ordinal))
            throw Invalid("source_pack_selection_stale", "The selected Pack path no longer matches the pinned catalog entry.");

        await using var content = await contentReader.OpenAsync(snapshot.Definition.Artifact, cancellationToken);
        var bytes = await content.ReadBytesAsync(entry.Path, MaximumArchiveBytes, cancellationToken);
        await using var stream = new MemoryStream(bytes, writable: false);
        var archive = await archiveReader.ReadAsync(stream, entry.Path, cancellationToken);
        if (!string.Equals(archive.Manifest.Metadata.Name, entry.Name, StringComparison.Ordinal))
            throw Invalid("source_pack_identity_mismatch", $"Catalog entry '{entry.Name}' contains Pack '{archive.Manifest.Metadata.Name}'.");
        var declaredPublisher = version.Definition.PublishedDefinition.Publisher.Name;
        if (!string.Equals(archive.Manifest.Metadata.Publisher, declaredPublisher, StringComparison.Ordinal))
            throw Invalid("source_pack_publisher_mismatch", $"Pack publisher '{archive.Manifest.Metadata.Publisher}' does not match Source publisher '{declaredPublisher}' and is not rewritten.");

        return new(archive, new SourcePackProvenance
        {
            SourceUid = source.Uid,
            SourceName = source.Name,
            SourcePublisher = source.Definition.Publisher,
            SourceVersionUid = version.Uid,
            SourceVersion = version.Definition.Version,
            SourceVersionDigest = version.Definition.ManifestDigest,
            Channel = selection.Channel,
            ProviderRevision = snapshot.Definition.ResolvedRevision,
            SnapshotUid = snapshot.Uid,
            SnapshotDigest = snapshot.Definition.Artifact.Sha256,
            CatalogName = catalog.Provenance.CatalogName,
            CatalogPath = catalog.Provenance.CatalogPath,
            EntryName = entry.Name,
            Path = entry.Path
        });
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    private sealed record ResolvedPack(PackArchive Archive, SourcePackProvenance Provenance);
}
