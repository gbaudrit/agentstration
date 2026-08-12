using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Agentstration.Aep.Client;

public sealed record AepHttpTrace(
    DateTimeOffset Timestamp,
    string Method,
    Uri? Url,
    int? StatusCode,
    TimeSpan Duration,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string? RequestBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string? ResponseBody);

public interface IAepHttpTraceSink
{
    ValueTask RecordAsync(AepHttpTrace trace, CancellationToken cancellationToken = default);
}

public sealed class AepTracingHandler(IAepHttpTraceSink sink) : DelegatingHandler
{
    private const int MaximumBodyLength = 64 * 1024;
    private static readonly string[] SensitiveNames = ["authorization", "cookie", "token", "secret", "password", "apikey", "api-key"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null ? null : Redact(await request.Content.ReadAsStringAsync(cancellationToken));
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            return response;
        }
        finally
        {
            stopwatch.Stop();
            var isStreaming = response?.Content?.Headers.ContentType?.MediaType == "text/event-stream";
            var responseBody = response?.Content is null ? null : isStreaming ? "<streaming response>" : Redact(await response.Content.ReadAsStringAsync(cancellationToken));
            await sink.RecordAsync(new AepHttpTrace(
                startedAt,
                request.Method.Method,
                request.RequestUri,
                response is null ? null : (int)response.StatusCode,
                stopwatch.Elapsed,
                Headers(request.Headers.Select(value => (value.Key, value.Value))),
                requestBody,
                response is null ? new Dictionary<string, string>() : Headers(response.Headers.Concat(response.Content.Headers).Select(value => (value.Key, value.Value))),
                responseBody), cancellationToken);
        }
    }

    private static Dictionary<string, string> Headers(IEnumerable<(string Key, IEnumerable<string> Value)> headers) =>
        headers.ToDictionary(
            value => value.Key,
            value => IsSensitive(value.Key) ? "***" : string.Join(", ", value.Value),
            StringComparer.OrdinalIgnoreCase);

    private static string Redact(string value)
    {
        if (value.Length == 0) return value;
        try
        {
            using var document = JsonDocument.Parse(value);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteRedacted(writer, document.RootElement);
            return Limit(Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (JsonException) { return Limit(value); }
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element, string? propertyName = null)
    {
        if (propertyName is not null && IsSensitive(propertyName)) { writer.WriteStringValue("***"); return; }
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()) { writer.WritePropertyName(property.Name); WriteRedacted(writer, property.Value, property.Name); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteRedacted(writer, item);
                writer.WriteEndArray();
                break;
            default: element.WriteTo(writer); break;
        }
    }

    private static bool IsSensitive(string name) => SensitiveNames.Any(value => name.Contains(value, StringComparison.OrdinalIgnoreCase));
    private static string Limit(string value) => value.Length <= MaximumBodyLength ? value : value[..MaximumBodyLength] + "…";
}
