using System.Security.Cryptography;
using Agentstration.Management.Abstractions;

namespace Agentstration.Infrastructure.Packs;

public sealed class FileSystemPackArtifactStore(string rootPath) : IPackArtifactStore
{
    private readonly string root = EnsureRoot(rootPath);

    public async Task<PackArtifactReference> SaveAsync(ReadOnlyMemory<byte> content, string fileName, CancellationToken cancellationToken)
    {
        if (content.IsEmpty) throw new ArgumentException("A Pack artifact cannot be empty.", nameof(content));
        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var key = $"{hash}.pack.zip";
        var path = Resolve(key);
        if (!File.Exists(path))
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync(content, cancellationToken);
        }
        return new(key, hash, content.Length, SafeName(fileName));
    }

    public Task<Stream> OpenReadAsync(PackArtifactReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(reference.StorageKey);
        if (!File.Exists(path)) throw new FileNotFoundException("The Pack artifact is unavailable.", reference.StorageKey);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 96 || key.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || key.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("The Pack artifact storage key is invalid.", nameof(key));
        var path = Path.GetFullPath(Path.Combine(root, key));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The Pack artifact path is outside the configured store.");
        return path;
    }

    private static string EnsureRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(full);
        return full;
    }

    private static string SafeName(string value)
    {
        var result = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? "pack.zip" : value.Trim());
        return result.Length <= 256 ? result : result[..256];
    }
}
