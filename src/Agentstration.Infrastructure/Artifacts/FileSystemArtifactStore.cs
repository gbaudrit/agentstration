using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class FileSystemArtifactStore(string rootPath) : IArtifactStore
{
    private readonly string root = EnsureRoot(rootPath);

    public async Task<ArtifactReference> SaveAsync(ArtifactContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content.Name) || content.Name.Length > 240) throw new WorkValidationException("artifact_name_invalid", "An artifact name of at most 240 characters is required.");
        if (string.IsNullOrWhiteSpace(content.ContentType) || content.ContentType.Length > 160) throw new WorkValidationException("artifact_content_type_invalid", "A valid artifact content type is required.");
        var extension = Path.GetExtension(Path.GetFileName(content.Name));
        if (extension.Length > 16 || extension.Any(character => !char.IsLetterOrDigit(character) && character != '.')) extension = string.Empty;
        var key = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var path = Resolve(key);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await content.Content.CopyToAsync(target, cancellationToken);
        return new ArtifactReference(key, content.ContentType, target.Length);
    }

    public Task<Stream> OpenReadAsync(ArtifactReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(Resolve(reference.StorageKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<Stream>(stream);
    }

    public Task DeleteAsync(ArtifactReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(reference.StorageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 80 || key.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || key.Contains("..", StringComparison.Ordinal))
            throw new WorkValidationException("artifact_storage_key_invalid", "The artifact storage key is invalid.");
        var path = Path.GetFullPath(Path.Combine(root, key));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new WorkValidationException("artifact_path_invalid", "The artifact path is outside the configured store.");
        return path;
    }

    private static string EnsureRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var fullPath = Path.GetFullPath(value);
        Directory.CreateDirectory(fullPath);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
