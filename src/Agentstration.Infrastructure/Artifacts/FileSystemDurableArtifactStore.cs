using System.Security.Cryptography;
using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class FileSystemDurableArtifactStore(string rootDirectory) : IArtifactDurableStore
{
    public async Task ResetAsync(WorkspaceId workspaceId, string opaqueReference, CancellationToken cancellationToken)
    {
        var path = PathFor(workspaceId, opaqueReference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        await stream.FlushAsync(cancellationToken);
    }

    public async Task<long> WriteAsync(WorkspaceId workspaceId, string opaqueReference, long offset,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var path = PathFor(workspaceId, opaqueReference);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (stream.Length != offset) throw new ArtifactValidationException("artifact_storage_offset_conflict", $"The next durable storage offset is {stream.Length}.");
        stream.Position = offset;
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return stream.Length;
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(WorkspaceId workspaceId, string opaqueReference, long offset,
        int length, CancellationToken cancellationToken)
    {
        var path = PathFor(workspaceId, opaqueReference);
        if (!File.Exists(path)) throw new ArtifactValidationException("flow_run_artifact_content_not_found", "Durable Artifact content was not found.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (offset < 0 || offset > stream.Length) throw new ArtifactValidationException("artifact_storage_read_range_invalid", "The durable content offset is invalid.");
        stream.Position = offset;
        var buffer = new byte[Math.Min(length, checked((int)Math.Min(int.MaxValue, stream.Length - offset)))];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.AsMemory(0, read);
    }

    public async Task<ArtifactBackendStat> StatAsync(WorkspaceId workspaceId, string opaqueReference, CancellationToken cancellationToken)
    {
        var path = PathFor(workspaceId, opaqueReference);
        if (!File.Exists(path)) throw new ArtifactValidationException("flow_run_artifact_content_not_found", "Durable Artifact content was not found.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(stream.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private string PathFor(WorkspaceId workspaceId, string opaqueReference)
    {
        if (string.IsNullOrWhiteSpace(opaqueReference) || opaqueReference.Length > 160
            || opaqueReference.Any(value => !char.IsLetterOrDigit(value) && value is not '-' and not '_'))
            throw new ArtifactValidationException("artifact_storage_reference_invalid", "The durable Artifact reference is invalid.");
        var root = Path.GetFullPath(rootDirectory);
        var directory = Path.GetFullPath(Path.Combine(root, workspaceId.Value.ToString("N")));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArtifactValidationException("artifact_storage_path_invalid", "The durable Artifact path is outside its configured root.");
        return Path.Combine(directory, opaqueReference + ".bin");
    }
}
