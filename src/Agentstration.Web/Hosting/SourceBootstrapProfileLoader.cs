using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;

namespace Agentstration.Web.Hosting;

internal sealed class LoadedBootstrapSelection(
    IReadOnlyList<LoadedBootstrapProfile> profiles,
    BootstrapSourceProvenance? sourceProvenance = null,
    string? temporaryRoot = null) : IAsyncDisposable
{
    public IReadOnlyList<LoadedBootstrapProfile> Profiles { get; } = profiles;
    public BootstrapSourceProvenance? SourceProvenance { get; } = sourceProvenance;

    public ValueTask DisposeAsync()
    {
        if (temporaryRoot is not null)
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class SourceBootstrapProfileLoader(
    BootstrapProfileCatalog bootstrapCatalog,
    SourceCatalogService sourceCatalogs,
    SourceManagementService sources,
    SourceChannelSnapshotService snapshots,
    ISourceSnapshotContentReader contentReader)
{
    private const int MaximumProfileBytes = 32 * 1024 * 1024;

    internal async Task<LoadedBootstrapSelection> LoadAsync(
        BootstrapSourceProfileSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ValidatePin(selection);

        try
        {
            var catalogs = await sourceCatalogs.BrowseAsync(
                selection.ScopeRef,
                selection.Publisher,
                selection.SourceName,
                selection.SourceVersionUid,
                selection.Channel,
                selection.SnapshotUid,
                selection.Locale,
                cancellationToken);
            var catalog = catalogs.SingleOrDefault(value =>
                value.Provenance.CatalogKind == SourceCatalogKinds.Bootstrap
                && string.Equals(value.Provenance.CatalogName, selection.CatalogName, StringComparison.Ordinal))
                ?? throw Invalid($"Pinned Bootstrap catalog '{selection.CatalogName}' was not found in the selected Source Version.");
            var entry = catalog.BootstrapEntries.SingleOrDefault(value =>
                string.Equals(value.Name, selection.EntryName, StringComparison.Ordinal))
                ?? throw Invalid($"Pinned Bootstrap entry '{selection.EntryName}' was not found in catalog '{selection.CatalogName}'.");
            if (!string.Equals(entry.ResolvedLocale, selection.Locale, StringComparison.Ordinal)
                || !string.Equals(entry.ResolvedPath, selection.Path, StringComparison.Ordinal))
                throw Invalid("The pinned Bootstrap locale or descendant path no longer matches the selected Source catalog entry. Select the entry again.");

            var source = (await sources.GetExactAsync(
                selection.ScopeRef, selection.Publisher, selection.SourceName, cancellationToken))?.Source
                ?? throw Invalid($"Pinned Source '{selection.Publisher}/{selection.SourceName}' was not found.");
            var version = await sources.GetVersionExactAsync(
                selection.ScopeRef, selection.Publisher, selection.SourceName, selection.SourceVersionUid, cancellationToken)
                ?? throw Invalid($"Pinned Source Version '{selection.SourceVersionUid}' was not found.");
            var snapshot = await snapshots.GetAsync(
                selection.ScopeRef, selection.Publisher, selection.SourceName, selection.SourceVersionUid,
                selection.Channel, selection.SnapshotUid, cancellationToken)
                ?? throw Invalid($"Pinned Channel Snapshot '{selection.SnapshotUid}' was not found.");

            ValidateIdentity(selection, source, version, snapshot, catalog.Provenance);
            var provenance = new BootstrapSourceProvenance(
                selection.ScopeRef,
                source.Uid,
                selection.Publisher,
                selection.SourceName,
                version.Uid,
                version.Definition.Version,
                version.Definition.ManifestDigest,
                selection.Channel,
                snapshot.Definition.ResolvedRevision,
                snapshot.Uid,
                snapshot.Definition.Artifact.Sha256,
                catalog.Provenance.CatalogKind,
                catalog.Provenance.CatalogName,
                catalog.Provenance.CatalogPath,
                entry.Name,
                entry.ResolvedLocale,
                entry.ResolvedPath);

            return await MaterializeAsync(snapshot.Definition.Artifact, entry, provenance, cancellationToken);
        }
        catch (ControlPlaneResourceNotFoundException exception)
        {
            throw new DeclarativeBootstrapException(
                $"Pinned Source identity '{selection.Publisher}/{selection.SourceName}' or one of its pinned descendants was not found; a publisher mismatch is not rewritten.",
                exception);
        }
        catch (SourceValidationException exception)
        {
            throw new DeclarativeBootstrapException($"Source selection is invalid ({exception.Code}): {exception.Message}", exception);
        }
    }

    private async Task<LoadedBootstrapSelection> MaterializeAsync(
        SourceSnapshotArtifactReference artifact,
        SourceBootstrapEntryView entry,
        BootstrapSourceProvenance provenance,
        CancellationToken cancellationToken)
    {
        BootstrapProfileCatalog.ValidateProfileName(entry.Name);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"agentstration-source-bootstrap-{Guid.NewGuid():N}");
        var profileRoot = Path.Combine(temporaryRoot, entry.Name);
        try
        {
            Directory.CreateDirectory(profileRoot);
            await using var content = await contentReader.OpenAsync(artifact, cancellationToken);
            var prefix = $"{entry.ResolvedPath}/";
            var paths = content.Paths
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
                throw Invalid($"Pinned Bootstrap variant '{entry.ResolvedPath}' contains no files.");

            long totalBytes = 0;
            foreach (var path in paths)
            {
                var relativePath = path[prefix.Length..];
                _ = SourceDescendantPath.Normalize(relativePath, $"Bootstrap variant file '{path}'");
                var remaining = MaximumProfileBytes - totalBytes;
                if (remaining <= 0)
                    throw Invalid($"Pinned Bootstrap variant '{entry.ResolvedPath}' exceeds {MaximumProfileBytes} bytes.");
                var bytes = await content.ReadBytesAsync(path, checked((int)remaining), cancellationToken);
                totalBytes = checked(totalBytes + bytes.Length);
                var destination = Path.GetFullPath(Path.Combine(profileRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(profileRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw Invalid($"Pinned Bootstrap variant file '{path}' escapes its profile directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
            }

            var loaded = await bootstrapCatalog.LoadFromRootAsync(temporaryRoot, entry.Name, cancellationToken);
            return new([loaded], provenance, temporaryRoot);
        }
        catch
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void ValidatePin(BootstrapSourceProfileSelection selection)
    {
        if (string.IsNullOrWhiteSpace(selection.Publisher)
            || string.IsNullOrWhiteSpace(selection.SourceName)
            || string.IsNullOrWhiteSpace(selection.Channel)
            || string.IsNullOrWhiteSpace(selection.CatalogName)
            || string.IsNullOrWhiteSpace(selection.EntryName)
            || string.IsNullOrWhiteSpace(selection.Locale)
            || string.IsNullOrWhiteSpace(selection.Path)
            || selection.SourceVersionUid == Guid.Empty
            || selection.SnapshotUid == Guid.Empty)
            throw Invalid("A Source Bootstrap selection requires the complete Source Version, Channel Snapshot, catalog, entry, locale, and path pin.");
        _ = SourceDescendantPath.Normalize(selection.Path, "Pinned Bootstrap variant path");
    }

    private static void ValidateIdentity(
        BootstrapSourceProfileSelection selection,
        SourceResource source,
        SourceVersionResource version,
        SourceChannelSnapshotResource snapshot,
        SourceCatalogProvenance catalog)
    {
        if (!string.Equals(source.Definition.Publisher, selection.Publisher, StringComparison.Ordinal)
            || !string.Equals(source.Name, selection.SourceName, StringComparison.Ordinal)
            || version.Definition.SourceUid != source.Uid
            || !string.Equals(version.Definition.Publisher, selection.Publisher, StringComparison.Ordinal)
            || !string.Equals(version.Definition.PublishedDefinition.Publisher.Name, selection.Publisher, StringComparison.Ordinal)
            || !string.Equals(version.Definition.SourceName, selection.SourceName, StringComparison.Ordinal))
            throw Invalid("The selected Source contains a publisher or source identity mismatch; the declared identity was not rewritten.");
        if (snapshot.Definition.SourceUid != source.Uid
            || snapshot.Definition.SourceVersionUid != version.Uid
            || !string.Equals(snapshot.Definition.SourceVersion, version.Definition.Version, StringComparison.Ordinal)
            || !string.Equals(snapshot.Definition.Channel, selection.Channel, StringComparison.Ordinal)
            || catalog.SourceUid != source.Uid
            || catalog.SourceVersionUid != version.Uid
            || catalog.SnapshotUid != snapshot.Uid
            || !string.Equals(catalog.SnapshotDigest, snapshot.Definition.Artifact.Sha256, StringComparison.Ordinal))
            throw Invalid("The selected Source catalog does not belong to the pinned Source Version and Channel Snapshot.");
    }

    private static DeclarativeBootstrapException Invalid(string message) => new(message);
}
