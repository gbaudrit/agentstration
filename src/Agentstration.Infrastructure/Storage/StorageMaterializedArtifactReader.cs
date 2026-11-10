using System.Security.Cryptography;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Reads an Artifact's exact materialization through its owning Storage boundary.</summary>
public sealed class StorageMaterializedArtifactReader(IStorageContentGateway content, IStoragePublicationGateway publications,
    IIdentityStore identities, ICurrentRequestContext current, IRequestContextScopeFactory scopes)
{
    public async Task<ReadOnlyMemory<byte>> ReadAsync(WorkspaceId workspaceId, ArtifactStorageMaterialization value,
        long offset, int length, CancellationToken cancellationToken)
    {
        if (value.WorkspaceId != workspaceId || current.Current.WorkspaceId != workspaceId.Value
            || value.Length is < 0 or > 65536 || offset < 0 || offset > value.Length || length < 1
            || value.Target is null || value.Target.Consumer is null || value.Target.Storage is null || value.Target.Policy is null
            || string.IsNullOrWhiteSpace(value.Key) || value.Key.Length > 2048
            || value.Sha256 is not { Length: 64 } || !value.Sha256.All(char.IsAsciiHexDigit))
            throw Error("artifact_storage_reference_invalid");
        var workspace = await identities.GetWorkspaceAsync(current.Current.TenantId, workspaceId.Value, cancellationToken)
            ?? throw Error("artifact_storage_workspace_unavailable");
        // Administrative Artifacts belong to that scope's dedicated operational Workspace.
        // A reference never promotes a regular Workspace into an administrative context.
        var owner = LocalStorageDefaultsProvisioner.OwnerScope(workspace);
        if (value.Target.Consumer.ConsumerScopeRef != owner) throw Error("artifact_storage_scope_invalid");
        using var logical = workspace.Kind switch
        {
            WorkspaceKind.InstanceOperations => scopes.PushInstance(current.Current),
            WorkspaceKind.TenantOperations => scopes.PushTenantAdministration(current.Current),
            _ => scopes.Push(current.Current)
        };
        using var bytes = new MemoryStream();
        var target = value.Target with { Policy = value.Target.Policy with { Write = false } };
        StorageObject? item = null;
        if (value.Publication is { } publication)
        {
            if (publication.Target != value.Target || publication.Key != value.Key || publication.Version != value.Version
                || publication.Length != value.Length || publication.Sha256 != value.Sha256)
                throw Error("artifact_storage_reference_invalid");
        }
        else
        {
            var page = await content.ListAsync(target, value.Key, 1, cancellationToken: cancellationToken);
            item = page.Objects.SingleOrDefault(candidate => candidate.Key == value.Key);
            if (item is null || item.Length != value.Length || value.Version is not null && item.Version != value.Version
                || item.Sha256 is not null && !string.Equals(item.Sha256, value.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("artifact_storage_content_changed");
        }
        var chunkSize = Math.Max(1, Math.Min(65536, value.Target.Policy.MaximumReadBytes));
        do
        {
            var count = (int)Math.Min(chunkSize, value.Length - bytes.Length);
            try
            {
                ReadOnlyMemory<byte> chunk;
                if (value.Publication is { } retained)
                    chunk = await publications.ReadAsync(retained, bytes.Length, Math.Max(1, count), cancellationToken);
                else
                {
                    var result = await content.ReadAsync(target, item!.Reference, bytes.Length, Math.Max(1, count), cancellationToken);
                    if (result.Offset != bytes.Length || result.Item.Key != value.Key || result.Item.Version != item.Version
                        || result.Item.Length != value.Length) throw Error("artifact_storage_content_changed");
                    chunk = Convert.FromBase64String(result.ContentBase64);
                }
                if (chunk.Length > count || chunk.Length == 0 && bytes.Length < value.Length)
                    throw Error("artifact_storage_content_incomplete");
                await bytes.WriteAsync(chunk, cancellationToken);
            }
            catch (StorageException exception) when (exception.Code is "storage_content_limit_exceeded" or "storage_publication_range_invalid" && chunkSize > 1)
            { chunkSize = Math.Max(1, chunkSize / 2); continue; }
        } while (bytes.Length < value.Length);
        var all = bytes.ToArray();
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(all)), value.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("artifact_storage_integrity_failed");
        return all.AsMemory((int)offset, (int)Math.Min(length, value.Length - offset));
    }

    private static ArtifactValidationException Error(string code) => new(code, "The exact Artifact materialization is unavailable or invalid.");
}
