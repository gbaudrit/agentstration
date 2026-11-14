using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Agentstration.Http.Diagnostics;

internal static partial class HttpDiagnosticsTelemetry
{
    internal static readonly ActivitySource Source = new(HttpDiagnosticsExtensions.ActivitySourceName);

    internal static void Payload(ILogger logger, Activity? activity, string direction, CapturedBody body, string? contentType,
        ActivityContext parentContext = default, string? category = null)
    {
        activity?.AddEvent(new ActivityEvent($"http.{direction}.payload", tags: new ActivityTagsCollection
        {
            ["agentstration.http.payload.body"] = body.Body,
            ["agentstration.http.payload.content_type"] = contentType,
            ["agentstration.http.payload.truncated"] = body.Truncated
        }));
        var correlation = activity?.Context ?? parentContext;
        LogPayload(logger, direction, body.Body, contentType, body.Truncated, category,
            correlation == default ? null : correlation.TraceId.ToString(), correlation == default ? null : correlation.SpanId.ToString());
    }

    [LoggerMessage(4200, LogLevel.Information, "HTTP {Direction} {Method} {Target} Category={Category} StatusCode={StatusCode} DurationMs={DurationMs} ErrorType={ErrorType} TraceId={TraceId} SpanId={SpanId}")]
    internal static partial void Metadata(ILogger logger, string direction, string method, string target,
        int? statusCode, double durationMs, string? errorType, string? traceId, string? spanId, string category);

    [LoggerMessage(4201, LogLevel.Information, "HTTP {Direction} payload Category={Category} ContentType={ContentType} Truncated={Truncated} TraceId={TraceId} SpanId={SpanId}: {Body}")]
    private static partial void LogPayload(ILogger logger, string direction, string body, string? contentType,
        bool truncated, string? category, string? traceId, string? spanId);
}
