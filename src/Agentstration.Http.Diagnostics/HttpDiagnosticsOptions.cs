namespace Agentstration.Http.Diagnostics;

public sealed class HttpDiagnosticsOptions
{
    public const string SectionName = "Observability:Http";
    public bool Enabled { get; init; }
    public bool LogMetadata { get; init; } = true;
    public bool CaptureRequestBody { get; init; }
    public bool CaptureResponseBody { get; init; }
    public int MaximumBodyBytes { get; init; } = 16_384;
    public string[] IncludedPaths { get; init; } = [];
    public string[] ExcludedPaths { get; init; } = [];
    public string[] IncludedHosts { get; init; } = [];
    public string[] ExcludedHosts { get; init; } = [];
    public Dictionary<HttpDiagnosticCategory, HttpCaptureLevel> Categories { get; init; } = [];

    internal void Validate(bool isDevelopment)
    {
        if (MaximumBodyBytes is < 256 or > 1_048_576)
            throw new InvalidOperationException("Observability:Http:MaximumBodyBytes must be between 256 and 1048576 bytes.");
        if (Categories is null || Categories.Any(entry => !Enum.IsDefined(entry.Key) || !Enum.IsDefined(entry.Value)))
            throw new InvalidOperationException("Observability:Http:Categories requires known HTTP categories and capture levels.");
        if (Enabled && (CaptureRequestBody || CaptureResponseBody ||
            Categories.Values.Any(level => level is HttpCaptureLevel.RequestBody or HttpCaptureLevel.ResponseBody or HttpCaptureLevel.Bodies)) && !isDevelopment)
            throw new InvalidOperationException("Observability:Http body capture may only be enabled in Development.");
        if (IncludedPaths is null || ExcludedPaths is null || IncludedHosts is null || ExcludedHosts is null ||
            IncludedPaths.Concat(ExcludedPaths).Any(path => string.IsNullOrWhiteSpace(path) || !path.StartsWith('/')) ||
            IncludedHosts.Concat(ExcludedHosts).Any(host => string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown))
            throw new InvalidOperationException("Observability:Http filters require absolute path prefixes and exact host names.");
    }

    internal bool Includes(string path, string? host = null) => Enabled &&
        (IncludedPaths.Length == 0 || IncludedPaths.Any(prefix => MatchesPath(path, prefix))) &&
        !ExcludedPaths.Any(prefix => MatchesPath(path, prefix)) &&
        (host is null || ((IncludedHosts.Length == 0 || IncludedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) &&
            !ExcludedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)));

    internal HttpCapturePolicy Resolve(string path)
    {
        var category = BodyCapture.IsHealthPath(path) ? HttpDiagnosticCategory.Health
            : MatchesPath(path, "/api/awp") ? HttpDiagnosticCategory.Awp
            : MatchesPath(path, "/aep") || MatchesPath(path, "/api/aep") || path.Equals("/.well-known/aep", StringComparison.OrdinalIgnoreCase)
                ? HttpDiagnosticCategory.Aep
            : MatchesPath(path, "/bff") ? HttpDiagnosticCategory.Bff
            : MatchesPath(path, "/api") ? HttpDiagnosticCategory.Api
            : HttpDiagnosticCategory.Other;
        if (!Categories.TryGetValue(category, out var level))
            return new(category, true, LogMetadata, CaptureRequestBody, CaptureResponseBody);
        return new(category, level != HttpCaptureLevel.Off, level != HttpCaptureLevel.Off,
            level is HttpCaptureLevel.RequestBody or HttpCaptureLevel.Bodies,
            level is HttpCaptureLevel.ResponseBody or HttpCaptureLevel.Bodies);
    }

    private static bool MatchesPath(string path, string prefix) => prefix == "/" ||
        path.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
}

public enum HttpDiagnosticCategory { Health, Api, Awp, Aep, Bff, Other }

public enum HttpCaptureLevel { Off, Metadata, RequestBody, ResponseBody, Bodies }

internal readonly record struct HttpCapturePolicy(HttpDiagnosticCategory Category, bool Enabled, bool LogMetadata,
    bool CaptureRequestBody, bool CaptureResponseBody);
