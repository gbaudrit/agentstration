using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace Agentstration.Http.Diagnostics;

// Preserve the server's streaming, flushing, completion and send-file features. In particular,
// do not replace its PipeWriter with a new buffered StreamResponseBodyFeature.
internal sealed class CaptureResponseBodyFeature(IHttpResponseBodyFeature original, BodyCapture capture) : IHttpResponseBodyFeature
{
    public Stream Stream { get; } = new CaptureStream(original.Stream, capture);
    public PipeWriter Writer { get; } = new CaptureWriter(original.Writer, capture);
    public void DisableBuffering() => original.DisableBuffering();
    public Task StartAsync(CancellationToken cancellationToken = default) => original.StartAsync(cancellationToken);
    public Task CompleteAsync() => original.CompleteAsync();
    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        capture.Invalidate();
        return original.SendFileAsync(path, offset, count, cancellationToken);
    }

    private sealed class CaptureWriter(PipeWriter inner, BodyCapture capture) : PipeWriter
    {
        private Memory<byte> memory;
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
        public override long UnflushedBytes => inner.UnflushedBytes;
        public override void Advance(int bytes)
        {
            capture.Append(memory.Span[..bytes]);
            inner.Advance(bytes);
            memory = default;
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) => memory = inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
    }
}
