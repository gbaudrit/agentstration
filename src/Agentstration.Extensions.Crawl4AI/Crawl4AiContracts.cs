namespace Agentstration.Extensions.Crawl4AI;

public sealed record AcquiredContentReference(
    string Reference,
    string SourceUrl,
    string MediaType,
    long Length,
    string Sha256,
    IReadOnlyList<string> Links,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record WebFetchResult(string CorrelationId, AcquiredContentReference Content);

public sealed record WebCrawlResult(
    string CorrelationId,
    string StartUrl,
    int MaximumDepth,
    int MaximumPages,
    IReadOnlyList<AcquiredContentReference> Contents,
    bool Truncated);

public sealed record ContentReadResult(string Reference, long Offset, string ContentBase64, bool EndOfContent);

public sealed class Crawl4AiException(string code, string message, Exception? innerException = null) : Exception($"{code}: {message}", innerException)
{
    public string Code { get; } = code;
}
