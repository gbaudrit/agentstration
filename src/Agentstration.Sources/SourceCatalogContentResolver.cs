using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Sources;

public sealed class SourceCatalogContentResolver(
    SourceManagementService sources,
    SourceChannelSnapshotService snapshots,
    SourceChannelCompatibilityEvaluator compatibility,
    ISourceSnapshotContentReader contentReader,
    ISourceCatalogManifestReader manifests,
    ResourceScopeOperationService scopeOperations) : ISourceCatalogContentResolver
{
    public async Task<IResolvedSourceCatalogContent> ResolveAsync(
        SourceCatalogContentSelection selection,
        CancellationToken cancellationToken) =>
        await scopeOperations.WriteAsync(
            SourceResourceKinds.SourceChannelSnapshot,
            selection.ScopeRef,
            AuthorizationPermissions.ResourcesRead,
            async token =>
            {
                var source = (await sources.GetExactAsync(
                    selection.ScopeRef, selection.SourcePublisher, selection.SourceName, token))?.Source
                    ?? throw new ResourceNotFoundException(new(
                        SourceResourceKinds.Source, selection.SourceName, new(selection.SourcePublisher)));
                var version = await sources.GetVersionExactAsync(
                    selection.ScopeRef, selection.SourcePublisher, selection.SourceName,
                    selection.SourceVersionUid, token)
                    ?? throw new ResourceNotFoundException(new(
                        SourceResourceKinds.SourceVersion, selection.SourceVersionUid.ToString("D")));
                var channelDefinition = version.Definition.PublishedDefinition.Channels.SingleOrDefault(value =>
                    string.Equals(value.Name, selection.Channel, StringComparison.Ordinal))
                    ?? throw Invalid("source_channel_missing", $"Source Version '{version.Definition.Version}' has no channel named '{selection.Channel}'.");
                compatibility.RequireCompatible(channelDefinition);
                var snapshot = await snapshots.GetAsync(
                    selection.ScopeRef, selection.SourcePublisher, selection.SourceName,
                    selection.SourceVersionUid, selection.Channel, selection.SnapshotUid, token)
                    ?? throw new ResourceNotFoundException(new(
                        SourceResourceKinds.SourceChannelSnapshot, selection.SnapshotUid.ToString("D")));

                var content = await contentReader.OpenAsync(snapshot.Definition.Artifact, token);
                try
                {
                    SourceCatalogDocument? selectedCatalog = null;
                    string? selectedPath = null;
                    foreach (var declaration in version.Definition.PublishedDefinition.Catalogs.Where(value =>
                                 string.Equals(value.Kind, selection.CatalogKind, StringComparison.Ordinal)))
                    {
                        var catalogPath = SourceDescendantPath.Normalize(
                            declaration.Path, $"Catalog '{declaration.Kind}' path");
                        var raw = await content.ReadTextAsync(
                            catalogPath, SourceCatalogLimits.MaximumManifestBytes, token);
                        var catalog = manifests.ReadCatalog(raw, declaration.Kind);
                        if (!string.Equals(catalog.Name, selection.CatalogName, StringComparison.Ordinal)) continue;
                        if (selectedCatalog is not null)
                            throw Invalid("source_catalog_name_duplicate", $"Catalog metadata.name '{catalog.Name}' is declared more than once in Source Version '{version.Definition.Version}'.");
                        selectedCatalog = catalog;
                        selectedPath = catalogPath;
                    }

                    if (selectedCatalog is null || selectedPath is null)
                        throw Invalid("source_catalog_missing", $"Source Version '{version.Definition.Version}' has no '{selection.CatalogKind}' catalog named '{selection.CatalogName}'.");

                    var provenance = new SourceCatalogProvenance(
                        source.Uid,
                        version.Uid,
                        version.Definition.Version,
                        selection.Channel,
                        snapshot.Uid,
                        snapshot.Definition.Artifact.Sha256,
                        selectedCatalog.Kind,
                        selectedCatalog.Name,
                        selectedPath);
                    return new ResolvedSourceCatalogContent(
                        source, version, snapshot, selectedCatalog, provenance, content);
                }
                catch
                {
                    await content.DisposeAsync();
                    throw;
                }
            },
            cancellationToken);

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    private sealed class ResolvedSourceCatalogContent(
        SourceResource source,
        SourceVersionResource version,
        SourceChannelSnapshotResource snapshot,
        SourceCatalogDocument catalog,
        SourceCatalogProvenance provenance,
        ISourceSnapshotContent content) : IResolvedSourceCatalogContent
    {
        public SourceResource Source { get; } = source;
        public SourceVersionResource Version { get; } = version;
        public SourceChannelSnapshotResource Snapshot { get; } = snapshot;
        public SourceCatalogDocument Catalog { get; } = catalog;
        public SourceCatalogProvenance Provenance { get; } = provenance;
        public IReadOnlyCollection<string> Paths => content.Paths;

        public Task<byte[]> ReadBytesAsync(
            string normalizedPath,
            int maximumBytes,
            CancellationToken cancellationToken) =>
            content.ReadBytesAsync(normalizedPath, maximumBytes, cancellationToken);

        public ValueTask DisposeAsync() => content.DisposeAsync();
    }
}
