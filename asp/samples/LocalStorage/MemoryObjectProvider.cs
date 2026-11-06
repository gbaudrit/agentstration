using System.Text.Json;
using Agentstration.Asp.Abstractions;

namespace Asp.Samples.LocalStorage;

// Bounded, process-local demonstration. No durability/stable-version/retention claim.
// The dictionary may be shared by all bindings; the exact logical StoreRef partitions it.
public sealed class MemoryObjectProvider(TimeProvider clock) : IAspObjectReader, IAspObjectWriter
{
    private readonly object gate = new();
    private readonly Dictionary<(AspStoreRef Store, string Key), Entry> objects = [];
    private readonly Dictionary<(AspStoreRef Store, string Subject, string Authorization, string Id), Receipt> receipts = [];
    private sealed record Entry(AspProviderObject Metadata, byte[] Bytes);
    private sealed record Receipt(string Fingerprint, AspProviderObject Item, DateTimeOffset Expires);
    private long sequence;
    public string ProviderId => "sample.memory";
    public string ProviderVersion => "1.0.0";
    public JsonElement ConfigurationSchema => JsonSerializer.SerializeToElement(new
    { type = "object", properties = new Dictionary<string, object>(), additionalProperties = false });

    public ValueTask<AspProviderObject> WriteAsync(AspAccessContext context, AspProviderWrite request, CancellationToken cancellationToken)
    {
        AspValidation.Context(context);
        AspValidation.Require(context.Policy.CanWrite, "forbidden", "Write is not authorized.");
        AspValidation.DemandKey(context, request.Key);
        AspValidation.Write(new(request.RequestId, request.NotAfter, request.Key, Convert.ToBase64String(request.Content), request.ExpectedVersion), context, clock);
        AspValidation.Require(request.Content.Length <= context.Policy.MaxWriteBytes, "limit_exceeded", "Write limit exceeded.");
        var fingerprint = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { context, request }, AspProtocol.JsonOptions));
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Revalidate at the commit boundary, including time spent waiting for the lock.
            AspValidation.Write(new(request.RequestId, request.NotAfter, request.Key, "", request.ExpectedVersion), context, clock);
            var receiptKey = (context.Store, context.Subject, context.AuthorizationRevision, request.RequestId);
            if (receipts.TryGetValue(receiptKey, out var receipt))
            {
                AspValidation.Require(receipt.Fingerprint == fingerprint, "idempotency_conflict", "Request identity was reused with different arguments or scope.");
                return ValueTask.FromResult(receipt.Item);
            }
            foreach (var expired in receipts.Where(pair => pair.Value.Expires <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
                receipts.Remove(expired);
            AspValidation.Require(receipts.Count < 128, "capacity_exceeded", "The bounded write receipt ledger is full.");
            var objectKey = (context.Store, request.Key);
            objects.TryGetValue(objectKey, out var current);
            // null means create only; overwrites require the exact current version.
            AspValidation.Require(request.ExpectedVersion is null ? current is null : current?.Metadata.Version == request.ExpectedVersion,
                "conflict", "The object already exists or the expected version is stale.");
            AspValidation.Require((current is not null || objects.Count < 128)
                && objects.Values.Sum(entry => entry.Bytes.Length) - (current?.Bytes.Length ?? 0) + request.Content.Length <= 1_048_576,
                "capacity_exceeded", "The bounded sample store is full.");
            var metadata = new AspProviderObject(request.Key, (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.Content.Length, AspValidation.Digest(request.Content));
            // No cancellation or fallible I/O between the atomic content/receipt updates.
            objects[objectKey] = new(metadata, request.Content.ToArray());
            receipts[receiptKey] = new(fingerprint, metadata, request.NotAfter);
            return ValueTask.FromResult(metadata);
        }
    }

    public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken)
    {
        AspValidation.Context(context);
        AspValidation.Require(context.Policy.CanList && prefix.StartsWith(context.Policy.Prefix, StringComparison.Ordinal), "forbidden", "List scope is not authorized.");
        AspValidation.Require(limit > 0 && limit <= context.Policy.MaxPageSize, "limit_exceeded", "Invalid page size.");
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = objects.Where(pair => pair.Key.Store == context.Store && AspValidation.Allows(context.Policy, pair.Key.Key)
                    && pair.Key.Key.StartsWith(prefix, StringComparison.Ordinal) && (afterKey is null || string.CompareOrdinal(pair.Key.Key, afterKey) > 0))
                .Select(pair => pair.Value.Metadata).OrderBy(value => value.Key, StringComparer.Ordinal).Take(limit + 1).ToArray();
            return ValueTask.FromResult(new AspProviderPage(page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].Key : null));
        }
    }

    public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string version, CancellationToken cancellationToken)
    {
        AspValidation.Require(context.Policy.CanStat, "forbidden", "Stat is not authorized.");
        lock (gate) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Find(context, key, version).Metadata); }
    }

    public ValueTask<AspProviderRead> ReadAsync(AspAccessContext context, string key, string version, long offset, int maxBytes, CancellationToken cancellationToken)
    {
        AspValidation.Require(context.Policy.CanRead, "forbidden", "Read is not authorized.");
        AspValidation.Require(offset >= 0 && maxBytes > 0 && maxBytes <= context.Policy.MaxReadBytes, "limit_exceeded", "Invalid read range.");
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Find(context, key, version);
            AspValidation.Require(offset <= entry.Bytes.Length, "invalid_request", "Offset exceeds the object length.");
            var content = entry.Bytes.AsSpan((int)offset, (int)Math.Min(maxBytes, entry.Bytes.Length - offset)).ToArray();
            return ValueTask.FromResult(new AspProviderRead(entry.Metadata, content, offset + content.Length == entry.Bytes.Length));
        }
    }

    private Entry Find(AspAccessContext context, string key, string version)
    {
        AspValidation.Context(context);
        AspValidation.DemandKey(context, key);
        var entry = objects.GetValueOrDefault((context.Store, key)) ?? throw new AspException("not_found", "Object not found.");
        AspValidation.Require(entry.Metadata.Version == version, "version_unavailable", "The referenced version is no longer available.");
        return entry;
    }
}

// Second capability variant reuses the same reader and MCP SDK with no hand-written Tool schemas.
public sealed class ReadOnlyObjectProvider(IAspObjectReader inner) : IAspObjectReader
{
    public string ProviderId => inner.ProviderId;
    public string ProviderVersion => inner.ProviderVersion;
    public JsonElement ConfigurationSchema => inner.ConfigurationSchema;
    public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken) => inner.ListAsync(context, prefix, afterKey, limit, cancellationToken);
    public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string version, CancellationToken cancellationToken) => inner.StatAsync(context, key, version, cancellationToken);
    public ValueTask<AspProviderRead> ReadAsync(AspAccessContext context, string key, string version, long offset, int maxBytes, CancellationToken cancellationToken) => inner.ReadAsync(context, key, version, offset, maxBytes, cancellationToken);
}
