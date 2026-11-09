using System.Text.Json;
using Agentstration.Asp.Abstractions;
using OpenDAL;
using OpenDAL.Layer;
using OpenDAL.Options;

namespace Agentstration.Extensions.OpenDal;

/// <summary>ASP-to-OpenDAL adapter. The SDK owns protocol sessions; OpenDAL owns physical content.</summary>
public sealed class OpenDalObjectProvider : IAspObjectReader, IAspObjectWriter
{
    private readonly AspStoreRef store;
    private readonly Func<Operator> create;
    private readonly Action<string>? checkKey;
    private readonly OpenDalExecutionGate execution;
    private readonly Capability capability;
    private readonly Func<string, string>? encodeKey;
    private readonly Func<string, string?>? decodeKey;
    public string ProviderId { get; }
    public string ProviderVersion => OpenDalStorageTypes.Version;
    public JsonElement ConfigurationSchema { get; }
    public AspTransferCapabilities Transfers { get; }
    public AspCapabilities Capabilities => new(capability.List, capability.Stat, capability.Read, capability.Write, false, false, false);

    public OpenDalObjectProvider(AspStoreRef store, string providerId, JsonElement schema, Func<Operator> create,
        OpenDalExecutionGate execution, Action<string>? checkKey = null,
        Func<string, string>? encodeKey = null, Func<string, string?>? decodeKey = null)
    {
        this.store = store; ProviderId = providerId; ConfigurationSchema = schema;
        this.create = create; this.execution = execution; this.checkKey = checkKey;
        this.encodeKey = encodeKey; this.decodeKey = decodeKey;
        using var op = create(); capability = op.Info.Capability;
        // The 0.3.1 Stream completion API returns no native metadata. Append cannot give the
        // exact resulting object length, and version write conditions have no managed option.
        Transfers = new(ProgressiveRead: capability.Read, ProgressiveWrite: capability.WriteCanMulti,
            Replace: capability.Write, Append: false, VersionCondition: false,
            ETagConditions: capability.WriteWithIfMatch && capability.WriteWithIfNoneMatch,
            Metadata: capability.WriteWithContentType && capability.WriteWithContentEncoding
                && capability.WriteWithContentDisposition && capability.WriteWithCacheControl && capability.WriteWithUserMetadata,
            AtomicPublication: false,
            MaximumObjectBytes: capability.WriteTotalMaxSize is { } maximum ? (long)Math.Min(maximum, (ulong)long.MaxValue) : long.MaxValue,
            DefaultChunkSizeBytes: 16_384, CreateOnly: capability.WriteWithIfNotExists, EmptyWrite: capability.WriteCanEmpty);
    }

    public async ValueTask<AspProviderPage> ListAsync(AspAccessContext context, string prefix, string? afterKey, int limit, CancellationToken ct)
    {
        Validate(context); AspValidation.Prefix(prefix);
        AspValidation.Require(context.Policy.CanList && capability.List && prefix.StartsWith(context.Policy.Prefix, StringComparison.Ordinal), "forbidden", "List is not authorized.");
        AspValidation.Require(limit > 0 && limit <= context.Policy.MaxPageSize, "limit_exceeded", "Invalid page size.");
        // List() in binding 0.3.1 materializes native entries. Limit is a backend page hint,
        // not a global lexical limit; sort/filter before exposing the bounded ASP page.
        return await RunAsync(async op =>
        {
            var directory = encodeKey is null && prefix.Contains('/') ? prefix[..(prefix.LastIndexOf('/') + 1)] : "";
            var entries = await op.ListAsync(directory, new ListOptions { Recursive = true }, CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            var keys = entries.Where(e => e.Metadata.IsFile).Select(e => decodeKey is null ? e.Path : decodeKey(e.Path)).OfType<string>()
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && AspValidation.Allows(context.Policy, k)
                    && (afterKey is null || string.CompareOrdinal(k, afterKey) > 0))
                .OrderBy(k => k, StringComparer.Ordinal).Take(limit + 1).ToArray();
            var page = new List<AspProviderObject>();
            foreach (var key in keys.Take(limit))
            {
                ValidateKey(context, key);
                page.Add(Map(key, await op.StatAsync(NativeKey(key), cancellationToken: CancellationToken.None)));
                ct.ThrowIfCancellationRequested();
            }
            return new AspProviderPage(page, keys.Length > limit ? page[^1].Key : null);
        }, ct);
    }

