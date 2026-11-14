using System.Net;

namespace Agentstration.Http.Diagnostics;

internal sealed class DiagnosticHttpContent(
    HttpContent original,
    int maximumBytes,
    Action<BodyCapture, bool> finished,
    bool reading) : HttpContent
{
    private BodyCapture? readCapture;
    private bool reported;

    internal static DiagnosticHttpContent Wrap(HttpContent content, int maximumBytes, Action<BodyCapture, bool> finished, bool reading)
    {
        var wrapper = new DiagnosticHttpContent(content, maximumBytes, finished, reading);
        foreach (var header in content.Headers) wrapper.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return wrapper;
    }

    protected override bool TryComputeLength(out long length)
    {
        length = original.Headers.ContentLength ?? 0;
        return original.Headers.ContentLength.HasValue;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var capture = new BodyCapture(maximumBytes);
        using var tee = new CaptureStream(stream, capture);
        var complete = false;
        try
        {
            await original.CopyToAsync(tee, context, cancellationToken);
            complete = true;
        }
        finally
        {
            if (reading) Report(capture, complete);
            else finished(capture, complete);
        }
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
        WrapReadStream(original.ReadAsStream(cancellationToken));

    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        WrapReadStream(await original.ReadAsStreamAsync(cancellationToken));

    private Stream WrapReadStream(Stream stream)
    {
        readCapture = new BodyCapture(maximumBytes);
        return new CaptureStream(stream, readCapture, complete => Report(readCapture, complete), leaveOpen: false);
    }

    private void Report(BodyCapture capture, bool complete)
    {
        if (reported) return;
        reported = true;
        finished(capture, complete);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (reading) Report(readCapture ?? new BodyCapture(maximumBytes), false);
            original.Dispose();
        }
        base.Dispose(disposing);
    }
}
