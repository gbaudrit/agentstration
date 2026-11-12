using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Agentstration.Http.Diagnostics;

internal sealed class HttpDiagnosticsMiddleware(RequestDelegate next, HttpDiagnosticsOptions options, ILogger<HttpDiagnosticsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (!options.Includes(path)) { await next(context); return; }
        var policy = options.Resolve(path);
        if (!policy.Enabled) { await next(context); return; }

        using var activity = HttpDiagnosticsTelemetry.Source.StartActivity("http.incoming.diagnostics");
        var category = policy.Category.ToString();
        activity?.SetTag("agentstration.http.category", category);
        var parentContext = activity?.Context ?? Activity.Current?.Context ?? default;
        var sensitive = BodyCapture.IsSensitivePath(path);
        var target = sensitive ? "/[sensitive route]" : path;
        activity?.SetTag("http.request.method", context.Request.Method);
        activity?.SetTag("url.path", target);
        var originalRequest = context.Request.Body;
        var originalResponse = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        var requestCapture = !sensitive ? new BodyCapture(options.MaximumBodyBytes) : null;
        var responseCapture = !sensitive ? new BodyCapture(options.MaximumBodyBytes) : null;
        using var requestStream = requestCapture is null ? null : new CaptureStream(originalRequest, requestCapture);
        if (requestStream is not null) context.Request.Body = requestStream;
        if (responseCapture is not null) context.Features.Set<IHttpResponseBodyFeature>(new CaptureResponseBodyFeature(originalResponse, responseCapture));
        var started = Stopwatch.GetTimestamp();
        var complete = false;
        string? errorType = null;
        try
        {
            await next(context);
            complete = true;
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
            context.Request.Body = originalRequest;
            context.Features.Set(originalResponse);
            activity?.SetTag("http.response.status_code", context.Response.StatusCode);
            if (context.Response.StatusCode >= 400) activity?.SetStatus(ActivityStatusCode.Error);
            if (requestCapture is not null)
                HttpDiagnosticsTelemetry.Payload(logger, activity, "request", requestCapture.Finish(context.Request.ContentType,
                    context.Request.Headers.ContentEncoding.Count > 0,
                    requestStream!.ReachedEnd || context.Request.ContentLength == requestCapture.ObservedBytes ||
                    context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false), context.Request.ContentType, parentContext, category);
            if (responseCapture is not null)
                HttpDiagnosticsTelemetry.Payload(logger, activity, "response", responseCapture.Finish(context.Response.ContentType,
                    context.Response.Headers.ContentEncoding.Count > 0, complete, BodyCapture.IsHealthPath(path)), context.Response.ContentType, parentContext, category);
            if (options.LogMetadata)
                HttpDiagnosticsTelemetry.Metadata(logger, "incoming", context.Request.Method, target,
                    errorType is null ? context.Response.StatusCode : null, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    errorType, (activity ?? Activity.Current)?.TraceId.ToString(), (activity ?? Activity.Current)?.SpanId.ToString(), category);
        }
    }
}