    public async ValueTask<AspProviderObject> StatAsync(AspAccessContext context, string key, string? version, string? etag, CancellationToken ct)
    {
        ValidateKey(context, key); AspValidation.Require(context.Policy.CanStat && capability.Stat, "forbidden", "Stat is not authorized.");
        return await RunAsync(async op => await StatCoreAsync(op, key, version, etag, ct), ct);
    }
    private async Task<AspProviderObject> StatCoreAsync(Operator op, string key, string? version, string? etag, CancellationToken ct)
    {
        AspValidation.Require(version is null || capability.StatWithVersion, "unsupported_option", "Native version selection is unavailable.");
        var item = Map(key, await op.StatAsync(NativeKey(key), new StatOptions
        {
            Version = version,
            IfMatch = capability.StatWithIfMatch ? etag : null
        }, CancellationToken.None));
        ct.ThrowIfCancellationRequested();
        AspValidation.Require((version is null || version == item.Version) && (etag is null || etag == item.ETag), "version_unavailable", "The selected object identity changed.");
        return item;
    }
    public async ValueTask<AspReadHandle> OpenReadAsync(AspAccessContext context, string key, string? version, string? etag, AspByteRange? range, CancellationToken ct)
    {
        ValidateKey(context, key); AspValidation.Require(context.Policy.CanRead && capability.Read, "forbidden", "Read is not authorized.");
        await execution.Slots.WaitAsync(ct); Operator? op = null;
        try
        {
            op = Open();
            var item = await StatCoreAsync(op, key, version, etag, ct);
            var offset = range?.Offset ?? 0;
            AspValidation.Require(offset >= 0 && offset <= item.Length && range?.Length is null or >= 0, "invalid_range", "Invalid byte range.");
            AspValidation.Require((version is null || capability.ReadWithVersion) && (etag is null || capability.ReadWithIfMatch),
                "unsupported_option", "The native reader cannot protect this object identity.");
            var length = Math.Min(range?.Length ?? long.MaxValue, item.Length - offset);
            Stream input = length == 0 ? Stream.Null : op.OpenReadStream(NativeKey(key),
                new ReadOptions { Offset = offset, Length = length, Version = version, IfMatch = etag, Chunk = 16_384, Concurrent = 1 });
            return new(item, new NativeStream(input, op, execution), offset, length);
        }
        catch (OpenDALException error) { op?.Dispose(); execution.Slots.Release(); throw Error(error); }
        catch { op?.Dispose(); execution.Slots.Release(); throw; }
    }
    public async ValueTask<IAspWriteHandle> OpenWriteAsync(AspAccessContext context, AspWriteOptions options, CancellationToken ct)
    {
        ValidateKey(context, options.Key);
        AspValidation.Require(context.Policy.CanWrite && capability.Write, "forbidden", "Write is not authorized.");
        AspValidation.Require(options.Mode is "create" or "replace" && options.ExpectedVersion is null
            && (options.Mode != "create" || Transfers.CreateOnly)
            && (options.IfMatch is null && options.IfNoneMatch is null || Transfers.ETagConditions)
            && (options.Metadata is null || Transfers.Metadata), "unsupported_option", "The native writer cannot uphold these options.");
        await execution.Slots.WaitAsync(ct); Operator? op = null;
        try
        {
            op = Open();
            var metadata = options.Metadata;
            var nativeOptions = new WriteOptions
            {
                IfNotExists = options.Mode == "create",
                IfMatch = options.IfMatch,
                IfNoneMatch = options.IfNoneMatch,
                ContentType = metadata?.ContentType,
                ContentEncoding = metadata?.ContentEncoding,
                ContentDisposition = metadata?.ContentDisposition,
                CacheControl = metadata?.CacheControl,
                UserMetadata = metadata?.UserMetadata,
                Concurrent = 1
            };
            if (!capability.WriteCanMulti) return new SingleWriter(op, execution, options, nativeOptions, context.Policy.MaxWriteBytes, NativeKey(options.Key));
            var output = op.OpenWriteStream(NativeKey(options.Key), nativeOptions);
            return new Writer(new NativeStream(output, op, execution), output, options);
        }
        catch (OpenDALException error) { op?.Dispose(); execution.Slots.Release(); throw Error(error); }
        catch { op?.Dispose(); execution.Slots.Release(); throw; }
    }
    private Operator Open()
    {
        using var original = create();
        return original.WithLayer(new TimeoutLayer { Timeout = TimeSpan.FromSeconds(20), IoTimeout = TimeSpan.FromSeconds(15) });
    }
    private async Task<T> RunAsync<T>(Func<Operator, Task<T>> operation, CancellationToken ct)
    {
        await execution.Slots.WaitAsync(ct);
        try { using var op = Open(); var result = await operation(op); ct.ThrowIfCancellationRequested(); return result; }
        catch (OpenDALException error) { throw Error(error); }
        finally { execution.Slots.Release(); }
    }
    private string NativeKey(string key) => encodeKey is null ? key : encodeKey(key);
    private void Validate(AspAccessContext context)
    {
        AspValidation.Context(context);
        AspValidation.Require(context.Store == store, "forbidden", "The exact logical Storage differs from this binding.");
    }
    private void ValidateKey(AspAccessContext context, string key) { Validate(context); AspValidation.DemandKey(context, key); checkKey?.Invoke(key); }
    private static AspProviderObject Map(string key, Metadata metadata)
    {
        AspValidation.Require(metadata.IsFile && metadata.ContentLength <= long.MaxValue, "invalid_result", "The backend returned invalid object metadata.");
        return new(key, string.IsNullOrEmpty(metadata.Version) ? null : metadata.Version, (long)metadata.ContentLength, null,
            string.IsNullOrEmpty(metadata.ETag) ? null : metadata.ETag,
            new(metadata.ContentType, metadata.ContentEncoding, metadata.ContentDisposition, metadata.CacheControl, metadata.UserMetadata));
    }
    private static AspException Error(OpenDALException error) => new(error.Code switch
    {
        ErrorCode.NotFound => "not_found",
        ErrorCode.AlreadyExists or ErrorCode.ConditionNotMatch or ErrorCode.Conflict => "conflict",
        ErrorCode.PermissionDenied => "forbidden",
        ErrorCode.Unsupported => "unsupported_option",
        ErrorCode.RangeNotSatisfied => "invalid_range",
        _ => "provider_failure"
    }, "The OpenDAL operation did not produce a confirmed result.");

