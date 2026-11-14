using System.Text.Json;

namespace Agentstration.Http.Diagnostics;

internal sealed class BodyCapture(int maximumBytes)
{
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "accesstoken", "apikey", "authorization", "clientsecret", "credential", "credentials", "password",
        "refreshtoken", "secret", "secretaccess", "secretcapability", "secretvaluebase64", "token",
        "code", "pairingcode", "cookie", "setcookie", "assertion", "idtoken", "key", "privatekey"
    };
    private readonly MemoryStream buffer = new();
    private bool oversized;
    private bool nonSequential;
    internal long ObservedBytes { get; private set; }

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        ObservedBytes += bytes.Length;
        if (oversized) return;
        if (buffer.Length + bytes.Length > maximumBytes)
        {
            oversized = true;
            buffer.SetLength(0);
            return;
        }
        buffer.Write(bytes);
    }

    internal void Invalidate() => nonSequential = true;

    internal CapturedBody Finish(string? contentType, bool encoded, bool complete, bool isHealthResponse = false)
    {
        if (oversized) return new("[body omitted: size limit exceeded]", true);
        if (!complete || nonSequential) return new("[body omitted: incomplete or non-sequential stream]", false);
        if (buffer.Length == 0) return new("[empty body]", false);
        if (!encoded && isHealthResponse &&
            string.Equals(contentType?.Split(';', 2)[0].Trim(), "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            var health = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            return health is "Healthy" or "Degraded" or "Unhealthy"
                ? new(health, false) : new("[body omitted: unrecognized health response]", false);
        }
        if (encoded || !IsJson(contentType)) return new("[body omitted: unsupported content type or encoding]", false);
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            using var redacted = new MemoryStream();
            using (var writer = new Utf8JsonWriter(redacted)) Redact(writer, document.RootElement);
            var text = System.Text.Encoding.UTF8.GetString(redacted.ToArray());
            // Redaction can enlarge a small value; bound the exported value too.
            return System.Text.Encoding.UTF8.GetByteCount(text) > maximumBytes
                ? new("[body omitted: size limit exceeded after redaction]", true) : new(text, false);
        }
        catch (JsonException)
        {
            return new("[body omitted: invalid JSON]", false);
        }
    }

    internal static bool IsJson(string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        return mediaType is not null && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsHealthPath(string path) => path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSensitivePath(string path)
    {
        var decoded = Uri.UnescapeDataString(path);
        return decoded.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment =>
        {
            var normalized = Normalize(segment);
            return SensitiveNames.Contains(normalized) || normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                normalized is "account" or "accounts" or "identity" or "auth" or "authentication" or "bootstrap" or
                    "login" or "logout" or "signin" or "signout" or "pairing" or "enrollment" or "redeem";
        });
    }

    private static void Redact(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (SensitiveNames.Contains(Normalize(property.Name))) writer.WriteStringValue("***REDACTED***");
                else Redact(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in element.EnumerateArray()) Redact(writer, child);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit));
}

internal sealed record CapturedBody(string Body, bool Truncated);
