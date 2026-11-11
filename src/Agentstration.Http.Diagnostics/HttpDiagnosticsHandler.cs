using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Agentstration.Http.Diagnostics;

internal sealed class HttpDiagnosticsHandler(HttpDiagnosticsOptions options, ILogger<HttpDiagnosticsHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || !options.Includes(uri.AbsolutePath, uri.Host))
            return await base.SendAsync(request, cancellationToken);

        request.Options.Set(new HttpRequestOptionsKey<bool>(HttpDiagnosticsExtensions.CaptureOwnerKey), true);
        using var activity = HttpDiagnosticsTelemetry.Source.StartActivity("http.outgoing.diagnostics");
        var parentContext = activity?.Context ?? Activity.Current?.Context ?? default;
        var sensitive = BodyCapture.IsSensitivePath(uri.AbsolutePath);
        var target = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + (sensitive ? "/[sensitive route]" : uri.AbsolutePath);
        activity?.SetTag("http.request.method", request.Method.Method);
        activity?.SetTag("url.full", target);
        if (options.CaptureRequestBody && request.Content is { } content && !sensitive)
        {
            request.Content = DiagnosticHttpContent.Wrap(content, options.MaximumBodyBytes,
                (capture, complete) => HttpDiagnosticsTelemetry.Payload(logger, activity, "request",
                    capture.Finish(content.Headers.ContentType?.ToString(), content.Headers.ContentEncoding.Count > 0, complete),
                    content.Headers.ContentType?.MediaType, parentContext), reading: false);
        }

        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        string? errorType = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode) activity?.SetStatus(ActivityStatusCode.Error);
            if (options.CaptureResponseBody && !sensitive)
            {
                var responseContent = response.Content;
                response.Content = DiagnosticHttpContent.Wrap(responseContent, options.MaximumBodyBytes, (capture, complete) =>
                {
                    // ResponseHeadersRead completes before the caller consumes the content. Emit a child span
                    // at EOF/disposal instead of reading ahead or retaining an open exchange span indefinitely.
                    using var payloadActivity = HttpDiagnosticsTelemetry.Source.StartActivity("http.response.payload", ActivityKind.Internal, parentContext);
                    HttpDiagnosticsTelemetry.Payload(logger, payloadActivity, "response",
                        capture.Finish(responseContent.Headers.ContentType?.ToString(), responseContent.Headers.ContentEncoding.Count > 0, complete),
                        responseContent.Headers.ContentType?.MediaType, parentContext);
                }, reading: true);
            }
            return response;
        }
        catch (Exception exception)
        {
            errorType = exception.GetType().FullName;
            activity?.SetTag("error.type", errorType);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            if (options.LogMetadata)
                HttpDiagnosticsTelemetry.Metadata(logger, "outgoing", request.Method.Method, target,
                    response is null ? null : (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    errorType, (activity ?? Activity.Current)?.TraceId.ToString(), (activity ?? Activity.Current)?.SpanId.ToString());
        }
    }
}
