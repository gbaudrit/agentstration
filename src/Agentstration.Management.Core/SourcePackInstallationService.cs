using System.Security.Cryptography;
using System.Text.Json;
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

    public async Task<SourcePackInstallationPreview> PreviewAsync(
        SourcePackSelection selection,
        IReadOnlyList<PackBindingSelection> bindings,
        bool replaceExisting,
        PackRemovalOptions removalOptions,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(selection, cancellationToken);
        var preview = await packs.PreviewAsync(resolved.Archive, bindings, cancellationToken);
        return CreatePreview(resolved, preview, bindings, replaceExisting, removalOptions);
    }

    public async Task<StoredResource<InstalledPackResource>> InstallAsync(
        SourcePackSelection selection,
        string expectedPreviewDigest,
        bool replaceExisting,
        IReadOnlyList<PackBindingSelection> bindings,
        PackRemovalOptions removalOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedPreviewDigest))
            throw Invalid("source_pack_preview_digest_required", "Installing a Pack from a Source requires the exact preview digest.");

        var resolved = await ResolveAsync(selection, cancellationToken);
        var packPreview = await packs.PreviewAsync(resolved.Archive, bindings, cancellationToken);
        var current = CreatePreview(resolved, packPreview, bindings, replaceExisting, removalOptions);
        if (!string.Equals(current.PreviewDigest, expectedPreviewDigest, StringComparison.Ordinal))
            throw Invalid("source_pack_preview_stale", "The Source Pack selection, bindings, options, or target state changed. Preview the installation again.");

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
            selection.ScopeRef, selection.SourcePublisher, selection.SourceName, cancellationToken))?.Source
            ?? throw new ControlPlaneResourceNotFoundException(
                new(ResourceKinds.Source, selection.SourceName, new(selection.SourcePublisher)));
        var version = await sources.GetVersionExactAsync(
            selection.ScopeRef, selection.SourcePublisher, selection.SourceName, selection.SourceVersionUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(
                new(ResourceKinds.SourceVersion, selection.SourceVersionUid.ToString("D")));
        var snapshot = await snapshots.GetAsync(
            selection.ScopeRef, selection.SourcePublisher, selection.SourceName, selection.SourceVersionUid,
            selection.Channel, selection.SnapshotUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(
                new(ResourceKinds.SourceChannelSnapshot, selection.SnapshotUid.ToString("D")));

        // BrowseAsync re-resolves the exact Source Version and Channel, recalculates compatibility
        // against the running Agentstration version, and verifies Snapshot ownership.
        var discovered = await catalogs.BrowseAsync(
            selection.ScopeRef, selection.SourcePublisher, selection.SourceName, selection.SourceVersionUid,
            selection.Channel, selection.SnapshotUid, null, cancellationToken);
        var catalog = discovered.SingleOrDefault(value =>
            value.Provenance.CatalogKind == SourceCatalogKinds.Pack
            && string.Equals(value.Provenance.CatalogName, selection.CatalogName, StringComparison.Ordinal))
            ?? throw Invalid("source_pack_catalog_missing", $"Pinned snapshot has no Pack catalog named '{selection.CatalogName}'.");
        var entry = catalog.PackEntries.SingleOrDefault(value =>
            string.Equals(value.Name, selection.EntryName, StringComparison.Ordinal))
            ?? throw Invalid("source_pack_entry_missing", $"Pack catalog '{selection.CatalogName}' has no entry named '{selection.EntryName}'.");

        await using var content = await contentReader.OpenAsync(snapshot.Definition.Artifact, cancellationToken);
        var bytes = await content.ReadBytesAsync(entry.Path, MaximumArchiveBytes, cancellationToken);
        var archiveDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        await using var stream = new MemoryStream(bytes, writable: false);
        var archive = await archiveReader.ReadAsync(stream, entry.Path, cancellationToken);
        if (!string.Equals(archive.Manifest.Metadata.Name, entry.Name, StringComparison.Ordinal))
            throw Invalid("source_pack_identity_mismatch", $"Catalog entry '{entry.Name}' contains Pack '{archive.Manifest.Metadata.Name}'.");
        var declaredPublisher = version.Definition.PublishedDefinition.Publisher.Name;
        if (!string.Equals(archive.Manifest.Metadata.Publisher, declaredPublisher, StringComparison.Ordinal))
            throw Invalid("source_pack_publisher_mismatch", $"Pack publisher '{archive.Manifest.Metadata.Publisher}' does not match Source publisher '{declaredPublisher}'. The Pack identity is not rewritten or presented as verified.");

        var pin = new SourcePackInstallationPin(
            version.Uid,
            version.Definition.Version,
            version.Definition.ManifestDigest,
            selection.Channel,
            snapshot.Uid,
            snapshot.Definition.Artifact.Sha256,
            catalog.Provenance.CatalogName,
            catalog.Provenance.CatalogPath,
            entry.Name,
            entry.Path,
            archiveDigest);
        var provenance = new SourcePackProvenance
        {
            SourceUid = source.Uid,
            SourceName = source.Name,
            SourcePublisher = source.Definition.Publisher,
            SourceVersionUid = version.Uid,
            SourceVersion = version.Definition.Version,
            ManifestDigest = version.Definition.ManifestDigest,
            Channel = selection.Channel,
            ProviderRevision = snapshot.Definition.ResolvedRevision,
            SnapshotUid = snapshot.Uid,
            SnapshotDigest = snapshot.Definition.Artifact.Sha256,
            CatalogName = catalog.Provenance.CatalogName,
            CatalogPath = catalog.Provenance.CatalogPath,
            EntryName = entry.Name,
            EntryPath = entry.Path
        };
        return new(archive, pin, provenance);
    }

    private static SourcePackInstallationPreview CreatePreview(
        ResolvedPack resolved,
        PackInstallationPreview preview,
        IReadOnlyList<PackBindingSelection> requestedBindings,
        bool replaceExisting,
        PackRemovalOptions removalOptions) =>
        new(preview, resolved.Pin, ComputePreviewDigest(
            resolved.Pin, preview, requestedBindings, replaceExisting, removalOptions));

    private static string ComputePreviewDigest(
        SourcePackInstallationPin pin,
        PackInstallationPreview preview,
        IReadOnlyList<PackBindingSelection> requestedBindings,
        bool replaceExisting,
        PackRemovalOptions removalOptions)
    {
        var payload = new
        {
            Pin = pin,
            Pack = new
            {
                preview.Metadata.Publisher,
                preview.Metadata.Name,
                preview.Metadata.Version,
                Namespace = preview.Namespace.Value,
                TargetScope = preview.TargetScope.ToString(),
                preview.AlreadyInstalled,
                Resources = preview.Resources
                    .OrderBy(value => value.Kind, StringComparer.Ordinal)
                    .ThenBy(value => value.Name, StringComparer.Ordinal)
                    .ThenBy(value => value.Path, StringComparer.Ordinal)
                    .Select(value => new { value.Path, value.Kind, value.Name, value.AlreadyExists, Change = value.Change.ToString() }),
                Bindings = preview.Bindings
                    .OrderBy(value => value.Name, StringComparer.Ordinal)
                    .Select(value => new
                    {
                        value.Name,
                        TargetKind = value.TargetKind.ToString(),
                        value.Required,
                        value.TargetAvailable,
                        TargetName = value.Target?.Name,
                        TargetNamespace = value.Target?.Namespace?.Value,
                        TargetScope = value.Target?.ScopeRef?.Value.ToString()
                    })
            },
            RequestedBindings = requestedBindings
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .Select(value => new
                {
                    value.Name,
                    TargetName = value.Target.Name,
                    TargetNamespace = value.Target.Namespace?.Value,
                    TargetScope = value.Target.ScopeRef?.Value.ToString()
                }),
            replaceExisting,
            removalOptions.RemoveDashboardReferences,
            removalOptions.CloseInteractions
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    private sealed record ResolvedPack(
        PackArchive Archive,
        SourcePackInstallationPin Pin,
        SourcePackProvenance Provenance);
}
