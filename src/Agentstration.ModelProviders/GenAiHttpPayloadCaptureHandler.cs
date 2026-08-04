using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agentstration.ModelProviders;

public sealed partial class GenAiHttpPayloadCaptureHandler(
    GenAiObservabilityOptions observability,
    ILogger<GenAiHttpPayloadCaptureHandler> logger) : DelegatingHandler
{
    public const string TelemetrySourceName = "Agentstration.GenAI.HttpPayloadCapture";
    public static readonly ActivitySource ActivitySource = new(TelemetrySourceName);

    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "accesstoken",
        "apikey",
        "authorization",
        "clientsecret",
        "credential",
        "password",
        "refreshtoken",
        "secret",
        "token"
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var capture = observability.HttpPayloadCapture;
        if (!capture.Enabled) return await base.SendAsync(request, cancellationToken);

        using var activity = ActivitySource.StartActivity("gen_ai.http.payload_capture", ActivityKind.Internal);
        var safeRequestUri = SafeRequestUri(request.RequestUri);
        activity?.SetTag("http.request.method", request.Method.Method);
        activity?.SetTag("url.full", safeRequestUri);

        if (request.Content is not null)
        {
            var requestBody = await CaptureAsync(request.Content, capture.MaximumBodyLength, cancellationToken);
            activity?.AddEvent(new ActivityEvent("http.request.payload", tags: new ActivityTagsCollection
            {
                ["agentstration.http.payload.body"] = requestBody.Value,
                ["agentstration.http.payload.content_type"] = request.Content.Headers.ContentType?.MediaType,
                ["agentstration.http.payload.truncated"] = requestBody.Truncated
            }));
            LogRequestPayload(
                logger,
                request.Method.Method,
                safeRequestUri,
                request.Content.Headers.ContentType?.MediaType,
                requestBody.Truncated,
                requestBody.Value);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            activity?.SetTag("error.type", exception.GetType().FullName);
            throw;
        }
        activity?.SetTag("http.response.status_code", (int)response.StatusCode);
        activity?.SetStatus(response.IsSuccessStatusCode ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        if (!capture.CaptureResponse || response.Content is null) return response;

        var originalContent = response.Content;
        var originalBytes = await originalContent.ReadAsByteArrayAsync(cancellationToken);
        var replacement = new ByteArrayContent(originalBytes);
        foreach (var header in originalContent.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
        var responseBody = Capture(originalBytes, originalContent.Headers.ContentType, capture.MaximumBodyLength);
        activity?.AddEvent(new ActivityEvent("http.response.payload", tags: new ActivityTagsCollection
        {
            ["agentstration.http.payload.body"] = responseBody.Value,
            ["agentstration.http.payload.content_type"] = originalContent.Headers.ContentType?.MediaType,
            ["agentstration.http.payload.truncated"] = responseBody.Truncated
        }));
        LogResponsePayload(
            logger,
            request.Method.Method,
            safeRequestUri,
            (int)response.StatusCode,
            originalContent.Headers.ContentType?.MediaType,
            responseBody.Truncated,
            responseBody.Value);
        originalContent.Dispose();
        return response;
    }

    private static async Task<CapturedPayload> CaptureAsync(HttpContent content, int maximumLength, CancellationToken cancellationToken)
    {
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
        return Capture(bytes, content.Headers.ContentType, maximumLength);
    }

    private static CapturedPayload Capture(byte[] bytes, MediaTypeHeaderValue? contentType, int maximumLength)
    {
        if (!IsJson(contentType))
            return new CapturedPayload($"[payload omitted: content type {contentType?.MediaType ?? "unknown"}]", false);

        var text = Encoding.UTF8.GetString(bytes);
        try
        {
            using var document = JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteRedacted(writer, document.RootElement);
            text = Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return new CapturedPayload("[payload omitted: invalid JSON]", false);
        }

        if (text.Length <= maximumLength) return new CapturedPayload(text, false);
        return new CapturedPayload($"{text[..maximumLength]}...[truncated]", true);
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (SensitivePropertyNames.Contains(Normalize(property.Name))) writer.WriteStringValue("***REDACTED***");
                    else WriteRedacted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteRedacted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit));

    private static bool IsJson(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static string SafeRequestUri(Uri? uri) => uri is null
        ? string.Empty
        : uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.OriginalString.Split('?', 2)[0];

    [LoggerMessage(4100, LogLevel.Information, "AI HTTP request payload {Method} {RequestUri} ContentType={ContentType} Truncated={Truncated}: {Body}")]
    private static partial void LogRequestPayload(ILogger logger, string method, string requestUri, string? contentType, bool truncated, string body);

    [LoggerMessage(4101, LogLevel.Information, "AI HTTP response payload {Method} {RequestUri} StatusCode={StatusCode} ContentType={ContentType} Truncated={Truncated}: {Body}")]
    private static partial void LogResponsePayload(ILogger logger, string method, string requestUri, int statusCode, string? contentType, bool truncated, string body);

    private sealed record CapturedPayload(string Value, bool Truncated);
}
