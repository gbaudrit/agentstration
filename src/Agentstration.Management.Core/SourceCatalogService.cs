using System.Globalization;
using Agentstration.Management.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceCatalogService(
    SourceManagementService sources,
    SourceChannelSnapshotService snapshots,
    SourceChannelCompatibilityEvaluator compatibility,
    ISourceSnapshotContentReader contentReader,
    ISourceCatalogManifestReader manifests,
    ResourceScopeOperationService scopeOperations)
{
    public async Task<IReadOnlyList<SourceCatalogView>> BrowseAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string sourceName,
        Guid versionUid,
        string channel,
        Guid snapshotUid,
        string? locale,
        CancellationToken cancellationToken) =>
        await scopeOperations.WriteAsync(ResourceKinds.SourceChannelSnapshot, scopeRef, AuthorizationPermissions.ResourcesRead, async token =>
        {
            var source = (await sources.GetExactAsync(scopeRef, publisher, sourceName, token))?.Source
                ?? throw new ResourceNotFoundException(new(ResourceKinds.Source, sourceName, new ResourceNamespace(publisher)));
            var version = await sources.GetVersionExactAsync(scopeRef, publisher, sourceName, versionUid, token)
                ?? throw new ResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")));
            var channelDefinition = version.Definition.PublishedDefinition.Channels.SingleOrDefault(value =>
                string.Equals(value.Name, channel, StringComparison.Ordinal))
                ?? throw Invalid("source_channel_missing", $"Source Version '{version.Definition.Version}' has no channel named '{channel}'.");
            compatibility.RequireCompatible(channelDefinition);
            var snapshot = await snapshots.GetAsync(scopeRef, publisher, sourceName, versionUid, channel, snapshotUid, token)
                ?? throw new ResourceNotFoundException(new(ResourceKinds.SourceChannelSnapshot, snapshotUid.ToString("D")));
            var requestedLocale = ValidateRequestedLocale(locale);
            await using var content = await contentReader.OpenAsync(snapshot.Definition.Artifact, token);
            var results = new List<SourceCatalogView>(version.Definition.PublishedDefinition.Catalogs.Count);
            var catalogNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in version.Definition.PublishedDefinition.Catalogs)
            {
                var catalogPath = SourceDescendantPath.Normalize(declaration.Path, $"Catalog '{declaration.Kind}' path");
                var raw = await content.ReadTextAsync(catalogPath, SourceCatalogLimits.MaximumManifestBytes, token);
                var parsed = manifests.ReadCatalog(raw, declaration.Kind);
                if (!catalogNames.Add(parsed.Name))
                    throw Invalid("source_catalog_name_duplicate", $"Catalog metadata.name '{parsed.Name}' is declared more than once in Source Version '{version.Definition.Version}'.");
                var provenance = new SourceCatalogProvenance(
                    source.Uid, version.Uid, version.Definition.Version, channel, snapshot.Uid,
                    snapshot.Definition.Artifact.Sha256, parsed.Kind, parsed.Name, catalogPath);
                results.Add(parsed.Bootstrap is { } bootstrap
                    ? await BootstrapAsync(content, provenance, bootstrap, catalogPath, requestedLocale, token)
                    : Pack(content, provenance, parsed.Pack!, catalogPath));
            }
            return results;
        }, cancellationToken);

    private async Task<SourceCatalogView> BootstrapAsync(
        ISourceSnapshotContent content,
        SourceCatalogProvenance provenance,
        BootstrapCatalogManifest catalog,
        string catalogPath,
        string? requestedLocale,
        CancellationToken cancellationToken)
    {
        var entries = new List<SourceBootstrapEntryView>(catalog.Definition.Entries.Count);
        var catalogDirectory = SourceDescendantPath.Parent(catalogPath);
        foreach (var entry in catalog.Definition.Entries)
        {
            SourceBootstrapProfileContract? invariant = null;
            var variants = new List<SourceBootstrapVariantView>(entry.Variants.Count);
            foreach (var variant in entry.Variants)
            {
                var variantPath = SourceDescendantPath.Normalize(variant.Path, $"Bootstrap variant '{entry.Name}/{variant.Locale}' path");
                var expected = $"profiles/{entry.Name}/{variant.Locale}";
                if (!string.Equals(variantPath, expected, StringComparison.Ordinal))
                    throw Invalid("source_bootstrap_variant_layout_invalid", $"Bootstrap variant '{entry.Name}/{variant.Locale}' must use path '{expected}'.");
                var resolvedPath = SourceDescendantPath.Combine(catalogDirectory, variantPath, $"Bootstrap variant '{entry.Name}/{variant.Locale}' path");
                var descriptorPath = SourceDescendantPath.Combine(resolvedPath, "profile.yaml", $"Bootstrap variant '{entry.Name}/{variant.Locale}' descriptor");
                var descriptor = manifests.ReadBootstrapProfile(await content.ReadTextAsync(
                    descriptorPath, SourceCatalogLimits.MaximumManifestBytes, cancellationToken));
                if (!string.Equals(descriptor.Name, entry.Name, StringComparison.Ordinal))
                    throw Invalid("source_bootstrap_profile_name_mismatch", $"Variant '{entry.Name}/{variant.Locale}' contains Bootstrap Profile '{descriptor.Name}'.");
                if (invariant is null) invariant = descriptor;
                else if (invariant.TargetScope != descriptor.TargetScope || !invariant.Bindings.SequenceEqual(descriptor.Bindings))
                    throw Invalid("source_bootstrap_variant_contract_mismatch", $"Bootstrap variants for '{entry.Name}' must use the same target scope and binding declarations.");
                variants.Add(new(variant.Locale, resolvedPath));
            }
            var resolvedLocale = requestedLocale is not null && variants.Any(value => value.Locale == requestedLocale)
                ? requestedLocale
                : entry.DefaultLocale;
            var resolved = variants.Single(value => value.Locale == resolvedLocale);
            entries.Add(new(entry.Name, entry.DefaultLocale, variants, resolved.Locale, resolved.Path));
        }
        return new(provenance, catalog.Definition.DisplayName, catalog.Definition.Description, entries, []);
    }

    private static SourceCatalogView Pack(
        ISourceSnapshotContent content,
        SourceCatalogProvenance provenance,
        PackCatalogManifest catalog,
        string catalogPath)
    {
        var catalogDirectory = SourceDescendantPath.Parent(catalogPath);
        var entries = catalog.Definition.Entries.Select(entry =>
        {
            var path = SourceDescendantPath.Combine(catalogDirectory, entry.Path, $"Pack catalog entry '{entry.Name}' path");
            if (!content.Paths.Contains(path, StringComparer.Ordinal))
                throw Invalid("source_content_missing", $"Pack catalog entry '{entry.Name}' references missing snapshot content '{path}'.");
            return new SourcePackEntryView(entry.Name, entry.DisplayName, entry.Description, path);
        }).ToArray();
        return new(provenance, catalog.Definition.DisplayName, catalog.Definition.Description, [], entries);
    }

    private static string? ValidateRequestedLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        if (locale == "neutral") return locale;
        string canonical;
        try
        {
            canonical = CultureInfo.GetCultureInfo(locale).Name;
        }
        catch (CultureNotFoundException)
        {
            throw Invalid("source_catalog_locale_invalid", $"Requested locale '{locale}' must be a canonical BCP 47 identifier or 'neutral'.");
        }
        if (canonical.Length == 0 || canonical != locale)
            throw Invalid("source_catalog_locale_invalid", $"Requested locale '{locale}' must be a canonical BCP 47 identifier or 'neutral'.");
        return locale;
    }

    private static SourceValidationException Invalid(string code, string message) => new(code, message);
}
