using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Sources.Contracts;

namespace Agentstration.Packs;

public sealed class PackSourceInstallationService(
    ISourceCatalogContentResolver sourceCatalogs,
    PackSourceCatalogHandler packCatalogs,
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
        IResolvedSourceCatalogContent resolved;
        try
        {
            resolved = await sourceCatalogs.ResolveAsync(
                new(
                    selection.ScopeRef,
                    selection.SourcePublisher,
                    selection.SourceName,
                    selection.SourceVersionUid,
                    selection.Channel,
                    selection.SnapshotUid,
                    PackCatalogKinds.Pack,
                    selection.CatalogName),
                cancellationToken);
        }
        catch (SourceValidationException exception) when (exception.Code == "source_catalog_missing")
        {
            throw Invalid("source_pack_catalog_missing", $"Pinned snapshot has no Pack catalog named '{selection.CatalogName}'.");
        }

        await using (resolved)
        {
            var catalog = packCatalogs.Read(resolved.Catalog);
            var entry = catalog.Definition.Entries.SingleOrDefault(value =>
                string.Equals(value.Name, selection.EntryName, StringComparison.Ordinal))
                ?? throw Invalid("source_pack_entry_missing", $"Pack catalog '{selection.CatalogName}' has no entry named '{selection.EntryName}'.");
            var entryPath = SourceDescendantPath.Combine(
                SourceDescendantPath.Parent(resolved.Provenance.CatalogPath),
                entry.Path,
                $"Pack catalog entry '{entry.Name}' path");
            if (!resolved.Paths.Contains(entryPath, StringComparer.Ordinal))
                throw Invalid("source_content_missing", $"Pack catalog entry '{entry.Name}' references missing snapshot content '{entryPath}'.");

            var bytes = await resolved.ReadBytesAsync(entryPath, MaximumArchiveBytes, cancellationToken);
            var archiveDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
            await using var stream = new MemoryStream(bytes, writable: false);
            var archive = await archiveReader.ReadAsync(stream, entryPath, cancellationToken);
            if (!string.Equals(archive.Manifest.Metadata.Name, entry.Name, StringComparison.Ordinal))
                throw Invalid("source_pack_identity_mismatch", $"Catalog entry '{entry.Name}' contains Pack '{archive.Manifest.Metadata.Name}'.");
            var declaredPublisher = resolved.Version.Definition.PublishedDefinition.Publisher.Name;
            if (!string.Equals(archive.Manifest.Metadata.Publisher, declaredPublisher, StringComparison.Ordinal))
                throw Invalid("source_pack_publisher_mismatch", $"Pack publisher '{archive.Manifest.Metadata.Publisher}' does not match Source publisher '{declaredPublisher}'. The Pack identity is not rewritten or presented as verified.");

            var pin = new SourcePackInstallationPin(
                resolved.Version.Uid,
                resolved.Version.Definition.Version,
                resolved.Version.Definition.ManifestDigest,
                selection.Channel,
                resolved.Snapshot.Uid,
                resolved.Snapshot.Definition.Artifact.Sha256,
                resolved.Provenance.CatalogName,
                resolved.Provenance.CatalogPath,
                entry.Name,
                entryPath,
                archiveDigest,
                resolved.Version.Definition.Origin?.Registry);
            var provenance = new SourcePackProvenance
            {
                SourceUid = resolved.Source.Uid,
                SourceName = resolved.Source.Name,
                SourcePublisher = resolved.Source.Definition.Publisher,
                SourceVersionUid = resolved.Version.Uid,
                SourceVersion = resolved.Version.Definition.Version,
                ManifestDigest = resolved.Version.Definition.ManifestDigest,
                Channel = selection.Channel,
                ProviderRevision = resolved.Snapshot.Definition.ResolvedRevision,
                SnapshotUid = resolved.Snapshot.Uid,
                SnapshotDigest = resolved.Snapshot.Definition.Artifact.Sha256,
                CatalogName = resolved.Provenance.CatalogName,
                CatalogPath = resolved.Provenance.CatalogPath,
                EntryName = entry.Name,
                EntryPath = entryPath,
                Registry = resolved.Version.Definition.Origin?.Registry
            };
            return new(archive, pin, provenance);
        }
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
