using Agentstration.Management.Abstractions;

namespace Agentstration.Infrastructure.Sources;

public sealed class FileSystemSourceRegistryCacheStore(string rootPath) : ISourceRegistryCacheStore
{
    private readonly string root = Path.GetFullPath(rootPath);

    public async Task StoreAsync(SourceRegistryCachedPublication publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (publication.ObservationId == Guid.Empty) throw new ArgumentException("A cache observation ID is required.", nameof(publication));
        if (publication.Index.Length > SourceRegistryLimits.MaximumIndexDocumentBytes)
            throw new IOException("The canonical registry index exceeds the cache limit.");
        if (publication.Catalogs.Count is < 1 or > SourceRegistryLimits.MaximumCatalogs)
            throw new IOException("The registry cache must contain between 1 and 128 catalogues.");

        Directory.CreateDirectory(root);
        RejectReparsePoint(root);
        var destination = Descendant(publication.ObservationId.ToString("N"));
        if (Directory.Exists(destination)) throw new IOException("The registry cache observation already exists.");
        var temporary = Descendant($".{publication.ObservationId:N}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporary);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(temporary, "index.json"), publication.Index, cancellationToken);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.json" };
            foreach (var catalog in publication.Catalogs.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var normalized = SourceDescendantPath.Normalize(catalog.Key, "Registry catalogue cache path");
                if (!paths.Add(normalized)) throw new IOException("Registry cache paths collide by case.");
                if (catalog.Value.Length > SourceRegistryLimits.MaximumDocumentBytes)
                    throw new IOException("A canonical registry catalogue exceeds the cache limit.");
                var target = Path.GetFullPath(Path.Combine(temporary, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A registry cache path escaped its observation directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, catalog.Value, cancellationToken);
            }
            Directory.Move(temporary, destination);
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    public async Task<SourceRegistryCachedPublication?> GetAsync(Guid observationId, CancellationToken cancellationToken)
    {
        if (observationId == Guid.Empty) throw new ArgumentException("A cache observation ID is required.", nameof(observationId));
        var directory = Descendant(observationId.ToString("N"));
        if (!Directory.Exists(directory)) return null;
        RejectReparsePoint(root);
        RejectReparsePoint(directory);
        var indexPath = Path.Combine(directory, "index.json");
        if (!File.Exists(indexPath)) throw new IOException("The registry cache observation has no index.");
        var index = await File.ReadAllBytesAsync(indexPath, cancellationToken);
        var catalogs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(path);
            var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, "index.json", StringComparison.Ordinal)) continue;
            if (catalogs.Count >= SourceRegistryLimits.MaximumCatalogs) throw new IOException("The registry cache contains too many catalogues.");
            catalogs.Add(relative, await File.ReadAllBytesAsync(path, cancellationToken));
        }
        return new(observationId, index, catalogs);
    }

    private string Descendant(string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The registry cache path escaped its root.");
        return path;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Registry cache paths cannot contain reparse points.");
    }
}
