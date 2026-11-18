using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Asp.Abstractions;

namespace Agentstration.Asp.Server;

/// <summary>Protocol state only: physical content remains in the provider's streams.</summary>
internal sealed class AspTransfers : IAsyncDisposable
{
    private sealed record Replay(string Fingerprint, object? Result, string? Error, DateTimeOffset Expires, int Bytes);
    private sealed class Session(string token, string scope, string kind, DateTimeOffset expires)
    {
        public string Token { get; } = token;
        public string Scope { get; } = scope;
        public string Kind { get; } = kind;
        public DateTimeOffset Expires { get; } = expires;
        public SemaphoreSlim Gate { get; } = new(1);
        public Dictionary<long, Replay> Replies { get; } = [];
        public Dictionary<string, long> Identities { get; } = new(StringComparer.Ordinal);
        public int Reservation { get; set; }
        public bool Reserved { get; set; }
        public long Next { get; set; }
        public long Bytes { get; set; }
        public long Consumed { get; set; }
        public byte[] Pending { get; set; } = [];
        public string State { get; set; } = "open";
        public AspReadHandle? Reader { get; set; }
        public AspObject? PublishedRead { get; set; }
        public IAspWriteHandle? Writer { get; set; }
        public AspWriteOptions? Options { get; set; }
        public AspReadSelection? Selection { get; set; }
        public AspWriteReceipt? Receipt { get; set; }
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }
    private readonly TimeProvider clock;
    private readonly AspReferences references;
    private readonly AspSessionLimits limits;
    private readonly object gate = new();
    private readonly SemaphoreSlim collection = new(1);
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> initial = new(StringComparer.Ordinal);
    private readonly ITimer cleanup;
    private bool disposed;
    private int reservedReceipts;
    private long reservedBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public AspTransfers(TimeProvider clock, AspReferences references, AspSessionLimits limits)
    {
        AspValidation.Require(limits.MaximumSessions is > 0 and <= 1024 && limits.MaximumReceipts > 0
            && limits.MaximumReplayBytes > 0 && limits.ReceiptWindowSeconds is > 0 and <= 300,
            "invalid_policy", "Invalid session capacity limits.");
        this.clock = clock; this.references = references; this.limits = limits;
        cleanup = clock.CreateTimer(_ => _ = CollectAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public async ValueTask<object> ExecuteAsync(AspStoreBinding binding, AspAccessContext context, string operation,
        JsonElement arguments, Func<AspProviderObject, AspObject> publicObject, CancellationToken ct)
    {
        await CollectAsync();
        if (operation is "close_read" or "abort_write" or "write_status")
            return await ControlAsync(context, operation, AspValidation.Parse<AspSessionRequest>(arguments), ct);
        var text = operation.EndsWith("_text", StringComparison.Ordinal);
        var writing = operation.StartsWith("write_", StringComparison.Ordinal);
        AspValidation.Require(writing ? context.Policy.CanWrite && binding.Provider is IAspObjectWriter : context.Policy.CanRead,
            "forbidden", "The transfer operation is not authorized.");
        string requestId; long sequence; string? token; DateTimeOffset? expires;
        AspReadSelection? selection = null; AspWriteOptions? options = null; byte[] bytes = []; bool final = false;
        if (writing && text)
        {
            var request = AspValidation.Parse<AspWriteTextRequest>(arguments);
            (requestId, sequence, token, expires, options, final) = (request.RequestId, request.Sequence, request.SessionRef, request.NotAfter, request.Options, request.Final);
            AspValidation.Require(request.Encoding is null or "utf-8" && (token is null || request.Encoding is null), "unsupported_encoding", "Only initial UTF-8 text encoding is supported.");
            bytes = AspValidation.Text(request.Content, context.Policy.MaxWriteBytes);
        }
        else if (writing)
        {
            var request = AspValidation.Parse<AspWriteBlobRequest>(arguments);
            (requestId, sequence, token, expires, options, final) = (request.RequestId, request.Sequence, request.SessionRef, request.NotAfter, request.Options, request.Final);
            bytes = AspValidation.Content(request.ContentBase64, context.Policy.MaxWriteBytes);
        }
        else if (text)
        {
            var request = AspValidation.Parse<AspReadTextRequest>(arguments);
            (requestId, sequence, token, expires, selection) = (request.RequestId, request.Sequence, request.SessionRef, request.NotAfter, request.Selection);
        }
        else
        {
            var request = AspValidation.Parse<AspReadBlobRequest>(arguments);
            (requestId, sequence, token, expires, selection) = (request.RequestId, request.Sequence, request.SessionRef, request.NotAfter, request.Selection);
        }
        AspValidation.RequestId(requestId);
        AspValidation.Require(sequence >= 0 && (!writing || bytes.Length <= context.Policy.MaxWriteBytes), "limit_exceeded", "Invalid sequence or excessive chunk.");
        var fingerprint = AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { operation, arguments }, AspProtocol.JsonOptions));
        var scope = Scope(context);
        Session session = null!;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (token is null)
            {
                AspValidation.Require(sequence == 0 && expires is not null && (writing ? options is not null : selection is not null),
                    "invalid_request", "An initial call requires sequence zero, deadline and fixed selection/options.");
                AspValidation.Deadline(expires.Value, clock);
                var identity = scope + ":" + requestId;
                if (initial.TryGetValue(identity, out var existing)) session = sessions[existing];
                else
                {
                    AspValidation.Require(sessions.Count < limits.MaximumReceipts && sessions.Values.Count(s => s.State == "open") < limits.MaximumSessions, "capacity_exceeded", "Session capacity reached.");
                    if (writing) ValidateWrite(options!, binding.Provider.Transfers, context);
                    else ValidateSelection(selection!, text);
                    var key = writing ? options!.Key : references.Open(selection!.Reference, "object", context).Key;
                    token = references.Seal("session", context, key);
                    // Include a random selector: identical logical keys still need independent session identities.
                    token += ":" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                    session = new(token, scope, operation, expires.Value) { Options = options, Selection = selection };
                    sessions.Add(token, session); initial.Add(identity, token);
                }
            }
            else
            {
                AspValidation.Require(options is null && selection is null && expires is null, "invalid_request", "Continuation cannot replace fixed options or deadline.");
                AspValidation.Require(token.Length <= 4096 && sessions.TryGetValue(token, out session!), "invalid_reference", "Unknown session reference.");
            }
            AspValidation.Require(session.Scope == scope && session.Kind == operation, "invalid_reference", "Session target, representation or authority differs.");
        }
        await session.Gate.WaitAsync(ct);
        try
        {
            AspValidation.Require(clock.GetUtcNow() < session.Expires, "request_expired", "The session has expired.");
            Replay? replay;
            lock (gate) session.Replies.TryGetValue(sequence, out replay);
            if (replay is not null)
            {
                AspValidation.Require(replay.Expires > clock.GetUtcNow(), "request_expired", "The chunk replay window has expired.");
                AspValidation.Require(replay.Fingerprint == fingerprint, "idempotency_conflict", "Chunk identity was reused with different arguments.");
                if (replay.Error is not null) throw new AspException(replay.Error, "The original transfer outcome was not confirmed.", session.Token);
                return replay.Result!;
            }
            AspValidation.Require(sequence >= session.Next, "request_expired", "The replay receipt has expired.");
            AspValidation.Require(sequence == session.Next && session.State == "open", "sequence_conflict", "The session is not open at the requested sequence.");
            lock (gate) AspValidation.Require(!session.Identities.TryGetValue(requestId, out var previous) || previous == sequence,
                "idempotency_conflict", "Request identity was reused at another sequence.");
            var reservation = writing ? 0 : context.Policy.MaxReadBytes;
            lock (gate)
            {
                PurgeReplays();
                AspValidation.Require(sessions.Values.Sum(s => s.Replies.Count) + reservedReceipts < limits.MaximumReceipts
                    && sessions.Values.Sum(s => s.Replies.Values.Sum(r => (long)r.Bytes)) + reservation + reservedBytes <= limits.MaximumReplayBytes,
                    "capacity_exceeded", "Live replay receipt capacity reached; retry the unchanged call later.");
                reservedReceipts++; reservedBytes += reservation; session.Reservation = reservation; session.Reserved = true;
            }
            using var expiry = new CancellationTokenSource(session.Expires - clock.GetUtcNow(), clock);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
            ct = execution.Token;
            object result;
            try
            {
                if (writing)
                {
                    if (session.Writer is null)
                    {
                        AspValidation.Require(bytes.Length > 0 || !final || binding.Provider.Transfers.EmptyWrite, "unsupported_option", "Empty writes are unavailable.");
                        AspValidation.Require(final || binding.Provider.Transfers.ProgressiveWrite, "unsupported_operation", "Progressive writes are unavailable.");
                        session.Writer = await ((IAspObjectWriter)binding.Provider).OpenWriteAsync(context, session.Options!, ct);
                    }
                    AspValidation.Require(session.Bytes <= binding.Provider.Transfers.MaximumObjectBytes - bytes.Length,
                        "limit_exceeded", "Object exceeds provider transfer limits.");
                    await session.Writer.Content.WriteAsync(bytes, ct);
                    session.Hash.AppendData(bytes); session.Bytes += bytes.Length;
                    if (final)
                    {
                        var digest = Convert.ToHexStringLower(session.Hash.GetHashAndReset());
                        AspValidation.Require((session.Options!.ExpectedLength is null || session.Options.ExpectedLength == session.Bytes)
                            && (session.Options.ExpectedSha256 is null || session.Options.ExpectedSha256 == digest),
                            "integrity_mismatch", "Written bytes differ from the expected length or digest.");
                        var item = await session.Writer.CompleteAsync(ct);
                        AspValidation.Require(clock.GetUtcNow() < session.Expires, "unknown_outcome", "Publication completed after the session deadline.");
                        AspValidation.Item(item); AspValidation.DemandKey(context, item.Key);
                        AspValidation.Require(item.Key == session.Options.Key && (session.Options.Mode == "append" || item.Length == session.Bytes)
                            && (item.Sha256 is null || session.Options.Mode == "append" || item.Sha256 == digest), "invalid_result", "Invalid provider write receipt.");
                        session.Receipt = new(publicObject(item), session.Bytes, digest); session.State = "finalized";
                        await session.Writer.DisposeAsync(); session.Writer = null;
                    }
                    result = new AspWriteResult(session.Token, sequence, final, session.Bytes, session.Receipt);
                }
                else
                {
                    if (session.Reader is null)
                    {
                        var reference = references.Open(session.Selection!.Reference, "object", context);
                        session.Reader = await binding.Provider.OpenReadAsync(context, reference.Key, reference.Version, reference.ETag, session.Selection.Range, ct);
                        var reader = session.Reader;
                        session.PublishedRead = publicObject(reader.Item);
                        AspValidation.Item(reader.Item); AspValidation.DemandKey(context, reader.Item.Key);
                        var selectedOffset = session.Selection.Range?.Offset ?? 0;
                        AspValidation.Require(reader.Item.Key == reference.Key && (reference.Version is null || reader.Item.Version == reference.Version)
                            && (reference.ETag is null || reader.Item.ETag == reference.ETag)
                            && selectedOffset <= reader.Item.Length && reader.Offset == selectedOffset
                            && reader.Length == Math.Min(session.Selection.Range?.Length ?? long.MaxValue, reader.Item.Length - selectedOffset)
                            && reader.Length <= binding.Provider.Transfers.MaximumObjectBytes,
                            "invalid_result", "The provider changed the selected object or range.");
                        AspValidation.Require(binding.Provider.Transfers.ProgressiveRead || reader.Length <= context.Policy.MaxReadBytes,
                            "unsupported_operation", "Progressive reads are unavailable.");
                    }
                    var input = session.Reader;
                    var preferred = session.Selection!.PreferredChunkSizeBytes ?? binding.Provider.Transfers.DefaultChunkSizeBytes;
                    var desired = Math.Min(text ? Math.Min(context.Policy.MaxReadBytes, 24_576) : context.Policy.MaxReadBytes, preferred);
                    var buffer = new byte[context.Policy.MaxReadBytes];
                    session.Pending.CopyTo(buffer, 0);
                    var count = session.Pending.Length;
                    session.Pending = [];
                    if (session.Consumed < input.Length && count < desired)
                    {
                        var read = await input.Content.ReadAsync(buffer.AsMemory(count,
                            (int)Math.Min(desired - count, input.Length - session.Consumed)), ct);
                        AspValidation.Require(read > 0, "invalid_result", "The provider ended before the selected range.");
                        session.Consumed += read; count += read;
                    }
                    var returned = count;
                    if (text)
                    {
                        while (true)
                        {
                            var valid = Utf8Prefix(buffer.AsSpan(0, count), out var incomplete);
                            if (!incomplete) { returned = valid; break; }
                            AspValidation.Require(session.Consumed < input.Length, "invalid_text", "The byte range ends inside a UTF-8 character.");
                            if (valid > 0) { returned = valid; session.Pending = buffer.AsSpan(valid, count - valid).ToArray(); break; }
                            AspValidation.Require(count < buffer.Length, "character_boundary", "The hard chunk bound cannot hold the next UTF-8 character.");
                            var read = await input.Content.ReadAsync(buffer.AsMemory(count, 1), ct);
                            AspValidation.Require(read > 0, "invalid_result", "The provider ended inside a UTF-8 character.");
                            session.Consumed += read; count += read;
                        }
                    }
                    var offset = input.Offset + session.Bytes;
                    session.Bytes += returned;
                    var ended = session.Bytes == input.Length;
                    session.Hash.AppendData(buffer, 0, returned);
                    if (ended && input.Offset == 0 && input.Length == input.Item.Length && input.Item.Sha256 is not null)
                        AspValidation.Require(Convert.ToHexStringLower(session.Hash.GetHashAndReset()) == input.Item.Sha256, "invalid_result", "The complete read failed its content integrity check.");
                    var item = session.PublishedRead!;
                    result = text ? new AspReadTextResult(session.Token, sequence, ended, item, offset, returned, input.Offset, input.Length,
                        Utf8.GetString(buffer, 0, returned))
                        : new AspReadBlobResult(session.Token, sequence, ended, item, offset, returned, input.Offset, input.Length,
                            Convert.ToBase64String(buffer, 0, returned));
                    if (ended) { session.State = "closed"; await input.DisposeAsync(); session.Reader = null; }
                }
            }
            catch (Exception exception) when (exception is AspException or OperationCanceledException or IOException or InvalidOperationException or ArgumentException)
            {
                var code = exception is AspException business ? business.Code : "unknown_outcome";
                session.State = "unknown";
                await ReleaseAsync(session);
                AddReplay(session, sequence, requestId, new(fingerprint, null, code, ReceiptExpiry(session), 0));
                throw new AspException(code, "The transfer did not produce a confirmed result; inspect status or retry the exact original call.", session.Token);
            }
            AddReplay(session, sequence, requestId, new(fingerprint, result, null, ReceiptExpiry(session), reservation));
            return result;
        }
        finally
        {
            lock (gate) { if (session.Reserved) { reservedReceipts--; reservedBytes -= session.Reservation; session.Reserved = false; } }
            session.Gate.Release();
        }
    }

    private async ValueTask<AspSessionResult> ControlAsync(AspAccessContext context, string operation, AspSessionRequest request, CancellationToken ct)
    {
        AspValidation.RequestId(request.RequestId);
        Session session = null!;
        lock (gate) AspValidation.Require(sessions.TryGetValue(request.SessionRef, out session!) && session.Scope == Scope(context),
            "invalid_reference", "Unknown session or changed authority.");
        var writing = session.Kind.StartsWith("write_", StringComparison.Ordinal);
        AspValidation.Require(operation == "close_read" ? !writing && context.Policy.CanRead : writing && context.Policy.CanWrite,
            "forbidden", "The session operation is not authorized.");
        await session.Gate.WaitAsync(ct);
        try
        {
            AspValidation.Require(session.Expires > clock.GetUtcNow(), "request_expired", "The session has expired.");
            if (operation != "write_status" && session.State == "open")
            {
                session.State = writing ? "aborted" : "closed";
                await ReleaseAsync(session);
            }
            return new(session.Token, session.State, session.Receipt);
        }
        finally { session.Gate.Release(); }
    }
    private void AddReplay(Session session, long sequence, string id, Replay replay)
    {
        lock (gate)
        {
            if (session.Reserved) { reservedReceipts--; reservedBytes -= session.Reservation; session.Reserved = false; }
            session.Replies.Add(sequence, replay); session.Identities[id] = sequence; session.Next++;
        }
    }
    private DateTimeOffset ReceiptExpiry(Session session) => new(Math.Min(session.Expires.UtcTicks,
        clock.GetUtcNow().AddSeconds(limits.ReceiptWindowSeconds).UtcTicks), TimeSpan.Zero);
    private static string Scope(AspAccessContext context) => AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(context, AspProtocol.JsonOptions));
    private void PurgeReplays()
    {
        foreach (var session in sessions.Values)
            foreach (var expired in session.Replies.Where(p => p.Value.Expires <= clock.GetUtcNow()).Select(p => p.Key).ToArray())
            {
                session.Replies.Remove(expired);
                foreach (var id in session.Identities.Where(p => p.Value == expired).Select(p => p.Key).ToArray()) session.Identities.Remove(id);
            }
    }
    private async Task CollectAsync()
    {
        await collection.WaitAsync();
        try { await CollectCoreAsync(); }
        finally { collection.Release(); }
    }
    private async Task CollectCoreAsync()
    {
        Session[] expired;
        lock (gate)
        {
            if (disposed) return;
            PurgeReplays();
            expired = sessions.Values.Where(s => s.Expires <= clock.GetUtcNow()).ToArray();
        }
        foreach (var session in expired)
        {
            if (!await session.Gate.WaitAsync(0)) continue;
            try
            {
                await ReleaseAsync(session);
                lock (gate)
                {
                    sessions.Remove(session.Token);
                    foreach (var key in initial.Where(p => p.Value == session.Token).Select(p => p.Key).ToArray()) initial.Remove(key);
                }
                session.Hash.Dispose();
            }
            finally { session.Gate.Release(); }
        }
    }
    private static async ValueTask ReleaseAsync(Session session)
    {
        try
        {
            if (session.Reader is not null) await session.Reader.DisposeAsync();
            if (session.Writer is not null) { await session.Writer.AbortAsync(CancellationToken.None); await session.Writer.DisposeAsync(); }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or AspException) { session.State = "unknown"; }
        finally { session.Reader = null; session.Writer = null; }
    }
    private static int Utf8Prefix(ReadOnlySpan<byte> bytes, out bool incomplete)
    {
        var position = 0; incomplete = false;
        while (position < bytes.Length)
        {
            var status = Rune.DecodeFromUtf8(bytes[position..], out _, out var consumed);
            AspValidation.Require(status != OperationStatus.InvalidData, "invalid_text", "Invalid UTF-8 content or byte-range boundary.");
            if (status == OperationStatus.NeedMoreData) { incomplete = true; break; }
            position += consumed;
        }
        return position;
    }
    private static void ValidateSelection(AspReadSelection selection, bool text)
    {
        AspValidation.Require(selection.Reference is { Length: > 0 and <= 4096 }
            && selection.PreferredChunkSizeBytes is null or > 0 && selection.Range is null or { Offset: >= 0, Length: null or >= 0 },
            "invalid_request", "Invalid read selection.");
        AspValidation.Require(text ? selection.Encoding is null or "utf-8" : selection.Encoding is null,
            "unsupported_encoding", "Only UTF-8 text is supported; binary has no text encoding.");
    }
    private static void ValidateWrite(AspWriteOptions options, AspTransferCapabilities caps, AspAccessContext context)
    {
        AspValidation.DemandKey(context, options.Key);
        AspValidation.Require(options.Mode is "create" or "replace" or "append" && options.ExpectedLength is null or >= 0,
            "invalid_request", "Invalid write mode or expected length.");
        AspValidation.Require((options.Mode != "create" || caps.CreateOnly) && (options.Mode != "replace" || caps.Replace) && (options.Mode != "append" || caps.Append)
            && (options.ExpectedVersion is null || caps.VersionCondition) && (options.IfMatch is null && options.IfNoneMatch is null || caps.ETagConditions)
            && (options.Metadata is null || caps.Metadata), "unsupported_option", "The provider cannot uphold a requested write option.");
        AspValidation.Require(!(options.IfMatch is not null && options.IfNoneMatch is not null)
            && (options.Mode != "create" || options.ExpectedVersion is null && options.IfMatch is null),
            "unsupported_option", "Contradictory write conditions.");
        AspValidation.Require(options.ExpectedLength is null || options.ExpectedLength <= caps.MaximumObjectBytes,
            "limit_exceeded", "Expected content exceeds the provider object bound.");
        if (options.ExpectedVersion is not null) AspValidation.RequestId(options.ExpectedVersion);
        if (options.ExpectedSha256 is not null) AspValidation.Require(options.ExpectedSha256.Length == 64 && options.ExpectedSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "invalid_request", "Expected digest must be lowercase SHA-256 hex.");
        AspValidation.Require(options.Metadata is null || JsonSerializer.SerializeToUtf8Bytes(options.Metadata, AspProtocol.JsonOptions).Length <= 8192,
            "limit_exceeded", "Metadata exceeds its bound.");
    }
    public async ValueTask DisposeAsync()
    {
        await cleanup.DisposeAsync();
        await collection.WaitAsync();
        Session[] all;
        lock (gate) { disposed = true; all = sessions.Values.ToArray(); }
        try
        {
            foreach (var session in all)
            {
                await session.Gate.WaitAsync();
                try { await ReleaseAsync(session); session.Hash.Dispose(); }
                finally { session.Gate.Release(); }
            }
            lock (gate) { sessions.Clear(); initial.Clear(); }
        }
        finally { collection.Release(); }
    }
}
