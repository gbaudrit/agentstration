using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Agentstration.Extensions.Crawl4AI;

[McpServerToolType]
public sealed class Crawl4AiTools(Crawl4AiAcquisitionService acquisition)
{
    [McpServerTool(Name = "web_fetch", UseStructuredContent = true), Description("Acquire one allowlisted web page through Crawl4AI and return an opaque temporary content reference. The document body is never embedded in the tool result.")]
    public Task<WebFetchResult> FetchAsync(
        [Description("Absolute HTTP(S) page URL.")] string url,
        [Description("Optional caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default) => acquisition.FetchAsync(url, correlationId, cancellationToken);

    [McpServerTool(Name = "web_crawl", UseStructuredContent = true), Description("Acquire a bounded breadth-first set of allowlisted web pages and return both page references and one bounded normalized corpus reference.")]
    public Task<WebCrawlResult> CrawlAsync(
        [Description("Absolute HTTP(S) starting URL.")] string startUrl,
        [Description("Optional crawl depth, bounded by extension configuration.")] int? maximumDepth = null,
        [Description("Optional page count, bounded by extension configuration.")] int? maximumPages = null,
        [Description("Optional caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default) => acquisition.CrawlAsync(startUrl, maximumDepth, maximumPages, correlationId, cancellationToken);

    [McpServerTool(Name = "content_extract", UseStructuredContent = true), Description("Extract normalized text from a temporary acquired content reference and return a new temporary content reference.")]
    public Task<WebFetchResult> ExtractAsync(
        [Description("Opaque content reference returned by this extension.")] string contentReference,
        [Description("Optional caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default) => acquisition.ExtractAsync(contentReference, correlationId, cancellationToken);

    [McpServerTool(Name = "content_read", UseStructuredContent = true), Description("Read one bounded base64 chunk from an opaque temporary content reference for transfer to a governed StagedArtifact writer.")]
    public Task<ContentReadResult> ReadAsync(
        [Description("Opaque content reference returned by this extension.")] string contentReference,
        [Description("Zero-based byte offset.")] long offset,
        [Description("Optional chunk size, bounded by extension configuration.")] int? maximumBytes = null,
        CancellationToken cancellationToken = default) => acquisition.ReadAsync(contentReference, offset, maximumBytes, cancellationToken);

    [McpServerTool(Name = "content_delete", UseStructuredContent = true), Description("Delete temporary acquired content after transfer or abandonment. This operation is idempotent.")]
    public Task<bool> DeleteAsync(
        [Description("Opaque content reference returned by this extension.")] string contentReference,
        CancellationToken cancellationToken = default) => acquisition.DeleteAsync(contentReference, cancellationToken);
}
