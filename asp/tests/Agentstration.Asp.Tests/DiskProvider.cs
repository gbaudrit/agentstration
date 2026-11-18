using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Asp.Abstractions;

namespace Agentstration.Asp.Tests;

/// <summary>Real file-backed conformance fixture; only the SDK owns protocol state.</summary>
internal sealed class DiskProvider : IAspObjectReader, IAspObjectWriter, IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "asp-session-disk-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<(AspStoreRef, string), AspProviderObject> entries = [];
    private readonly object gate = new();
    public string ProviderId => "fixture.disk";
    public string ProviderVersion => "1.0.0";
    public JsonElement ConfigurationSchema => JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
    public AspTransferCapabilities Transfers => new(Replace: true, VersionCondition: true, AtomicPublication: true,
        MaximumObjectBytes: 64 * 1024 * 1024, DefaultChunkSizeBytes: 8192);
    public ValueTask<IAspWriteHandle> OpenWriteAsync(AspAccessContext context, AspWriteOptions options, CancellationToken ct)
    {
        AspValidation.Context(context); AspValidation.DemandKey(context, options.Key); ct.ThrowIfCancellationRequested();
        AspValidation.Require(context.Policy.CanWrite, "forbidden", "Write is not authorized.");
        Directory.CreateDirectory(directory);
        return ValueTask.FromResult<IAspWriteHandle>(new Writer(this, context, options));
    }
    private string PathFor(AspStoreRef store, string key) => Path.Combine(directory, AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { store, key }, AspProtocol.JsonOptions)));
    public ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string? version, string? etag, CancellationToken ct)
    {
        AspValidation.Context(context); AspValidation.DemandKey(context, key); ct.ThrowIfCancellationRequested();
        AspValidation.Require(context.Policy.CanStat, "forbidden", "Stat is not authorized.");
        lock (gate) return ValueTask.FromResult(Lookup(context, key, version, etag));
    }
    private AspProviderObject Lookup(AspAccessContext context, string key, string? version, string? etag)
    {
        var item = entries.GetValueOrDefault((context.Store, key)) ?? throw new AspException("not_found", "Not found.");
        AspValidation.Require((version is null || version == item.Version) && (etag is null || etag == item.ETag), "version_unavailable", "Version changed.");
        return item;
    }
    public ValueTask<AspReadHandle> OpenReadAsync(AspAccessContext context, string key, string? version, string? etag, AspByteRange? range, CancellationToken ct)
    {
        AspValidation.Require(context.Policy.CanRead, "forbidden", "Read is not authorized.");
        AspValidation.Context(context); AspValidation.DemandKey(context, key); ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var item = Lookup(context, key, version, etag);
            var offset = range?.Offset ?? 0;
            AspValidation.Require(offset >= 0 && offset <= item.Length, "invalid_range", "Invalid byte range.");
            var input = new FileStream(PathFor(context.Store, key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous);
            input.Position = offset;
            return ValueTask.FromResult(new AspReadHandle(item, input, offset, Math.Min(range?.Length ?? long.MaxValue, item.Length - offset)));
        }
    }
    public ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken ct)
    {
        AspValidation.Context(context); ct.ThrowIfCancellationRequested();
        AspValidation.Require(context.Policy.CanList, "forbidden", "List is not authorized.");
        lock (gate)
        {
            var page = entries.Where(p => p.Key.Item1 == context.Store && AspValidation.Allows(context.Policy, p.Key.Item2) && p.Key.Item2.StartsWith(prefix, StringComparison.Ordinal)
                && (afterKey is null || string.CompareOrdinal(p.Key.Item2, afterKey) > 0)).Select(p => p.Value).OrderBy(p => p.Key, StringComparer.Ordinal).Take(limit + 1).ToArray();
            return ValueTask.FromResult(new AspProviderPage(page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].Key : null));
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class Writer : IAspWriteHandle
    {
        private readonly DiskProvider owner;
        private readonly AspAccessContext context;
        private readonly AspWriteOptions options;
        private readonly string temporary;
        private readonly FileStream output;
        private bool finished;
        public Writer(DiskProvider owner, AspAccessContext context, AspWriteOptions options)
        {
            this.owner = owner; this.context = context; this.options = options;
            temporary = Path.Combine(owner.directory, Guid.NewGuid().ToString("N") + ".tmp");
            output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
        }
        public Stream Content => output;
        public async ValueTask<AspProviderObject> CompleteAsync(CancellationToken ct)
        {
            var length = output.Length;
            await output.FlushAsync(ct); await output.DisposeAsync();
            lock (owner.gate)
            {
                ct.ThrowIfCancellationRequested();
                owner.entries.TryGetValue((context.Store, options.Key), out var current);
                AspValidation.Require((options.Mode != "create" || current is null) && (options.ExpectedVersion is null || current?.Version == options.ExpectedVersion), "conflict", "Conditional publication refused.");
                var item = new AspProviderObject(options.Key, Guid.NewGuid().ToString("N"), length, null);
                File.Move(temporary, owner.PathFor(context.Store, options.Key), overwrite: true);
                owner.entries[(context.Store, options.Key)] = item; finished = true; return item;
            }
        }
        public async ValueTask AbortAsync(CancellationToken ct) { await output.DisposeAsync(); if (File.Exists(temporary)) File.Delete(temporary); finished = true; }
        public async ValueTask DisposeAsync() { if (!finished) await AbortAsync(CancellationToken.None); }
    }
}
