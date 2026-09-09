using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Tools.SourceRegistry;

public sealed record SourceRegistryPublicationValidation(
    ParsedSourceRegistry Registry,
    IReadOnlyList<SourceRegistryPublicationFile> Files,
    int SourceCount,
    int VersionCount);

public sealed record SourceRegistryPublicationFile(
    string RelativePath,
    string FullPath,
    string Publisher,
    string Source,
    string Version,
    string Digest);

public sealed class SourceRegistryPublicationService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SourceRegistryReader registryReader = new();
    private readonly SourceManifestReader sourceReader = new();

    public async Task<SourceRegistryPublicationValidation> ValidateAsync(
        string registryPath,
        string publicationRoot,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(publicationRoot);
        var registry = Path.GetFullPath(registryPath);
        ValidateRegistryLocation(registry, root);
        RejectReparsePoint(new DirectoryInfo(root), "Publication root");
        RejectReparsePoint(new FileInfo(registry), "Registry document");

        var registryFile = await ReadUtf8Async(registry, SourceRegistryLimits.MaximumDocumentBytes, "source_registry_size_limit", cancellationToken);
        var parsed = registryReader.Read(registryFile.Text, registry);
        SourceRegistryReferenceResolver.ValidateBaseUri(baseUri);

        var files = new List<SourceRegistryPublicationFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "registry.json",
            "registry.sha256"
        };
        foreach (var source in parsed.Manifest.Definition.Sources)
        {
            foreach (var version in source.Versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = SourceRegistryReferenceResolver.ResolvePublicationPath(baseUri, version.ManifestUrl);
                if (!paths.Add(relativePath))
                    throw Invalid("source_registry_output_path_duplicate", $"Publication path '{relativePath}' is declared more than once or conflicts with a generated Registry file.");
                var fullPath = ResolveDescendant(root, relativePath);
                ValidateRegularDescendant(root, fullPath, relativePath);
                var sourceFile = await ReadUtf8Async(fullPath, SourceManifestReader.MaximumManifestBytes, "source_manifest_size_limit", cancellationToken);
                ValidateSourceManifest(sourceFile.Text, relativePath, source.Publisher, source.Name, version.Version, version.ManifestDigest);
                files.Add(new(relativePath, fullPath, source.Publisher, source.Name, version.Version, version.ManifestDigest));
            }
        }

        return new(parsed, files, parsed.Manifest.Definition.Sources.Count, files.Count);
    }

    public async Task<SourceRegistryPublicationValidation> BuildAsync(
        string registryPath,
        string publicationRoot,
        Uri baseUri,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(publicationRoot);
        var output = Path.GetFullPath(outputRoot);
        ValidateDistinctTrees(input, output);
        ValidateOutputRoot(output);
        var validation = await ValidateAsync(registryPath, input, baseUri, cancellationToken);

        var parent = Path.GetDirectoryName(output)
            ?? throw Invalid("source_registry_output_invalid", "The output root must have a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".agentstration-source-registry-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(Path.Combine(staging, "registry.json"), validation.Registry.CanonicalJson, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(staging, "registry.sha256"),
                validation.Registry.RegistryDigest + "\n",
                Encoding.ASCII,
                cancellationToken);
            foreach (var file in validation.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveDescendant(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var sourceFile = await ReadUtf8Async(file.FullPath, SourceManifestReader.MaximumManifestBytes, "source_manifest_size_limit", cancellationToken);
                ValidateSourceManifest(sourceFile.Text, file.RelativePath, file.Publisher, file.Source, file.Version, file.Digest);
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

    private static void ValidateRegistryLocation(string registryPath, string root)
    {
        if (!Directory.Exists(root)) throw Invalid("source_registry_publication_root_missing", "The publication root does not exist.");
        if (!File.Exists(registryPath)) throw new FileNotFoundException("The Registry document was not found.", registryPath);
        if (Path.GetDirectoryName(registryPath) is not { } parent || !PathEquals(parent, root)
            || Path.GetFileName(registryPath).ToLowerInvariant() is not ("registry.yaml" or "registry.yml" or "registry.json"))
            throw Invalid("source_registry_file_name_invalid", "The Registry document must be registry.yaml, registry.yml, or registry.json directly beneath --publication-root.");
    }

    private static void ValidateOutputRoot(string output)
    {
        if (File.Exists(output)) throw Invalid("source_registry_output_invalid", "The output root must be a directory path.");
        if (!Directory.Exists(output)) return;
        RejectReparsePoint(new DirectoryInfo(output), "Output root");
        if (Directory.EnumerateFileSystemEntries(output).Any())
            throw Invalid("source_registry_output_not_empty", "A pre-existing output directory must be empty.");
    }

    private static void ValidateDistinctTrees(string input, string output)
    {
        if (PathEquals(input, output) || IsDescendant(input, output) || IsDescendant(output, input))
            throw Invalid("source_registry_output_overlap", "Input and output roots must be distinct and neither may contain the other.");
    }

    private static string ResolveDescendant(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsDescendant(fullPath, root))
            throw Invalid("source_registry_manifest_path_invalid", $"Publication path '{relativePath}' escapes the publication root.");
        return fullPath;
    }

    private static void ValidateRegularDescendant(string root, string fullPath, string relativePath)
    {
        if (!File.Exists(fullPath))
            throw Invalid("source_registry_manifest_missing", $"Referenced manifest '{relativePath}' does not exist.");
        for (var current = new FileInfo(fullPath) as FileSystemInfo; current is not null; current = current switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo directory => directory.Parent,
            _ => null
        })
        {
            RejectReparsePoint(current, $"Publication path '{relativePath}'");
            if (PathEquals(current.FullName, root)) break;
        }
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) != 0)
            throw Invalid("source_registry_manifest_not_regular", $"Referenced manifest '{relativePath}' must be a regular file.");
    }

    private static void RejectReparsePoint(FileSystemInfo info, string label)
    {
        if (!info.Exists) return;
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            throw Invalid("source_registry_reparse_point", $"{label} must not contain a symbolic link, junction, or reparse point.");
    }

    private void ValidateSourceManifest(
        string rawSource,
        string relativePath,
        string expectedPublisher,
        string expectedName,
        string expectedVersion,
        string expectedDigest)
    {
        var sourceManifest = sourceReader.Read(rawSource);
        var actualPublisher = sourceManifest.Manifest.Definition.Publisher.Name;
        var actualName = sourceManifest.Manifest.Metadata.Name;
        var actualVersion = sourceManifest.Manifest.Definition.Version;
        if (actualPublisher != expectedPublisher || actualName != expectedName)
            throw Invalid("source_registry_manifest_identity_mismatch", $"Manifest '{relativePath}' declares '{actualPublisher}/{actualName}', expected '{expectedPublisher}/{expectedName}'.");
        if (actualVersion != expectedVersion)
            throw Invalid("source_registry_manifest_version_mismatch", $"Manifest '{relativePath}' declares version '{actualVersion}', expected '{expectedVersion}'.");
        if (sourceManifest.Digest != expectedDigest)
            throw Invalid("source_registry_manifest_digest_mismatch", $"Manifest '{relativePath}' digest does not match the Registry entry for '{expectedPublisher}/{expectedName}@{expectedVersion}'.");
    }

    private static async Task<BoundedUtf8File> ReadUtf8Async(
        string path,
        int maximumBytes,
        string sizeCode,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[maximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > maximumBytes) throw Invalid(sizeCode, $"File '{Path.GetFileName(path)}' exceeds the {maximumBytes}-byte limit.");
        var content = bytes.AsSpan(0, length);
        if (content.StartsWith(Encoding.UTF8.Preamble)) content = content[Encoding.UTF8.Preamble.Length..];
        try
        {
            return new(bytes[..length], StrictUtf8.GetString(content));
        }
        catch (DecoderFallbackException)
        {
            throw Invalid("source_registry_encoding_invalid", $"File '{Path.GetFileName(path)}' must use valid UTF-8.");
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), PathComparison);

    private static bool IsDescendant(string path, string parent)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedParent, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    private sealed record BoundedUtf8File(byte[] Bytes, string Text);
}