    private sealed class SingleWriter(Operator op, OpenDalExecutionGate execution, AspWriteOptions options,
        WriteOptions nativeOptions, int limit, string nativeKey) : IAspWriteHandle
    {
        private readonly MemoryStream buffer = new();
        private bool disposed;
        public Stream Content => buffer;
        public async ValueTask<AspProviderObject> CompleteAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AspValidation.Require(buffer.Length <= limit, "limit_exceeded", "Non-progressive native writes are bounded to one ASP chunk.");
            try
            {
                var metadata = await op.WriteAsync(nativeKey, buffer.ToArray(), nativeOptions, CancellationToken.None);
                ct.ThrowIfCancellationRequested(); return Map(options.Key, metadata);
            }
            catch (OpenDALException error) { throw Error(error); }
        }
        public ValueTask AbortAsync(CancellationToken ct) => DisposeAsync();
        public ValueTask DisposeAsync()
        {
            if (!disposed) { disposed = true; buffer.Dispose(); op.Dispose(); execution.Slots.Release(); }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Writer(NativeStream content, OperatorOutputStream native, AspWriteOptions options) : IAspWriteHandle
    {
        private bool finalized;
        public Stream Content => content;
        public async ValueTask<AspProviderObject> CompleteAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try { await native.CompleteAsync(CancellationToken.None); }
            catch (OpenDALException error) { throw Error(error); }
            finalized = true; ct.ThrowIfCancellationRequested();
            // CompleteAsync returns no Metadata in 0.3.1. These are known facts of this
            // completed write; version/ETag/digest remain unavailable. Never post-stat.
            return new(options.Key, null, content.WrittenBytes, null, Metadata: options.Metadata);
        }
        public async ValueTask AbortAsync(CancellationToken ct)
        {
            await content.DisposeAsync();
            if (!finalized) throw new AspException("unknown_outcome", "OpenDAL disposal may close and publish an unfinished writer; rollback is not available.");
        }
        public ValueTask DisposeAsync() => content.DisposeAsync();
    }
    private sealed class NativeStream(Stream inner, Operator op, OpenDalExecutionGate execution) : Stream
    {
        private bool disposed;
        public long WrittenBytes { get; private set; }
        public override bool CanRead => !disposed && inner.CanRead;
        public override bool CanWrite => !disposed && inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try { var count = await inner.ReadAsync(buffer, CancellationToken.None); ct.ThrowIfCancellationRequested(); return count; }
            catch (OpenDALException error) { throw Error(error); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try { await inner.WriteAsync(buffer, CancellationToken.None); WrittenBytes += buffer.Length; ct.ThrowIfCancellationRequested(); }
            catch (OpenDALException error) { throw Error(error); }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async Task FlushAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); try { await inner.FlushAsync(CancellationToken.None); ct.ThrowIfCancellationRequested(); } catch (OpenDALException error) { throw Error(error); } }
        public override async ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                try { await inner.DisposeAsync(); }
                finally { op.Dispose(); execution.Slots.Release(); }
            }
            await base.DisposeAsync(); GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed) { disposed = true; try { inner.Dispose(); } finally { op.Dispose(); execution.Slots.Release(); } }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use WriteAsync.");
        public override void Flush() => throw new NotSupportedException("Use FlushAsync.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
