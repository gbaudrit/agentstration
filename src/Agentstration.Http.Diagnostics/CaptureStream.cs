namespace Agentstration.Http.Diagnostics;

// A tee: no reads ahead, buffering of the transport, or ownership of the underlying stream.
internal sealed class CaptureStream(Stream inner, BodyCapture capture, Action<bool>? finished = null, bool leaveOpen = true) : Stream
{
    private bool completed;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { capture.Invalidate(); inner.Position = value; } }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) { capture.Invalidate(); return inner.Seek(offset, origin); }
    public override void SetLength(long value) { capture.Invalidate(); inner.SetLength(value); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = inner.Read(buffer);
        Observe(buffer[..count], buffer.Length);
        return count;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(buffer, cancellationToken);
        Observe(buffer.Span[..count], buffer.Length);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); capture.Append(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        capture.Append(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    internal bool ReachedEnd { get; private set; }
    private void Observe(ReadOnlySpan<byte> bytes, int requested)
    {
        capture.Append(bytes);
        if (bytes.IsEmpty && requested > 0) { ReachedEnd = true; Complete(true); }
    }

    private void Complete(bool complete)
    {
        if (completed) return;
        completed = true;
        finished?.Invoke(complete);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Complete(ReachedEnd);
            if (!leaveOpen) inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
