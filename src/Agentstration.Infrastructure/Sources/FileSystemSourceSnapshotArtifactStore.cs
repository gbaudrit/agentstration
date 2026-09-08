using System.Security.Cryptography;
using Agentstration.Management.Abstractions;

namespace Agentstration.Infrastructure.Sources;

public sealed class FileSystemSourceSnapshotArtifactStore(string rootPath) : ISourceSnapshotArtifactStore
{
    private readonly string root = EnsureRoot(rootPath);

    public async Task<SourceSnapshotArtifactReference> SaveAsync(
        MaterializedSourceRevision content,
        CancellationToken cancellationToken)
    {
        if (content.Content.IsEmpty)
            throw new SourceRetrievalException("source_archive_empty", "The source provider returned an empty archive.");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content.Content.Span));
        if (!string.Equals(content.Integrity.Algorithm, "sha256", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(content.Integrity.Digest, sha256, StringComparison.OrdinalIgnoreCase))
            throw new SourceRetrievalException("source_integrity_mismatch", "The materialized source archive does not match its integrity metadata.");

        var key = $"{sha256}.source";
        var path = Resolve(key);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync(content.Content, cancellationToken);
        }
        catch (IOException) when (File.Exists(path))
        {
            if (new FileInfo(path).Length != content.Content.Length)
                throw new SourceRetrievalException("source_artifact_collision", "A stored source artifact has an unexpected length.");
        }
        return new(key, content.MediaType, sha256, content.Content.Length, content.ExpandedBytes, content.EntryCount);
    }

    public Task<Stream> OpenReadAsync(SourceSnapshotArtifactReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(reference.StorageKey);
        if (!File.Exists(path)) throw new FileNotFoundException("The source snapshot artifact is unavailable.", reference.StorageKey);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 96 || key.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || key.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("The source artifact storage key is invalid.", nameof(key));
        var path = Path.GetFullPath(Path.Combine(root, key));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source artifact path is outside the configured store.");
        return path;
    }

    private static string EnsureRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(full);
        return full;
    }
}
