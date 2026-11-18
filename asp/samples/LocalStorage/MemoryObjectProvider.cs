using System.Text.Json;
using Agentstration.Asp.Abstractions;

namespace Asp.Samples.LocalStorage;

// Deterministic bounded sample backend. Protocol sessions/replays belong to Asp.Server.
public sealed class MemoryObjectProvider : IAspObjectReader, IAspObjectWriter
{
    private readonly object gate = new();
    private readonly Dictionary<(AspStoreRef Store, string Key), Entry> objects = [];
    private sealed record Entry(AspProviderObject Metadata, byte[] Bytes);
    private long sequence;
    public string ProviderId => "sample.memory";
    public string ProviderVersion => "1.0.0";
    public JsonElement ConfigurationSchema => JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
    public AspTransferCapabilities Transfers => new(Replace: true, Append: true, VersionCondition: true,
        ETagConditions: true, Metadata: true, AtomicPublication: true, MaximumObjectBytes: 1_048_576, DefaultChunkSizeBytes: 3);

    public ValueTask<IAspWriteHandle> OpenWriteAsync(AspAccessContext context, AspWriteOptions options, CancellationToken cancellationToken)
    {
        Validate(context, options.Key); cancellationToken.ThrowIfCancellationRequested();
        AspValidation.Require(context.Policy.CanWrite, "forbidden", "Write is not authorized.");
        return ValueTask.FromResult<IAspWriteHandle>(new Writer(this, context, options));
    }
    private AspProviderObject Commit(AspAccessContext context, AspWriteOptions options, byte[] content, CancellationToken ct)
    {
        Validate(context, options.Key);
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            objects.TryGetValue((context.Store, options.Key), out var current);
            AspValidation.Require((options.Mode != "create" || current is null)
                && (options.ExpectedVersion is null || current?.Metadata.Version == options.ExpectedVersion)
                && (options.IfMatch is null || current?.Metadata.Version == options.IfMatch)
                && (options.IfNoneMatch is null || (options.IfNoneMatch == "*" ? current is null : current?.Metadata.Version != options.IfNoneMatch)),
                "conflict", "The native conditional write was refused.");
            if (options.Mode == "append" && current is not null) content = current.Bytes.Concat(content).ToArray();
            AspValidation.Require(content.Length <= Transfers.MaximumObjectBytes && (current is not null || objects.Count < 128)
                && objects.Values.Sum(e => e.Bytes.Length) - (current?.Bytes.Length ?? 0) + content.Length <= 1_048_576,
                "capacity_exceeded", "The bounded sample backend is full.");
            var version = (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var metadata = new AspProviderObject(options.Key, version, content.Length, AspValidation.Digest(content), version, options.Metadata ?? (options.Mode == "append" ? current?.Metadata.Metadata : null));
            objects[(context.Store, options.Key)] = new(metadata, content);
            return metadata;
        }
    }
    public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken cancellationToken)
    {
        AspValidation.Context(context); AspValidation.Prefix(prefix);
        AspValidation.Require(context.Policy.CanList && prefix.StartsWith(context.Policy.Prefix, StringComparison.Ordinal), "forbidden", "List scope is not authorized.");
        AspValidation.Require(limit > 0 && limit <= context.Policy.MaxPageSize, "limit_exceeded", "Invalid page size.");
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = objects.Where(p => p.Key.Store == context.Store && AspValidation.Allows(context.Policy, p.Key.Key)
                && p.Key.Key.StartsWith(prefix, StringComparison.Ordinal) && (afterKey is null || string.CompareOrdinal(p.Key.Key, afterKey) > 0))
                .Select(p => p.Value.Metadata).OrderBy(v => v.Key, StringComparer.Ordinal).Take(limit + 1).ToArray();
            return ValueTask.FromResult(new AspProviderPage(page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].Key : null));
        }
    }
    public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string? version, string? etag, CancellationToken cancellationToken)
    {
        AspValidation.Require(context.Policy.CanStat, "forbidden", "Stat is not authorized.");
        lock (gate) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Find(context, key, version, etag).Metadata); }
    }
    public ValueTask<AspReadHandle> OpenReadAsync(AspAccessContext context, string key, string? version, string? etag, AspByteRange? range, CancellationToken cancellationToken)
    {
        AspValidation.Require(context.Policy.CanRead, "forbidden", "Read is not authorized.");
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Find(context, key, version, etag);
            var offset = range?.Offset ?? 0;
            AspValidation.Require(offset >= 0 && offset <= entry.Bytes.Length && range?.Length is null or >= 0, "invalid_range", "Invalid byte range.");
            var length = Math.Min(range?.Length ?? long.MaxValue, entry.Bytes.Length - offset);
            return ValueTask.FromResult(new AspReadHandle(entry.Metadata, new MemoryStream(entry.Bytes, (int)offset, (int)length, writable: false), offset, length));
        }
    }
    private Entry Find(AspAccessContext context, string key, string? version, string? etag)
    {
        Validate(context, key);
        var entry = objects.GetValueOrDefault((context.Store, key)) ?? throw new AspException("not_found", "Object not found.");
        AspValidation.Require((version is null || entry.Metadata.Version == version) && (etag is null || entry.Metadata.ETag == etag), "version_unavailable", "The selected version is no longer available.");
        return entry;
    }
    private static void Validate(AspAccessContext context, string key) { AspValidation.Context(context); AspValidation.DemandKey(context, key); }
    private sealed class Writer(MemoryObjectProvider owner, AspAccessContext context, AspWriteOptions options) : IAspWriteHandle
    {
        private readonly MemoryStream buffer = new();
        private bool ended;
        public Stream Content => buffer;
        public ValueTask<AspProviderObject> CompleteAsync(CancellationToken ct)
        {
            AspValidation.Require(!ended, "conflict", "The native writer has already ended.");
            var result = owner.Commit(context, options, buffer.ToArray(), ct); ended = true;
            return ValueTask.FromResult(result);
        }
        public ValueTask AbortAsync(CancellationToken ct) { ended = true; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { ended = true; return buffer.DisposeAsync(); }
    }
}

public sealed class ReadOnlyObjectProvider(IAspObjectReader inner) : IAspObjectReader
{
    public string ProviderId => inner.ProviderId;
    public string ProviderVersion => inner.ProviderVersion;
    public JsonElement ConfigurationSchema => inner.ConfigurationSchema;
    public AspTransferCapabilities Transfers => inner.Transfers;
    public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken ct) => inner.ListAsync(context, prefix, afterKey, limit, ct);
    public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string? version, string? etag, CancellationToken ct) => inner.StatAsync(context, key, version, etag, ct);
    public ValueTask<AspReadHandle> OpenReadAsync(AspAccessContext context, string key, string? version, string? etag, AspByteRange? range, CancellationToken ct) => inner.OpenReadAsync(context, key, version, etag, range, ct);
}
