using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Atomic bounded local content and idempotency state, partitioned by exact Storage UID.</summary>
public sealed class LocalStorageObjectProvider(LocalStoragePaths paths, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumObjects = 1024;
    private const int MaximumReceipts = 512;
    private const int MaximumStoreBytes = 64 * 1024 * 1024;
    private sealed record Entry(AspProviderObject Item, byte[] Content);
    private sealed record Receipt(string Fingerprint, AspProviderObject Item, DateTimeOffset Expires);
    private sealed record State(Dictionary<string, Entry> Objects, Dictionary<string, Receipt> Receipts);

    public async Task<AspProviderObject> WriteAsync(StorageRevisionReference storage, AspAccessContext context,
        LocalContentWrite request, CancellationToken cancellationToken)
    {
        Validate(store: storage, context);
        AspValidation.Require(context.Policy.CanWrite, "forbidden", "Write is not authorized.");
        AspValidation.Require(request.Content.Length <= context.Policy.MaxWriteBytes, "limit_exceeded", "Write limit exceeded.");
        request = request with { Content = request.Content.ToArray() };
        var directory = paths.EnsureDirectoryFor(storage);
        await using var lease = await LockAsync(directory, cancellationToken);
        // Validate again after waiting for another process, at the commit boundary.
        AspValidation.RequestId(request.RequestId);
        AspValidation.DemandKey(context, request.Key);
        AspValidation.Deadline(request.NotAfter, clock);
        if (request.ExpectedVersion is not null) AspValidation.RequestId(request.ExpectedVersion);
        var state = await ReadStateAsync(directory, cancellationToken);
        var identity = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new
        { context.Store, context.Subject, context.AuthorizationRevision, request.RequestId }, Json));
        var fingerprint = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { context, request }, Json));
        if (state.Receipts.TryGetValue(identity, out var previous))
        {
            AspValidation.Require(previous.Fingerprint == fingerprint, "idempotency_conflict", "The request identity was reused.");
            return previous.Item;
        }
        foreach (var expired in state.Receipts.Where(r => r.Value.Expires <= clock.GetUtcNow()).Select(r => r.Key).ToArray())
            state.Receipts.Remove(expired);
        AspValidation.Require(state.Receipts.Count < MaximumReceipts, "capacity_exceeded", "The write receipt capacity has been reached.");
        state.Objects.TryGetValue(request.Key, out var current);
        AspValidation.Require(request.ExpectedVersion is null ? current is null : current?.Item.Version == request.ExpectedVersion,
            "conflict", "The object exists or the selected version is stale.");
        AspValidation.Require((current is not null || state.Objects.Count < MaximumObjects)
            && state.Objects.Values.Sum(v => v.Content.LongLength) - (current?.Content.Length ?? 0) + request.Content.Length <= MaximumStoreBytes,
            "capacity_exceeded", "The local store capacity has been reached.");
        var item = new AspProviderObject(request.Key, Guid.NewGuid().ToString("N"), request.Content.Length,
            AspValidation.Digest(request.Content));
        state.Objects[request.Key] = new(item, request.Content.ToArray());
        state.Receipts[identity] = new(fingerprint, item, request.NotAfter);
        await CommitAsync(directory, state, cancellationToken);
        return item;
    }

    public async Task<bool> RetireAsync(StorageRevisionReference storage, AspAccessContext context, StorageObject exact, CancellationToken cancellationToken)
    {
        Validate(storage, context);
        AspValidation.DemandKey(context, exact.Key);
        var directory = paths.EnsureDirectoryFor(storage);
        await using var lease = await LockAsync(directory, cancellationToken);
        var state = await ReadStateAsync(directory, cancellationToken);
        if (!state.Objects.TryGetValue(exact.Key, out var entry)) return true;
        AspValidation.Require(entry.Item.Version == exact.Version && entry.Item.Length == exact.Length && entry.Item.Sha256 == exact.Sha256,
            "version_unavailable", "The result no longer matches the exact retirement receipt.");
        state.Objects.Remove(exact.Key);
        await CommitAsync(directory, state, cancellationToken);
        return true;
    }

    public async Task<AspProviderPage> ListAsync(StorageRevisionReference storage, AspAccessContext context,
        string prefix, string? after, int limit, CancellationToken cancellationToken)
    {
        Validate(store: storage, context);
        AspValidation.Prefix(prefix);
        AspValidation.Require(context.Policy.CanList && prefix.StartsWith(context.Policy.Prefix, StringComparison.Ordinal),
            "forbidden", "The list scope is not authorized.");
        AspValidation.Require(limit > 0 && limit <= context.Policy.MaxPageSize, "limit_exceeded", "Invalid page size.");
        var state = await ReadStateAsync(paths.DirectoryFor(storage), cancellationToken);
        var page = state.Objects.Values.Select(e => e.Item)
            .Where(item => AspValidation.Allows(context.Policy, item.Key) && item.Key.StartsWith(prefix, StringComparison.Ordinal)
                && (after is null || string.CompareOrdinal(item.Key, after) > 0))
            .OrderBy(item => item.Key, StringComparer.Ordinal).Take(limit + 1).ToArray();
        return new(page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].Key : null);
    }

    public async Task<AspProviderObject> StatAsync(StorageRevisionReference storage, AspAccessContext context,
        string key, string? version, CancellationToken cancellationToken)
    {
        AspValidation.Require(context.Policy.CanStat, "forbidden", "Stat is not authorized.");
        return (await FindAsync(storage, context, key, version, cancellationToken)).Item;
    }

    public async Task<LocalContentRead> ReadAsync(StorageRevisionReference storage, AspAccessContext context,
        string key, string? version, long offset, int maximumBytes, CancellationToken cancellationToken)
    {
        AspValidation.Require(context.Policy.CanRead, "forbidden", "Read is not authorized.");
        AspValidation.Require(offset >= 0 && maximumBytes > 0 && maximumBytes <= context.Policy.MaxReadBytes,
            "limit_exceeded", "Invalid read range.");
        var entry = await FindAsync(storage, context, key, version, cancellationToken);
        AspValidation.Require(offset <= entry.Content.Length, "invalid_request", "Offset exceeds the object length.");
        var content = entry.Content.AsSpan((int)offset, (int)Math.Min(maximumBytes, entry.Content.Length - offset)).ToArray();
        return new(entry.Item, content, offset + content.Length == entry.Content.Length);
    }

    private static async Task<State> ReadStateAsync(string directory, CancellationToken cancellationToken)
    {
        var path = LocalStoragePaths.Checked(Path.Combine(directory, "objects.json"));
        if (!File.Exists(path)) return new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        AspValidation.Require(stream.Length <= MaximumStoreBytes * 2L, "storage_state_invalid", "The local storage state exceeds its bound.");
        var state = await JsonSerializer.DeserializeAsync<State>(stream, Json, cancellationToken)
            ?? throw new AspException("storage_state_invalid", "The local storage state is invalid.");
        AspValidation.Require(state.Objects is not null && state.Receipts is not null
            && state.Objects.Count <= MaximumObjects && state.Receipts.Count <= MaximumReceipts,
            "storage_state_invalid", "The local storage inventory is invalid.");
        foreach (var (key, entry) in state.Objects)
        {
            AspValidation.Item(entry.Item);
            AspValidation.Require(key == entry.Item.Key && entry.Item.Length == entry.Content.Length
                && AspValidation.Digest(entry.Content) == entry.Item.Sha256,
                "storage_state_invalid", "The local storage content failed its integrity check.");
        }
        return state;
    }

    private static async Task CommitAsync(string directory, State state, CancellationToken cancellationToken)
    {
        var destination = LocalStoragePaths.Checked(Path.Combine(directory, "objects.json"));
        var temporary = LocalStoragePaths.Checked(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp"));
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            LocalStoragePaths.Checked(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<FileStream> LockAsync(string directory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = LocalStoragePaths.Checked(Path.Combine(directory, "write.lock"));
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken); }
        }
    }

    private async Task<Entry> FindAsync(StorageRevisionReference storage, AspAccessContext context,
        string key, string? version, CancellationToken cancellationToken)
    {
        Validate(store: storage, context);
        AspValidation.DemandKey(context, key);
        var state = await ReadStateAsync(paths.DirectoryFor(storage), cancellationToken);
        var entry = state.Objects.GetValueOrDefault(key)
            ?? throw new AspException("not_found", "The object was not found.");
        AspValidation.Require(entry.Item.Version == version, "version_unavailable", "The selected object version is unavailable.");
        return entry;
    }

    private static void Validate(StorageRevisionReference store, AspAccessContext context)
    {
        AspValidation.Context(context);
        AspValidation.Require(context.Store.StoreId == store.StorageUid.ToString("N") && context.Store.Revision == store.BindingDigest,
            "forbidden", "The provider context does not match the exact logical Storage.");
    }
}

public sealed record LocalContentWrite(string RequestId, DateTimeOffset NotAfter, string Key, byte[] Content, string? ExpectedVersion);
public sealed record LocalContentRead(AspProviderObject Item, byte[] Content, bool EndOfObject);
