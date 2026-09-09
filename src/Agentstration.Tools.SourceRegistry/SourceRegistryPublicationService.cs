using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Tools.SourceRegistry;

public enum SourceRegistryPublicationKind { Shard, Index }

public sealed record SourceRegistryPublicationValidation(
    SourceRegistryPublicationKind Kind,
    ParsedSourceRegistry? Registry,
    ParsedSourceRegistryIndex? Index,
    IReadOnlyList<SourceRegistryPublicationShard> Shards,
    IReadOnlyList<SourceRegistryPublicationFile> Files,
    int SourceCount,
    int VersionCount);

public sealed record SourceRegistryPublicationShard(
    string Name,
    string RelativePath,
    string FullPath,
    SourceCompatibilityBounds? Compatibility,
    ParsedSourceRegistry Registry);

public sealed record SourceRegistryPublicationFile(
    string RelativePath,
    string FullPath,
    string Publisher,
    string Source,
    string Version,
    string Digest,
    SourceCompatibilityBounds? ShardCompatibility);

public sealed class SourceRegistryPublicationService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SourceRegistryReader registryReader = new();
    private readonly SourceRegistryIndexReader indexReader = new();
    private readonly SourceManifestReader sourceReader = new();

    public async Task<SourceRegistryPublicationValidation> ValidateAsync(string registryOrIndexPath, string publicationRoot, Uri baseUri, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(publicationRoot);
        var entry = Path.GetFullPath(registryOrIndexPath);
        var kind = ValidateEntryLocation(entry, root);
        RejectReparsePoint(new DirectoryInfo(root), "Publication root");
        RejectReparsePoint(new FileInfo(entry), "Registry publication entry document");
        SourceRegistryReferenceResolver.ValidateBaseUri(baseUri);
        return kind == SourceRegistryPublicationKind.Index
            ? await ValidateIndexAsync(entry, root, baseUri, cancellationToken)
            : await ValidateShardAsync(entry, root, baseUri, cancellationToken);
    }

    public async Task<SourceRegistryPublicationValidation> BuildAsync(string registryOrIndexPath, string publicationRoot, Uri baseUri, string outputRoot, CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(publicationRoot);
        var output = Path.GetFullPath(outputRoot);
        ValidateDistinctTrees(input, output);
        ValidateOutputRoot(output);
        var validation = await ValidateAsync(registryOrIndexPath, input, baseUri, cancellationToken);
        var parent = Path.GetDirectoryName(output) ?? throw Invalid("source_registry_output_invalid", "The output root must have a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".agentstration-source-registry-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            if (validation.Kind == SourceRegistryPublicationKind.Index)
            {
                await WriteCanonicalDocumentAsync(staging, "index.json", "index.sha256", validation.Index!.CanonicalJson, validation.Index.IndexDigest, cancellationToken);
                foreach (var shard in validation.Shards)
                    await WriteCanonicalDocumentAsync(staging, shard.RelativePath, DigestPath(shard.RelativePath), shard.Registry.CanonicalJson, shard.Registry.RegistryDigest, cancellationToken);
            }
            else
            {
                await WriteCanonicalDocumentAsync(staging, "registry.json", "registry.sha256", validation.Registry!.CanonicalJson, validation.Registry.RegistryDigest, cancellationToken);
            }

            foreach (var file in validation.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveDescendant(staging, file.RelativePath, "source_registry_output_path_invalid");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var sourceFile = await ReadUtf8Async(file.FullPath, SourceManifestReader.MaximumManifestBytes, "source_manifest_size_limit", cancellationToken);
                var parsed = ValidateSourceManifest(sourceFile.Text, file.RelativePath, file.Publisher, file.Source, file.Version, file.Digest);
                ValidateShardMembership(parsed, file.RelativePath, file.ShardCompatibility);
                await File.WriteAllBytesAsync(target, sourceFile.Bytes, cancellationToken);
            }
            if (Directory.Exists(output)) Directory.Delete(output, recursive: false);
            Directory.Move(staging, output);
            return validation;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<SourceRegistryPublicationValidation> ValidateIndexAsync(string indexPath, string root, Uri baseUri, CancellationToken cancellationToken)
    {
        var indexFile = await ReadUtf8Async(indexPath, SourceRegistryLimits.MaximumIndexDocumentBytes, "source_registry_index_size_limit", cancellationToken);
        var index = indexReader.Read(indexFile.Text, indexPath, baseUri);
        var occupied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["index.json"] = "index.json", ["index.sha256"] = "index.sha256" };
        var manifests = new Dictionary<string, SourceRegistryPublicationFile>(StringComparer.OrdinalIgnoreCase);
        var observations = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var shards = new List<SourceRegistryPublicationShard>();
        foreach (var catalog in index.Manifest.Definition.Catalogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = SourceRegistryReferenceResolver.ResolveRegistryPublicationPath(baseUri, catalog.RegistryUrl);
            ReservePath(occupied, relativePath, "source_registry_index_output_path_duplicate");
            ReservePath(occupied, DigestPath(relativePath), "source_registry_index_output_path_duplicate");
            var fullPath = ResolveDescendant(root, relativePath, "source_registry_index_registry_path_invalid");
            ValidateRegularDescendant(root, fullPath, relativePath, "shard", "source_registry_index_registry_missing", "source_registry_index_registry_not_regular");
            var shardFile = await ReadUtf8Async(fullPath, SourceRegistryLimits.MaximumDocumentBytes, "source_registry_size_limit", cancellationToken);
            var shard = registryReader.Read(shardFile.Text, fullPath);
            if (shard.RegistryDigest != catalog.RegistryDigest)
                throw Invalid("source_registry_index_digest_mismatch", $"Shard '{relativePath}' digest does not match catalog '{catalog.Name}'.");
            var bounds = catalog.Compatibility.Agentstration!;
            shards.Add(new(catalog.Name, relativePath, fullPath, bounds, shard));
            await ValidateShardContentsAsync(shard, bounds, root, baseUri, occupied, manifests, observations, sources, cancellationToken);
        }
        return new(SourceRegistryPublicationKind.Index, null, index, shards, manifests.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray(), sources.Count, observations.Count);
    }

    private async Task<SourceRegistryPublicationValidation> ValidateShardAsync(string registryPath, string root, Uri baseUri, CancellationToken cancellationToken)
    {
        var file = await ReadUtf8Async(registryPath, SourceRegistryLimits.MaximumDocumentBytes, "source_registry_size_limit", cancellationToken);
        var registry = registryReader.Read(file.Text, registryPath);
        var occupied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["registry.json"] = "registry.json", ["registry.sha256"] = "registry.sha256" };
        var manifests = new Dictionary<string, SourceRegistryPublicationFile>(StringComparer.OrdinalIgnoreCase);
        var observations = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        await ValidateShardContentsAsync(registry, null, root, baseUri, occupied, manifests, observations, sources, cancellationToken);
        return new(SourceRegistryPublicationKind.Shard, registry, null, [], manifests.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray(), sources.Count, observations.Count);
    }

    private async Task ValidateShardContentsAsync(ParsedSourceRegistry shard, SourceCompatibilityBounds? bounds, string root, Uri baseUri,
        Dictionary<string, string> occupied, Dictionary<string, SourceRegistryPublicationFile> manifests,
        Dictionary<string, string> observations, HashSet<string> sources, CancellationToken cancellationToken)
    {
        foreach (var source in shard.Manifest.Definition.Sources)
        {
            var sourceIdentity = $"{source.Publisher}\0{source.Name}";
            sources.Add(sourceIdentity);
            foreach (var version in source.Versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = $"{sourceIdentity}\0{version.Version}";
                if (observations.TryGetValue(identity, out var digest) && digest != version.ManifestDigest)
                    throw Invalid("source_registry_cross_shard_digest_conflict", $"Source '{source.Publisher}/{source.Name}@{version.Version}' has conflicting digests across shards.");
                observations[identity] = version.ManifestDigest;
                var relativePath = SourceRegistryReferenceResolver.ResolveManifestPublicationPath(baseUri, version.ManifestUrl);
                if (manifests.TryGetValue(relativePath, out var existing))
                {
                    if (existing.RelativePath != relativePath)
                        throw Invalid("source_registry_output_path_duplicate", $"Publication paths '{existing.RelativePath}' and '{relativePath}' collide by case.");
                    if (existing.Publisher != source.Publisher || existing.Source != source.Name || existing.Version != version.Version || existing.Digest != version.ManifestDigest)
                        throw Invalid("source_registry_output_path_conflict", $"Publication path '{relativePath}' is referenced with conflicting Source Version metadata.");
                    if (bounds is not null)
                    {
                        var shared = await ReadUtf8Async(existing.FullPath, SourceManifestReader.MaximumManifestBytes, "source_manifest_size_limit", cancellationToken);
                        ValidateShardMembership(ValidateSourceManifest(shared.Text, relativePath, source.Publisher, source.Name, version.Version, version.ManifestDigest), relativePath, bounds);
                    }
                    continue;
                }
                ReservePath(occupied, relativePath, "source_registry_output_path_duplicate");
                var fullPath = ResolveDescendant(root, relativePath, "source_registry_manifest_path_invalid");
                ValidateRegularDescendant(root, fullPath, relativePath, "manifest", "source_registry_manifest_missing", "source_registry_manifest_not_regular");
                var sourceFile = await ReadUtf8Async(fullPath, SourceManifestReader.MaximumManifestBytes, "source_manifest_size_limit", cancellationToken);
                var parsed = ValidateSourceManifest(sourceFile.Text, relativePath, source.Publisher, source.Name, version.Version, version.ManifestDigest);
                ValidateShardMembership(parsed, relativePath, bounds);
                manifests.Add(relativePath, new(relativePath, fullPath, source.Publisher, source.Name, version.Version, version.ManifestDigest, bounds));
            }
        }
    }

    private static void ValidateShardMembership(ParsedSourceManifest source, string path, SourceCompatibilityBounds? shard)
    {
        if (shard is null || source.Manifest.Definition.Channels.Any(channel => Intersects(channel.Compatibility!.Agentstration!, shard))) return;
        throw Invalid("source_registry_shard_compatibility_missing", $"Manifest '{path}' has no Channel whose Agentstration compatibility intersects its shard interval.");
    }

    private static bool Intersects(SourceCompatibilityBounds left, SourceCompatibilityBounds right)
    {
        _ = SourceSemanticVersion.TryParse(left.MinVersion, out var leftMinimum);
        _ = SourceSemanticVersion.TryParse(right.MinVersion, out var rightMinimum);
        SourceSemanticVersion? leftMaximum = null;
        SourceSemanticVersion? rightMaximum = null;
        if (left.MaxVersionExclusive is { } leftValue) _ = SourceSemanticVersion.TryParse(leftValue, out leftMaximum);
        if (right.MaxVersionExclusive is { } rightValue) _ = SourceSemanticVersion.TryParse(rightValue, out rightMaximum);
        return (leftMaximum is null || rightMinimum.CompareTo(leftMaximum) < 0) && (rightMaximum is null || leftMinimum.CompareTo(rightMaximum) < 0);
    }

    private static async Task WriteCanonicalDocumentAsync(string root, string documentPath, string digestPath, byte[] json, string digest, CancellationToken cancellationToken)
    {
        var document = ResolveDescendant(root, documentPath, "source_registry_output_path_invalid");
        var digestFile = ResolveDescendant(root, digestPath, "source_registry_output_path_invalid");
        Directory.CreateDirectory(Path.GetDirectoryName(document)!);
        Directory.CreateDirectory(Path.GetDirectoryName(digestFile)!);
        await File.WriteAllBytesAsync(document, json, cancellationToken);
        await File.WriteAllTextAsync(digestFile, digest + "\n", Encoding.ASCII, cancellationToken);
    }

    private static SourceRegistryPublicationKind ValidateEntryLocation(string entry, string root)
    {
        if (!Directory.Exists(root)) throw Invalid("source_registry_publication_root_missing", "The publication root does not exist.");
        if (!File.Exists(entry)) throw new FileNotFoundException("The Registry publication entry document was not found.", entry);
        if (Path.GetDirectoryName(entry) is not { } parent || !PathEquals(parent, root))
            throw Invalid("source_registry_file_name_invalid", "The Registry or Index document must be directly beneath --publication-root.");
        return Path.GetFileName(entry).ToLowerInvariant() switch
        {
            "registry.yaml" or "registry.yml" or "registry.json" => SourceRegistryPublicationKind.Shard,
            "index.yaml" or "index.yml" or "index.json" => SourceRegistryPublicationKind.Index,
            _ => throw Invalid("source_registry_file_name_invalid", "The entry document must be registry.{json,yaml,yml} or index.{json,yaml,yml}.")
        };
    }

    private static void ValidateOutputRoot(string output)
    {
        if (File.Exists(output)) throw Invalid("source_registry_output_invalid", "The output root must be a directory path.");
        if (!Directory.Exists(output)) return;
        RejectReparsePoint(new DirectoryInfo(output), "Output root");
        if (Directory.EnumerateFileSystemEntries(output).Any()) throw Invalid("source_registry_output_not_empty", "A pre-existing output directory must be empty.");
    }

    private static void ValidateDistinctTrees(string input, string output)
    {
        if (PathEquals(input, output) || IsDescendant(input, output) || IsDescendant(output, input))
            throw Invalid("source_registry_output_overlap", "Input and output roots must be distinct and neither may contain the other.");
    }

    private static string ResolveDescendant(string root, string relativePath, string code)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsDescendant(fullPath, root)) throw Invalid(code, $"Publication path '{relativePath}' escapes the publication root.");
        return fullPath;
    }

    private static void ValidateRegularDescendant(string root, string fullPath, string relativePath, string label, string missingCode, string notRegularCode)
    {
        if (!File.Exists(fullPath)) throw Invalid(missingCode, $"Referenced {label} '{relativePath}' does not exist.");
        for (var current = new FileInfo(fullPath) as FileSystemInfo; current is not null; current = current switch { FileInfo file => file.Directory, DirectoryInfo directory => directory.Parent, _ => null })
        {
            RejectReparsePoint(current, $"Publication path '{relativePath}'");
            if (PathEquals(current.FullName, root)) break;
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0) throw Invalid(notRegularCode, $"Referenced {label} '{relativePath}' must be a regular file.");
    }

    private static void ReservePath(Dictionary<string, string> paths, string path, string code)
    {
        if (paths.TryGetValue(path, out var existing))
            throw Invalid(code, existing == path ? $"Publication path '{path}' is declared more than once." : $"Publication paths '{existing}' and '{path}' collide by case.");
        paths.Add(path, path);
    }

    private static string DigestPath(string path)
    {
        var extension = path.LastIndexOf('.');
        return extension < 0 ? path + ".sha256" : path[..extension] + ".sha256";
    }

    private static void RejectReparsePoint(FileSystemInfo info, string label)
    {
        if (!info.Exists) return;
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            throw Invalid("source_registry_reparse_point", $"{label} must not contain a symbolic link, junction, or reparse point.");
    }

    private ParsedSourceManifest ValidateSourceManifest(string rawSource, string path, string publisher, string name, string version, string digest)
    {
        var parsed = sourceReader.Read(rawSource);
        var actualPublisher = parsed.Manifest.Definition.Publisher.Name;
        var actualName = parsed.Manifest.Metadata.Name;
        var actualVersion = parsed.Manifest.Definition.Version;
        if (actualPublisher != publisher || actualName != name) throw Invalid("source_registry_manifest_identity_mismatch", $"Manifest '{path}' declares '{actualPublisher}/{actualName}', expected '{publisher}/{name}'.");
        if (actualVersion != version) throw Invalid("source_registry_manifest_version_mismatch", $"Manifest '{path}' declares version '{actualVersion}', expected '{version}'.");
        if (parsed.Digest != digest) throw Invalid("source_registry_manifest_digest_mismatch", $"Manifest '{path}' digest does not match the Registry entry for '{publisher}/{name}@{version}'.");
        return parsed;
    }

    private static async Task<BoundedUtf8File> ReadUtf8Async(string path, int maximumBytes, string sizeCode, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[maximumBytes + 1];
        var length = 0;
        while (length < bytes.Length) { var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken); if (read == 0) break; length += read; }
        if (length > maximumBytes) throw Invalid(sizeCode, $"File '{Path.GetFileName(path)}' exceeds the {maximumBytes}-byte limit.");
        var content = bytes.AsSpan(0, length);
        if (content.StartsWith(Encoding.UTF8.Preamble)) content = content[Encoding.UTF8.Preamble.Length..];
        try { return new(bytes[..length], StrictUtf8.GetString(content)); }
        catch (DecoderFallbackException) { throw Invalid("source_registry_encoding_invalid", $"File '{Path.GetFileName(path)}' must use valid UTF-8."); }
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), PathComparison);
    private static bool IsDescendant(string path, string parent) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static SourceValidationException Invalid(string code, string message) => new(code, message);
    private sealed record BoundedUtf8File(byte[] Bytes, string Text);
}
