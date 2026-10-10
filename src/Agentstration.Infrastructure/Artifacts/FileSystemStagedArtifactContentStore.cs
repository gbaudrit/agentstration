using System.Security.Cryptography;
using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class FileSystemStagedArtifactContentStore(string rootPath) : IArtifactContentStore
{
    private readonly string root = EnsureRoot(rootPath);

    public async Task<string> CreateAsync(WorkspaceId workspaceId, StagedArtifactId artifactId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reference = artifactId.ToString();
        var path = Resolve(workspaceId, reference, createWorkspace: true);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.FlushAsync(cancellationToken);
        return reference;
    }

    public async Task<long> WriteAsync(WorkspaceId workspaceId, string backendReference, long offset,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (offset < 0) throw Error("artifact_staging_offset_invalid", "Artifact offsets cannot be negative.");
        var path = Resolve(workspaceId, backendReference);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (stream.Length != offset)
            throw Error("artifact_staging_offset_conflict", $"The backend expects offset {stream.Length}.");
        stream.Position = offset;
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return stream.Length;
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(WorkspaceId workspaceId, string backendReference, long offset,
        int length, CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 1) throw Error("artifact_staging_read_range_invalid", "A positive bounded read range is required.");
        await using var stream = new FileStream(Resolve(workspaceId, backendReference), FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > stream.Length) throw Error("artifact_staging_read_range_invalid", "The requested offset exceeds the content length.");
        stream.Position = offset;
        var buffer = new byte[Math.Min(length, checked((int)Math.Min(int.MaxValue, stream.Length - offset)))];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.AsMemory(0, read);
    }

    public async Task<ArtifactBackendStat> StatAsync(WorkspaceId workspaceId, string backendReference, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Resolve(workspaceId, backendReference), FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return new ArtifactBackendStat(stream.Length, Convert.ToHexStringLower(digest));
    }

    public Task DeleteAsync(WorkspaceId workspaceId, string backendReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(workspaceId, backendReference);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(WorkspaceId workspaceId, string backendReference, bool createWorkspace = false)
    {
        if (workspaceId.Value == Guid.Empty) throw Error("artifact_workspace_required", "A Workspace identifier is required.");
        if (!Guid.TryParseExact(backendReference, "N", out _))
            throw Error("artifact_backend_reference_invalid", "The filesystem backend reference is invalid.");
        var workspaceRoot = Path.GetFullPath(Path.Combine(root, workspaceId.Value.ToString("N")));
        if (!workspaceRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Error("artifact_backend_path_invalid", "The Workspace staging path is outside the configured root.");
        if (createWorkspace) Directory.CreateDirectory(workspaceRoot);
        var path = Path.GetFullPath(Path.Combine(workspaceRoot, backendReference + ".staged"));
        if (!path.StartsWith(workspaceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Error("artifact_backend_path_invalid", "The artifact path is outside the Workspace staging directory.");
        return path;
    }

    private static string EnsureRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static ArtifactValidationException Error(string code, string message) => new(code, message);
}
