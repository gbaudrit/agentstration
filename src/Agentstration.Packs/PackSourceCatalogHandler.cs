using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;

namespace Agentstration.Packs;

public sealed class PackSourceCatalogHandler : ISourceCatalogHandler
{
    private const int MaximumEntries = 256;

    public string Kind => PackCatalogKinds.Pack;

    public Task<SourceCatalogView> BrowseAsync(
        SourceCatalogDocument catalog,
        ISourceSnapshotContent content,
        SourceCatalogProvenance provenance,
        string catalogPath,
        string? locale,
        CancellationToken cancellationToken)
    {
        var manifest = Read(catalog);
        var catalogDirectory = SourceDescendantPath.Parent(catalogPath);
        var entries = manifest.Definition.Entries.Select(entry =>
        {
            var path = SourceDescendantPath.Combine(
                catalogDirectory, entry.Path, $"Pack catalog entry '{entry.Name}' path");
            if (!content.Paths.Contains(path, StringComparer.Ordinal))
                throw Invalid("source_content_missing", $"Pack catalog entry '{entry.Name}' references missing snapshot content '{path}'.");
            return new SourceCatalogEntryView(entry.Name, entry.DisplayName, entry.Description, path);
        }).ToArray();

        return Task.FromResult(new SourceCatalogView(
            provenance,
            manifest.Definition.DisplayName,
            manifest.Definition.Description,
            [],
            entries));
    }

    public PackCatalogManifest Read(SourceCatalogDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!string.Equals(catalog.Kind, PackCatalogKinds.Pack, StringComparison.Ordinal))
            throw Invalid("source_catalog_kind_mismatch", $"Source catalog must use declared kind '{PackCatalogKinds.Pack}'.");

        try
        {
            var manifest = ResourceManifestSerializer.FromJsonStrict<PackCatalogManifest>(catalog.Value.GetRawText());
            if (!string.Equals(manifest.ApiVersion, ResourceApiVersions.CoreV1, StringComparison.Ordinal)
                || !string.Equals(manifest.Kind, PackCatalogKinds.Pack, StringComparison.Ordinal)
                || !string.Equals(manifest.Metadata.Name, catalog.Name, StringComparison.Ordinal))
                throw Invalid("source_catalog_invalid", $"Catalog must use '{ResourceApiVersions.CoreV1}', kind '{PackCatalogKinds.Pack}', and the inspected metadata.name.");
            if (manifest.Definition is null || string.IsNullOrWhiteSpace(manifest.Definition.DisplayName))
                throw Invalid("source_catalog_display_name_missing", "PackCatalog definition.displayName is required.");
            if (manifest.Definition.Entries.Count > MaximumEntries)
                throw Invalid("source_catalog_entry_limit", $"A Source catalog cannot contain more than {MaximumEntries} entries.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in manifest.Definition.Entries)
            {
                ValidateName(entry.Name);
                if (!names.Add(entry.Name))
                    throw Invalid("source_catalog_entry_duplicate", $"Pack catalog entry '{entry.Name}' is declared more than once.");
                if (string.IsNullOrWhiteSpace(entry.Path))
                    throw Invalid("source_catalog_path_invalid", $"Pack catalog entry '{entry.Name}' requires a path.");
            }
            return manifest;
        }
        catch (SourceValidationException) { throw; }
        catch (JsonException exception)
        {
            throw Invalid("source_catalog_invalid", $"Source catalog '{catalog.Kind}' is invalid: {exception.Message}");
        }
    }

    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')))
            throw Invalid("source_catalog_name_invalid", $"Pack catalog entry '{value}' is not a portable identifier.");
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);
}
