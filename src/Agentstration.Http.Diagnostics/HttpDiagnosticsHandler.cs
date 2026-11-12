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
        var policy = options.Resolve(uri.AbsolutePath);
        if (!policy.Enabled) return await base.SendAsync(request, cancellationToken);
        using var activity = HttpDiagnosticsTelemetry.Source.StartActivity("http.outgoing.diagnostics");
        var category = policy.Category.ToString();
        activity?.SetTag("agentstration.http.category", category);
        var parentContext = activity?.Context ?? Activity.Current?.Context ?? default;
        var sensitive = BodyCapture.IsSensitivePath(uri.AbsolutePath);
        var target = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + (sensitive ? "/[sensitive route]" : uri.AbsolutePath);
        activity?.SetTag("http.request.method", request.Method.Method);
        activity?.SetTag("url.full", target);
        if (request.Content is { } content && !sensitive)
        {
            request.Content = DiagnosticHttpContent.Wrap(content, options.MaximumBodyBytes,
                (capture, complete) => HttpDiagnosticsTelemetry.Payload(logger, activity, "request",
                    capture.Finish(content.Headers.ContentType?.ToString(), content.Headers.ContentEncoding.Count > 0, complete),
                    content.Headers.ContentType?.MediaType, parentContext, category), reading: false);
        }
        else if (request.Content is null && !sensitive)
            HttpDiagnosticsTelemetry.Payload(logger, activity, "request", new("[empty body]", false), null, parentContext, category);

        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        string? errorType = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode) activity?.SetStatus(ActivityStatusCode.Error);
            if (!sensitive)
            {
                var responseContent = response.Content;
                response.Content = DiagnosticHttpContent.Wrap(responseContent, options.MaximumBodyBytes, (capture, complete) =>
                {
                    // ResponseHeadersRead completes before the caller consumes the content. Emit a child span
                    // at EOF/disposal instead of reading ahead or retaining an open exchange span indefinitely.
                    using var payloadActivity = HttpDiagnosticsTelemetry.Source.StartActivity("http.response.payload", ActivityKind.Internal, parentContext);
                    payloadActivity?.SetTag("agentstration.http.category", category);
                    HttpDiagnosticsTelemetry.Payload(logger, payloadActivity, "response",
                        capture.Finish(responseContent.Headers.ContentType?.ToString(), responseContent.Headers.ContentEncoding.Count > 0, complete,
                            BodyCapture.IsHealthPath(uri.AbsolutePath)),
                        responseContent.Headers.ContentType?.MediaType, parentContext, category);
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
                    errorType, (activity ?? Activity.Current)?.TraceId.ToString(), (activity ?? Activity.Current)?.SpanId.ToString(), category);
        }
    }
}
