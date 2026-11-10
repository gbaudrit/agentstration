using System.Security.Cryptography;
using Agentstration.Asp.Abstractions;

namespace Agentstration.Asp.Client;

public static class AspStreams
{
    public static Stream OpenReadStream(this AspClient client, AspReadSelection selection, string requestId, DateTimeOffset notAfter) =>
        new ReadStream(client, selection, requestId, notAfter);
    public static AspWriteStream OpenWriteStream(this AspClient client, AspWriteOptions options, string requestId, DateTimeOffset notAfter) =>
        new(client, options, requestId, notAfter);

    private sealed class ReadStream(AspClient client, AspReadSelection selection, string id, DateTimeOffset notAfter) : Stream
    {
        private string? token;
        private long sequence;
        private byte[] content = [];
        private int position;
        private bool final;
        private bool disposed;
        private AspObject? item;
        private long nextOffset;
        private long selectedOffset;
        private long selectedLength;
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (buffer.Length == 0) return 0;
            if (position == content.Length && !final)
            {
                var result = await client.ReadBlobAsync(new(sequence == 0 ? id : AspValidation.Digest(System.Text.Encoding.UTF8.GetBytes(id + ":" + sequence)), sequence, token,
                    token is null ? selection : null, token is null ? notAfter : null), cancellationToken);
                if (item is null)
                {
                    item = result.Item; nextOffset = result.SelectedOffset;
                    selectedOffset = result.SelectedOffset; selectedLength = result.SelectedLength;
                }
                AspValidation.Require(System.Text.Json.JsonSerializer.Serialize(result.Item, AspProtocol.JsonOptions) == System.Text.Json.JsonSerializer.Serialize(item, AspProtocol.JsonOptions) && result.Offset == nextOffset
                    && result.SelectedOffset == selectedOffset && result.SelectedLength == selectedLength,
                    "invalid_result", "The read Stream changed object, selection or byte position.");
                token = result.SessionRef; sequence++; final = result.Final;
                content = Convert.FromBase64String(result.ContentBase64); position = 0;
                hash.AppendData(content); nextOffset += content.Length;
                if (final && selectedOffset == 0 && selectedLength == item.Length && item.Sha256 is not null)
                    AspValidation.Require(Convert.ToHexStringLower(hash.GetHashAndReset()) == item.Sha256,
                        "invalid_result", "The read Stream failed its complete-content digest check.");
            }
            var count = Math.Min(buffer.Length, content.Length - position);
            content.AsMemory(position, count).CopyTo(buffer); position += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync()
        {
            if (!disposed && token is not null && !final) await client.CloseReadAsync(new(Guid.NewGuid().ToString("N"), token));
            disposed = true; content = []; hash.Dispose(); await base.DisposeAsync(); GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Async Stream adapter. Explicit CompleteAsync publishes; disposal aborts without promising rollback.</summary>
public sealed class AspWriteStream(AspClient client, AspWriteOptions options, string requestId, DateTimeOffset notAfter) : Stream
{
    private readonly byte[] buffer = new byte[client.Description.Policy.MaxWriteBytes];
    private int count;
    private string? token;
    private long sequence;
    private bool completed;
    private bool disposed;
    private long acceptedBytes;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !completed && !disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed || completed, this);
        while (!content.IsEmpty)
        {
            if (count == buffer.Length) await SendAsync(false, cancellationToken);
            var take = Math.Min(content.Length, buffer.Length - count);
            content[..take].CopyTo(buffer.AsMemory(count)); hash.AppendData(content.Span[..take]);
            acceptedBytes += take; count += take; content = content[take..];
        }
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public async Task<AspWriteReceipt> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed || completed, this);
        var result = await SendAsync(true, cancellationToken); completed = true;
        var receipt = result.Receipt!;
        AspValidation.Require(receipt.WrittenBytes == acceptedBytes && receipt.Sha256 == Convert.ToHexStringLower(hash.GetHashAndReset())
            && receipt.Item.Key == options.Key && (options.Mode == "append" || receipt.Item.Length == acceptedBytes),
            "invalid_result", "The write Stream final receipt differs from its content or selected key.");
        return receipt;
    }
    private async Task<AspWriteResult> SendAsync(bool final, CancellationToken ct)
    {
        var result = await client.WriteBlobAsync(new(sequence == 0 ? requestId : AspValidation.Digest(System.Text.Encoding.UTF8.GetBytes(requestId + ":" + sequence)), sequence, final,
            Convert.ToBase64String(buffer, 0, count), token, token is null ? options : null, token is null ? notAfter : null), ct);
        token = result.SessionRef; sequence++; count = 0;
        return result;
    }
    public override async Task FlushAsync(CancellationToken cancellationToken) { if (count > 0) await SendAsync(false, cancellationToken); }
    public override async ValueTask DisposeAsync()
    {
        if (!disposed && !completed && token is not null) await client.AbortWriteAsync(new(Guid.NewGuid().ToString("N"), token));
        disposed = true; hash.Dispose(); await base.DisposeAsync(); GC.SuppressFinalize(this);
    }
    protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); disposed = true; base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException("Use FlushAsync.");
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use WriteAsync.");
}
