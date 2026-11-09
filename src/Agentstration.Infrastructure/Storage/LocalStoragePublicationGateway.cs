using System.Security.Cryptography;
using Agentstration.Asp.Abstractions;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Local retained publications use a separate namespace from mutable ASP objects.</summary>
public sealed class LocalStoragePublicationGateway(IStorageResolver resolver, IStorageContentGateway content,
    LocalStoragePaths paths, ICurrentRequestContext current, IAuthorizationService authorization, TimeProvider clock) : IStoragePublicationGateway
{
    private const int MaximumPublications = 1024;
    private const int MaximumContentBytes = 65536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Document(StoragePublicationReference Reference, byte[] Content);

    public Task ValidateAsync(StorageContentRequest target, CancellationToken cancellationToken) =>
        GuardAsync(async () => { await ValidateCoreAsync(target, cancellationToken); return true; });
    public Task<StoragePublicationReference> PrepareAsync(StorageContentRequest target, string publicationId,
        ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        GuardAsync(() => PrepareCoreAsync(target, publicationId, bytes, cancellationToken));
    public Task SealAsync(StoragePublicationReference reference, CancellationToken cancellationToken) =>
        GuardAsync(async () => { await SealCoreAsync(reference, cancellationToken); return true; });
    public Task<ReadOnlyMemory<byte>> ReadAsync(StoragePublicationReference reference, long offset, int length,
        CancellationToken cancellationToken) => GuardAsync(() => ReadCoreAsync(reference, offset, length, cancellationToken));

    private async Task ValidateCoreAsync(StorageContentRequest target, CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(target.Storage, target.Consumer, cancellationToken);
        if (resolved.Type.Type.ScopeRef != Agentstration.Resources.ResourceScopeRef.Instance
            || resolved.Type.Type.Name != StoragePlatformResourceProvisioner.LocalTypeName
            || resolved.Type.Type.Namespace is { } ns && ns != Agentstration.Resources.ResourceNamespace.Default
            || resolved.Type.Version != StoragePlatformResourceProvisioner.LocalTypeVersion)
            throw Error("storage_publication_unsupported", "This Storage has no retained publication implementation.");
        await content.ValidateAsync(target, cancellationToken);
        if (!target.Policy.Write || !target.Policy.Read || !target.Policy.Stat)
            throw Error("storage_publication_policy_invalid", "Publication requires read, stat and write permissions.");
    }

    private async Task<StoragePublicationReference> PrepareCoreAsync(StorageContentRequest target, string publicationId,
        ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await ValidateAsync(target, cancellationToken);
        await authorization.EnsurePermissionAsync(current.Current, AuthorizationPermissions.RunsExecute, cancellationToken);
        var resolved = await resolver.ResolveAsync(target.Storage, target.Consumer, cancellationToken);
        var envelope = resolved.Envelope.Deserialize<StorageContentPolicy>(Json)!;
        if (bytes.Length > Math.Min(Math.Min(MaximumContentBytes, target.Policy.MaximumWriteBytes),
                Math.Min(resolved.Limits.MaximumWriteBytes, envelope.MaximumWriteBytes))
            || string.IsNullOrWhiteSpace(publicationId) || publicationId.Length > 256)
            throw Error("storage_publication_limit", "The publication exceeds its bounds.");
        var key = target.Policy.Prefix + "publications/" + current.Current.WorkspaceId.ToString("N") + "/"
            + Hash(Encoding.UTF8.GetBytes(publicationId));
        if (!AllowsKey(target.Policy, key) || !AllowsKey(envelope, key))
            throw Error("storage_publication_forbidden", "The publication is outside the authorized key range.");
        target = target with { Policy = target.Policy with
        {
            MaximumReadBytes = Math.Min(target.Policy.MaximumReadBytes, Math.Min(resolved.Limits.MaximumReadBytes, envelope.MaximumReadBytes)),
            MaximumWriteBytes = Math.Min(target.Policy.MaximumWriteBytes, Math.Min(resolved.Limits.MaximumWriteBytes, envelope.MaximumWriteBytes)),
            MaximumPageSize = Math.Min(target.Policy.MaximumPageSize, Math.Min(resolved.Limits.MaximumPageSize, envelope.MaximumPageSize))
        } };
        var reference = new StoragePublicationReference(target, key, Hash(bytes.Span), bytes.Length, Hash(bytes.Span))
        {
            Binding = new(resolved.Type, resolved.ConnectedService, resolved.ConnectedServiceType,
                Hash(JsonSerializer.SerializeToUtf8Bytes(resolved.Bindings, Json)),
                Hash(JsonSerializer.SerializeToUtf8Bytes(new { resolved.Capabilities, resolved.Limits }, Json)),
                "storage.local.retained-publication/v1")
        };
        var directory = DirectoryFor(reference);
        Directory.CreateDirectory(directory);
        await using var gate = await LockAsync(directory, cancellationToken);
        // Unsealed crash leftovers are never referenced by a published Snapshot. Cleanup work is bounded.
        foreach (var stale in Directory.EnumerateFiles(directory).Where(file => file.EndsWith(".tmp", StringComparison.Ordinal)
            || file.EndsWith(".pending.json", StringComparison.Ordinal)).Take(128)
            .Where(file => File.GetLastWriteTimeUtc(file) < clock.GetUtcNow().UtcDateTime.AddDays(-1)).Take(16))
            File.Delete(LocalStoragePaths.Checked(stale));
        var sealedPath = FileFor(reference, false);
        var pendingPath = FileFor(reference, true);
        if (File.Exists(sealedPath)) { _ = await LoadAsync(reference, sealedPath, cancellationToken); return reference; }
        if (File.Exists(pendingPath)) { _ = await LoadAsync(reference, pendingPath, cancellationToken); return reference; }
        if (Directory.EnumerateFiles(directory, "*.json").Take(MaximumPublications).Count() >= MaximumPublications)
            throw Error("storage_publication_capacity", "The retained publication capacity has been reached.");
        var temporary = LocalStoragePaths.Checked(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp"));
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(reference, bytes.ToArray()), Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, pendingPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return reference;
    }

    private async Task SealCoreAsync(StoragePublicationReference reference, CancellationToken cancellationToken)
    {
        await ValidateAsync(reference.Target, cancellationToken);
        await authorization.EnsurePermissionAsync(current.Current, AuthorizationPermissions.RunsExecute, cancellationToken);
        var directory = DirectoryFor(reference);
        await using var gate = await LockAsync(directory, cancellationToken);
        var destination = FileFor(reference, false);
        if (File.Exists(destination)) { _ = await LoadAsync(reference, destination, cancellationToken); return; }
        var pending = FileFor(reference, true);
        _ = await LoadAsync(reference, pending, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(pending, destination); // Never overwrite sealed bytes.
    }

    private async Task<ReadOnlyMemory<byte>> ReadCoreAsync(StoragePublicationReference reference, long offset, int length,
        CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(reference.Target.Storage, reference.Target.Consumer, cancellationToken);
        var envelope = resolved.Envelope.Deserialize<StorageContentPolicy>(Json)!;
        await content.ValidateAsync(reference.Target with { Policy = reference.Target.Policy with { Write = false } }, cancellationToken);
        await authorization.EnsurePermissionAsync(current.Current, AuthorizationPermissions.RunsRead, cancellationToken);
        if (offset < 0 || offset > reference.Length || length < 1
            || length > Math.Min(Math.Min(MaximumContentBytes, reference.Target.Policy.MaximumReadBytes),
                Math.Min(resolved.Limits.MaximumReadBytes, envelope.MaximumReadBytes)))
            throw Error("storage_publication_range_invalid", "The publication read exceeds its bounds.");
        var document = await LoadAsync(reference, FileFor(reference, false), cancellationToken);
        return document.Content.AsMemory((int)offset, (int)Math.Min(length, document.Content.Length - offset));
    }

    private string DirectoryFor(StoragePublicationReference reference) => LocalStoragePaths.Checked(Path.Combine(
        paths.EnsureDirectoryFor(reference.Target.Storage), "publications", current.Current.WorkspaceId.ToString("N")));
    private string FileFor(StoragePublicationReference reference, bool pending)
    {
        var expectedPrefix = reference.Target.Policy.Prefix + "publications/" + current.Current.WorkspaceId.ToString("N") + "/";
        if (!reference.Key.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || reference.Key[expectedPrefix.Length..] is not { Length: 64 } identity || !identity.All(char.IsAsciiHexDigit))
            throw Error("storage_publication_reference_invalid", "The publication belongs to another scope.");
        return LocalStoragePaths.Checked(Path.Combine(DirectoryFor(reference), identity + (pending ? ".pending.json" : ".json")));
    }
    private static async Task<Document> LoadAsync(StoragePublicationReference reference, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw Error("storage_publication_unavailable", "The exact publication is unavailable.");
        await using var stream = File.OpenRead(LocalStoragePaths.Checked(path));
        if (stream.Length > MaximumContentBytes * 3) throw Error("storage_publication_integrity", "Invalid publication size.");
        var value = await JsonSerializer.DeserializeAsync<Document>(stream, Json, cancellationToken);
        if (value?.Content is null || value.Content.Length != reference.Length || Hash(value.Content) != reference.Sha256
            || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(value.Reference, Json), JsonSerializer.SerializeToElement(reference, Json)))
            throw Error("storage_publication_integrity", "The publication does not match its retained evidence.");
        return value;
    }
    private static async Task<FileStream> LockAsync(string directory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(LocalStoragePaths.Checked(Path.Combine(directory, "publication.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(20, cancellationToken); }
        }
    }
    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or AspException)
        { throw Error("storage_publication_unavailable", "The exact publication backend is unavailable or invalid."); }
    }
    private static bool AllowsKey(StorageContentPolicy policy, string key) => key.StartsWith(policy.Prefix, StringComparison.Ordinal)
        && (policy.KeyFrom is null || string.CompareOrdinal(key, policy.KeyFrom) >= 0)
        && (policy.KeyBefore is null || string.CompareOrdinal(key, policy.KeyBefore) < 0);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static StorageException Error(string code, string message) => new(code, message, 409);
}
