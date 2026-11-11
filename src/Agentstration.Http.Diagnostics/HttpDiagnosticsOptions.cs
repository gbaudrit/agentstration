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

    internal void Validate(bool isDevelopment)
    {
        if (MaximumBodyBytes is < 256 or > 1_048_576)
            throw new InvalidOperationException("Observability:Http:MaximumBodyBytes must be between 256 and 1048576 bytes.");
        if (Enabled && (CaptureRequestBody || CaptureResponseBody) && !isDevelopment)
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

    private static bool MatchesPath(string path, string prefix) => prefix == "/" ||
        path.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
}
