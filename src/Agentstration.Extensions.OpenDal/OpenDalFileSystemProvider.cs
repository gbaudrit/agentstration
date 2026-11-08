using System.Text;
using System.Text.Json;
using Agentstration.Asp.Abstractions;
using OpenDAL;

namespace Agentstration.Extensions.OpenDal;

/// <summary>
/// Bounded filesystem state through OpenDAL. One atomic state object contains content and receipts;
/// the operator-owned file lock serializes cooperating processes. Logical keys never become paths.
/// </summary>
public sealed class OpenDalFileSystemProvider(string root, AspStoreRef store, TimeProvider clock,
    OpenDalExecutionGate execution) : IAspObjectReader, IAspObjectWriter
{
    public const int MaximumObjects = 128;
    public const int MaximumReceipts = 512;
    public const int MaximumContentBytes = 8 * 1024 * 1024;
    private const int MaximumStateBytes = 16 * 1024 * 1024;
    private const string StateKey = "objects.json";
    private sealed record Entry(AspProviderObject Item, byte[] Content);
    private sealed record Receipt(string Fingerprint, AspProviderObject Item, DateTimeOffset Expires);
    private sealed record State(int Format, Dictionary<string, Entry> Objects, Dictionary<string, Receipt> Receipts);
    public string ProviderId => OpenDalStorageTypes.FileSystemId;
    public string ProviderVersion => OpenDalStorageTypes.Version;
    public JsonElement ConfigurationSchema => OpenDalStorageTypes.ConfigurationSchema;

    public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit,
        CancellationToken cancellationToken)
    {
        Validate(context);
        AspValidation.Prefix(prefix);
        AspValidation.Require(context.Policy.CanList && prefix.StartsWith(context.Policy.Prefix, StringComparison.Ordinal),
            "forbidden", "The list scope is not authorized.");
        AspValidation.Require(limit > 0 && limit <= context.Policy.MaxPageSize, "limit_exceeded", "Invalid page size.");
        if (afterKey is not null) AspValidation.DemandKey(context, afterKey);
        return ExecuteAsync(context, (_, state) =>
        {
            var page = state.Objects.Values.Select(e => e.Item)
                .Where(item => AspValidation.Allows(context.Policy, item.Key) && item.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && (afterKey is null || string.CompareOrdinal(item.Key, afterKey) > 0))
                .OrderBy(item => item.Key, StringComparer.Ordinal).Take(limit + 1).ToArray();
            return new AspProviderPage(page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].Key : null);
        }, cancellationToken);
    }

    public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string version, CancellationToken cancellationToken)
    {
        Validate(context);
        AspValidation.Require(context.Policy.CanStat, "forbidden", "Stat is not authorized.");
        AspValidation.DemandKey(context, key);
        return ExecuteAsync(context, (_, state) => Find(state, key, version).Item, cancellationToken);
    }

    public ValueTask<AspProviderRead> ReadAsync(AspAccessContext context, string key, string version, long offset, int maxBytes,
        CancellationToken cancellationToken)
    {
        Validate(context);
        AspValidation.Require(context.Policy.CanRead, "forbidden", "Read is not authorized.");
        AspValidation.DemandKey(context, key);
        AspValidation.Require(offset >= 0 && maxBytes > 0 && maxBytes <= context.Policy.MaxReadBytes,
            "limit_exceeded", "Invalid read range.");
        return ExecuteAsync(context, (_, state) =>
        {
            var entry = Find(state, key, version);
            AspValidation.Require(offset <= entry.Content.Length, "invalid_request", "Offset exceeds the object length.");
            var content = entry.Content.AsSpan((int)offset, (int)Math.Min(maxBytes, entry.Content.Length - offset)).ToArray();
            return new AspProviderRead(entry.Item, content, offset + content.Length == entry.Content.Length);
        }, cancellationToken);
    }

    public ValueTask<AspProviderObject> WriteAsync(AspAccessContext context, AspProviderWrite request, CancellationToken cancellationToken)
    {
        Validate(context);
        AspValidation.Require(context.Policy.CanWrite, "forbidden", "Write is not authorized.");
        AspValidation.Require(request.Content.Length <= context.Policy.MaxWriteBytes, "limit_exceeded", "Write limit exceeded.");
        AspValidation.Write(new(request.RequestId, request.NotAfter, request.Key, "", request.ExpectedVersion), context, clock);
        request = request with { Content = request.Content.ToArray() };
        return ExecuteAsync(context, (op, state) =>
        {
            // Recheck the original deadline after waiting for the cross-process commit lock.
            AspValidation.Write(new(request.RequestId, request.NotAfter, request.Key, "", request.ExpectedVersion), context, clock);
            var identity = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new
            { context.Store, context.Subject, context.AuthorizationRevision, request.RequestId }, AspProtocol.JsonOptions));
            var fingerprint = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { context, request }, AspProtocol.JsonOptions));
            if (state.Receipts.TryGetValue(identity, out var previous))
            {
                AspValidation.Require(previous.Fingerprint == fingerprint, "idempotency_conflict", "The write identity was reused.");
                return previous.Item;
            }
            foreach (var expired in state.Receipts.Where(r => r.Value.Expires <= clock.GetUtcNow()).Select(r => r.Key).ToArray())
                state.Receipts.Remove(expired);
            AspValidation.Require(state.Receipts.Count < MaximumReceipts, "capacity_exceeded", "The write receipt capacity has been reached.");
            state.Objects.TryGetValue(request.Key, out var current);
            AspValidation.Require(request.ExpectedVersion is null ? current is null : current?.Item.Version == request.ExpectedVersion,
                "conflict", "The object exists or the selected version is stale.");
            AspValidation.Require((current is not null || state.Objects.Count < MaximumObjects)
                && state.Objects.Values.Sum(e => e.Content.LongLength) - (current?.Content.Length ?? 0) + request.Content.Length <= MaximumContentBytes,
                "capacity_exceeded", "The filesystem store capacity has been reached.");
            var item = new AspProviderObject(request.Key, Guid.NewGuid().ToString("N"), request.Content.Length, AspValidation.Digest(request.Content));
            state.Objects[request.Key] = new(item, request.Content);
            state.Receipts[identity] = new(fingerprint, item, request.NotAfter);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, AspProtocol.JsonOptions);
            AspValidation.Require(bytes.Length <= MaximumStateBytes, "capacity_exceeded", "The filesystem state exceeds its bound.");
            cancellationToken.ThrowIfCancellationRequested();
            // Native async cancellation only cancels observation. Keep the synchronous, bounded fs commit
            // inside the exclusive lock until OpenDAL's atomic rename has returned.
            op.Write(StateKey, bytes);
            cancellationToken.ThrowIfCancellationRequested(); // A cancelled response may have committed; exact retry reconciles.
            return item;
        }, cancellationToken);
    }

    private async ValueTask<T> ExecuteAsync<T>(AspAccessContext context, Func<Operator, State, T> operation,
        CancellationToken cancellationToken)
    {
        Validate(context);
        cancellationToken.ThrowIfCancellationRequested();
        await execution.Slots.WaitAsync(cancellationToken);
        try
        {
            var directory = EnsureDirectory();
            await using var lease = await LockAsync(directory, cancellationToken);
            Checked(directory);
            var temporary = Checked(Path.Combine(directory, "tmp"));
            CreatePrivateDirectory(temporary);
            Checked(Path.Combine(directory, StateKey));
            using var op = new Operator("fs", new Dictionary<string, string>
            { ["root"] = directory, ["atomic_write_dir"] = temporary });
            var state = ReadState(op);
            cancellationToken.ThrowIfCancellationRequested();
            return operation(op, state);
        }
        catch (OpenDALException) { throw new AspException("provider_failure", "The OpenDAL filesystem operation could not complete; write outcome may be unknown."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { throw new AspException("provider_failure", "The filesystem state is unavailable or invalid; write outcome may be unknown."); }
        finally { execution.Slots.Release(); }
    }

    private void Validate(AspAccessContext context)
    {
        AspValidation.Context(context);
        AspValidation.Require(context.Store == store, "forbidden", "The exact logical Storage differs from the configured binding.");
    }

    private static Entry Find(State state, string key, string version)
    {
        var entry = state.Objects.GetValueOrDefault(key) ?? throw new AspException("not_found", "The object was not found.");
        AspValidation.Require(entry.Item.Version == version, "version_unavailable", "The selected object version is unavailable.");
        return entry;
    }

    private static State ReadState(Operator op)
    {
        if (!op.Exists(StateKey)) return new(1, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        AspValidation.Require(op.Stat(StateKey).ContentLength <= MaximumStateBytes, "invalid_result", "The filesystem state exceeds its bound.");
        var bytes = op.Read(StateKey);
        AspValidation.Require(bytes.Length <= MaximumStateBytes, "invalid_result", "The filesystem state exceeds its bound.");
        var state = JsonSerializer.Deserialize<State>(bytes, AspProtocol.JsonOptions);
        AspValidation.Require(state is { Format: 1, Objects: not null, Receipts: not null }
            && state.Objects.Count <= MaximumObjects && state.Receipts.Count <= MaximumReceipts,
            "invalid_result", "The filesystem inventory is invalid.");
        long size = 0;
        foreach (var (key, entry) in state.Objects)
        {
            AspValidation.Require(entry is { Item: not null, Content: not null }, "invalid_result", "The filesystem entry is invalid.");
            AspValidation.Item(entry.Item);
            AspValidation.Require(key == entry.Item.Key && entry.Content.Length <= AspProtocol.MaxContentBytes
                && entry.Item.Length == entry.Content.Length && entry.Item.Sha256 == AspValidation.Digest(entry.Content),
                "invalid_result", "The filesystem content failed its integrity check.");
            size += entry.Content.Length;
        }
        AspValidation.Require(size <= MaximumContentBytes, "invalid_result", "The filesystem content exceeds its bound.");
        foreach (var receipt in state.Receipts.Values)
        {
            AspValidation.Require(receipt is { Item: not null, Fingerprint.Length: 64 },
                "invalid_result", "The filesystem receipt is invalid.");
            AspValidation.Item(receipt.Item);
        }
        return state;
    }

    private string EnsureDirectory()
    {
        AspValidation.Store(store);
        AspValidation.Require(Path.IsPathFullyQualified(root), "invalid_request", "An operator-owned absolute root is required.");
        var partition = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { store.StoreId, store.BindingId }, AspProtocol.JsonOptions));
        var directory = Checked(Path.Combine(root, partition));
        CreatePrivateDirectory(directory);
        return Checked(directory);
    }

    private static void CreatePrivateDirectory(string path)
    {
        Checked(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Checked(path);
    }

    private static string Checked(string path)
    {
        var absolute = Path.GetFullPath(path);
        for (var current = absolute; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var linked = File.Exists(current) || Directory.Exists(current)
                ? (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
                : new FileInfo(current).LinkTarget is not null;
            AspValidation.Require(!linked, "forbidden", "The configured filesystem location contains an unsupported link.");
        }
        return absolute;
    }

    private static async Task<FileStream> LockAsync(string directory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Checked(Path.Combine(directory, "write.lock"));
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken); }
        }
    }
}
