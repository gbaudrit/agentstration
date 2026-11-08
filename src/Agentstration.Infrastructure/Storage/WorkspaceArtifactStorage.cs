using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>New Artifact receipts pin a Storage; existing receipts keep their original filesystem adapter.</summary>
public sealed class WorkspaceArtifactStorage(IIdentityStore identities, IResourceStore resources,
    IStorageResolver storages, LocalStoragePaths paths, FileSystemDurableArtifactStore historical,
    ICurrentRequestContext current, IRequestContextScopeFactory scopes) : IArtifactDurableStore
{
    private const string Prefix = "storage-v1.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Reference(Guid WorkspaceId, StorageRevisionReference Storage, string ObjectId, long Length, string Sha256);
    private enum Operation { Read, Write, Stat }
    private sealed record Target(FileSystemDurableArtifactStore Store, string Key, string Directory,
        Reference? Reference, StorageContentPolicy? Policy);

    public async Task<string> CreateReferenceAsync(WorkspaceId workspaceId, string objectId, long length, string sha256,
        CancellationToken cancellationToken)
    {
        if (objectId is not { Length: 64 } || !objectId.All(char.IsAsciiHexDigit)
            || length < 0 || sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigit))
            throw new StorageException("storage_artifact_reference_invalid", "Exact Artifact identity and integrity metadata are required.");
        var workspace = await identities.GetWorkspaceAsync(current.Current.TenantId, workspaceId.Value, cancellationToken)
            ?? throw new StorageException("storage_default_unavailable", "The execution Workspace is unavailable.", 409);
        StorageResource resource;
        using (scopes.PushSystem())
            resource = (await resources.GetExactAsync<StorageResource>(new(LocalStorageDefaultsProvisioner.OwnerScope(workspace),
                ResourceNamespace.Default, StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), cancellationToken))?.Value
                ?? throw new StorageException("storage_default_unavailable", "The Workspace default Storage is unavailable.", 409);
        var reference = new StorageRevisionReference(new(resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Name),
            resource.Uid, resource.Generation, StorageDigests.Binding(resource.Definition));
        _ = await AuthorizeAsync(reference, workspaceId, Operation.Write, cancellationToken);
        return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Reference(workspaceId.Value, reference, objectId, length, sha256), Json));
    }

    public async Task ResetAsync(WorkspaceId workspaceId, string opaqueReference, CancellationToken cancellationToken) =>
        _ = await GuardAsync(async () => { await ResetCoreAsync(workspaceId, opaqueReference, cancellationToken); return true; });
    public Task<long> WriteAsync(WorkspaceId workspaceId, string opaqueReference, long offset,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        GuardAsync(() => WriteCoreAsync(workspaceId, opaqueReference, offset, content, cancellationToken));
    public Task<ReadOnlyMemory<byte>> ReadAsync(WorkspaceId workspaceId, string opaqueReference, long offset,
        int length, CancellationToken cancellationToken) =>
        GuardAsync(() => ReadCoreAsync(workspaceId, opaqueReference, offset, length, cancellationToken));
    public Task<ArtifactBackendStat> StatAsync(WorkspaceId workspaceId, string opaqueReference, CancellationToken cancellationToken) =>
        GuardAsync(() => StatCoreAsync(workspaceId, opaqueReference, cancellationToken));

    private async Task ResetCoreAsync(WorkspaceId workspaceId, string opaqueReference, CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(workspaceId, opaqueReference, Operation.Write, cancellationToken);
        if (target.Reference is null) { await target.Store.ResetAsync(workspaceId, target.Key, cancellationToken); return; }
        _ = paths.EnsureDirectoryFor(target.Reference.Storage);
        Directory.CreateDirectory(target.Directory);
        await using var fileLock = await LockAsync(target, cancellationToken);
        if (File.Exists(Physical(target, target.Key)))
        { Verify(await target.Store.StatAsync(workspaceId, target.Key, cancellationToken), target.Reference); return; }
        var pending = Pending(target.Key);
        await using var capacityLock = await LockAsync(target with { Key = "pending-capacity" }, cancellationToken);
        if (!File.Exists(Physical(target, pending))
            && Directory.EnumerateFiles(target.Directory, "*-pending.bin").Take(128).Count() >= 128)
            throw new StorageException("storage_artifact_capacity_exceeded", "The pending Artifact capacity has been reached.", 429);
        await target.Store.ResetAsync(workspaceId, pending, cancellationToken);
    }
    private async Task<long> WriteCoreAsync(WorkspaceId workspaceId, string opaqueReference, long offset,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(workspaceId, opaqueReference, Operation.Write, cancellationToken);
        if (target.Reference is null) return await target.Store.WriteAsync(workspaceId, target.Key, offset, content, cancellationToken);
        await using var fileLock = await LockAsync(target, cancellationToken);
        if (offset < 0 || content.Length > target.Policy!.MaximumWriteBytes || offset > target.Reference.Length - content.Length)
            throw new StorageException("storage_artifact_limit_exceeded", "The Artifact write exceeds the pinned envelope.");
        if (File.Exists(Physical(target, target.Key)))
        {
            Verify(await target.Store.StatAsync(workspaceId, target.Key, cancellationToken), target.Reference);
            var existing = await target.Store.ReadAsync(workspaceId, target.Key, offset, content.Length, cancellationToken);
            if (!existing.Span.SequenceEqual(content.Span))
                throw new StorageException("storage_artifact_conflict", "The retained Artifact content differs from the retry.", 409);
            return offset + content.Length;
        }
        if (!File.Exists(Physical(target, Pending(target.Key))))
            throw new StorageException("storage_artifact_copy_required", "Start the Artifact copy before writing content.", 409);
        return await target.Store.WriteAsync(workspaceId, Pending(target.Key), offset, content, cancellationToken);
    }
    private async Task<ReadOnlyMemory<byte>> ReadCoreAsync(WorkspaceId workspaceId, string opaqueReference, long offset,
        int length, CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(workspaceId, opaqueReference, Operation.Read, cancellationToken);
        if (target.Policy is { } policy && (length < 1 || length > policy.MaximumReadBytes))
            throw new StorageException("storage_artifact_limit_exceeded", "The Artifact read exceeds the pinned envelope.");
        return await target.Store.ReadAsync(workspaceId, target.Key, offset, length, cancellationToken);
    }
    private async Task<ArtifactBackendStat> StatCoreAsync(WorkspaceId workspaceId, string opaqueReference, CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(workspaceId, opaqueReference, Operation.Stat, cancellationToken);
        if (target.Reference is null) return await target.Store.StatAsync(workspaceId, target.Key, cancellationToken);
        await using var fileLock = await LockAsync(target, cancellationToken);
        var destination = Physical(target, target.Key);
        if (!File.Exists(destination))
        {
            _ = await AuthorizeAsync(target.Reference.Storage, workspaceId, Operation.Write, cancellationToken);
            var pending = Pending(target.Key);
            var pendingPath = Physical(target, pending);
            Verify(await target.Store.StatAsync(workspaceId, pending, cancellationToken), target.Reference);
            try { File.Move(pendingPath, destination); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        var committed = await target.Store.StatAsync(workspaceId, target.Key, cancellationToken);
        Verify(committed, target.Reference);
        return committed;
    }

    public Task<int> MaximumChunkBytesAsync(WorkspaceId workspaceId, string reference, bool write, CancellationToken cancellationToken) => GuardAsync(async () =>
    {
        var target = await ResolveAsync(workspaceId, reference, write ? Operation.Write : Operation.Read, cancellationToken);
        return target.Policy is null ? ArtifactManagementService.MaximumChunkBytes
            : write ? target.Policy.MaximumWriteBytes : target.Policy.MaximumReadBytes;
    });

    internal static StorageRevisionReference? StorageReference(string reference) => Parse(reference)?.Storage;

    private async Task<Target> ResolveAsync(WorkspaceId workspaceId, string reference,
        Operation operation, CancellationToken cancellationToken)
    {
        var value = Parse(reference);
        if (value is null) return new(historical, reference, "", null, null);
        if (value.WorkspaceId != workspaceId.Value || value.ObjectId is not { Length: 64 } || !value.ObjectId.All(char.IsAsciiHexDigit)
            || value.Length < 0 || value.Sha256 is not { Length: 64 } || !value.Sha256.All(char.IsAsciiHexDigit))
            throw new StorageException("storage_artifact_reference_invalid", "The Artifact Storage reference is invalid.", 403);
        var policy = await AuthorizeAsync(value.Storage, workspaceId, operation, cancellationToken);
        var logicalKey = "artifacts/" + value.ObjectId;
        if (!logicalKey.StartsWith(policy.Prefix, StringComparison.Ordinal)
            || policy.KeyFrom is not null && string.CompareOrdinal(logicalKey, policy.KeyFrom) < 0
            || policy.KeyBefore is not null && string.CompareOrdinal(logicalKey, policy.KeyBefore) >= 0)
            throw new StorageException("storage_artifact_access_denied", "The Artifact is outside the pinned Storage envelope.", 403);
        var directory = LocalStoragePaths.Checked(Path.Combine(paths.DirectoryFor(value.Storage), "artifacts"));
        LocalStoragePaths.Checked(Path.Combine(directory, workspaceId.Value.ToString("N"), value.ObjectId + ".bin"));
        return new(new FileSystemDurableArtifactStore(directory), value.ObjectId,
            Path.Combine(directory, workspaceId.Value.ToString("N")), value, policy);
    }

    private async Task<StorageContentPolicy> AuthorizeAsync(StorageRevisionReference storage, WorkspaceId workspaceId,
        Operation operation, CancellationToken cancellationToken)
    {
        if (current.AccessMode != ControlPlaneAccessMode.System && current.Current.WorkspaceId != workspaceId.Value)
            throw new StorageException("storage_access_denied", "The Artifact execution Workspace differs from the caller.", 403);
        var consumer = current.AccessMode switch
        {
            ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
            ControlPlaneAccessMode.Tenant or ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(current.Current.TenantId),
            _ => ResourceScopeRef.Workspace(workspaceId.Value)
        };
        var context = await storages.ResolveAsync(storage, new(consumer, storage.Storage.Address), cancellationToken);
        if (context.Type.Type.ScopeRef != ResourceScopeRef.Instance || context.Type.Type.Name != StoragePlatformResourceProvisioner.LocalTypeName
            || context.Type.Type.Namespace is { } ns && ns != ResourceNamespace.Default
            || context.Type.Version != StoragePlatformResourceProvisioner.ContentTypeVersion
            || (operation == Operation.Write ? !context.Capabilities.Write : operation == Operation.Read ? !context.Capabilities.Read : !context.Capabilities.Stat))
            throw new StorageException("storage_artifact_incompatible", "The selected Artifact Storage implementation is incompatible.", 409);
        var policy = context.Envelope.Deserialize<StorageContentPolicy>(Json)
            ?? throw new StorageException("storage_artifact_incompatible", "The local Artifact envelope is invalid.", 409);
        if (operation == Operation.Write ? !policy.Write || !policy.Stat : operation == Operation.Read ? !policy.Read : !policy.Stat)
            throw new StorageException("storage_artifact_access_denied", "The pinned Storage envelope refuses this Artifact operation.", 403);
        return policy;
    }

    private static string Pending(string key) => key + "-pending";
    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new StorageException("storage_artifact_unavailable", "The Artifact content backend is unavailable.", 503); }
        catch (Agentstration.Asp.Abstractions.AspException)
        { throw new StorageException("storage_artifact_path_invalid", "The configured Artifact content location is unsafe.", 409); }
    }
    private static string Physical(Target target, string key) =>
        LocalStoragePaths.Checked(Path.Combine(target.Directory, key + ".bin"));
    private static void Verify(ArtifactBackendStat value, Reference reference)
    {
        if (value.Length != reference.Length || value.Sha256 != reference.Sha256)
            throw new StorageException("storage_artifact_integrity_failed", "The Artifact does not match its pinned integrity metadata.", 409);
    }

    private static async Task<FileStream> LockAsync(Target target, CancellationToken cancellationToken)
    {
        var path = LocalStoragePaths.Checked(Path.Combine(target.Directory, target.Key + ".lock"));
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new StorageException("storage_artifact_unavailable", "The Artifact content backend is unavailable.", 503); }
        }
    }

    private static Reference? Parse(string reference)
    {
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            if (reference.Length > 8192) throw new FormatException();
            return JsonSerializer.Deserialize<Reference>(Convert.FromBase64String(reference[Prefix.Length..]), Json)
                ?? throw new FormatException();
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        { throw new StorageException("storage_artifact_reference_invalid", "The Artifact Storage reference is invalid."); }
    }
}

public sealed class ArtifactStorageUsageProvider(IResourceStore resources, IRequestContextScopeFactory scopes) : IStorageUsageProvider
{
    public async Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress storage, CancellationToken cancellationToken = default)
    {
        using var system = scopes.PushSystem();
        var selected = await resources.GetExactAsync<StorageResource>(storage, cancellationToken);
        if (selected is null) return [];
        return (await resources.ListAllAsync<FlowRunArtifactResource>(ArtifactResourceKinds.FlowRunArtifact, cancellationToken))
            .Where(item => WorkspaceArtifactStorage.StorageReference(item.Value.Receipt.OpaqueReference) is { } reference
                && reference.Storage == storage && reference.StorageUid == selected.Value.Uid)
            .Select(item => new StorageUsage(item.Value.Kind, item.Value.Name, item.Value.Name,
                $"/artifacts/durable/{item.Value.ArtifactId}"))
            .Take(100).ToArray();
    }
}
