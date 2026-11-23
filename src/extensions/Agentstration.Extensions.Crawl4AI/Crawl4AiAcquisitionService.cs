using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Crawl4AI;

public sealed class Crawl4AiAcquisitionService(
    Crawl4AiClient client,
    ICrawlContentStore contentStore,
    IOptions<Crawl4AiOptions> options)
{
    private readonly Crawl4AiOptions options = options.Value;

    public async Task<WebFetchResult> FetchAsync(string url, string? correlationId, CancellationToken cancellationToken)
    {
        var correlation = NormalizeCorrelation(correlationId);
        var page = await client.FetchAsync(url, correlation, cancellationToken);
        return new WebFetchResult(correlation, await StoreAsync(page, cancellationToken));
    }

    public async Task<WebCrawlResult> CrawlAsync(
        string startUrl,
        int? maximumDepth,
        int? maximumPages,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var depthLimit = maximumDepth ?? options.MaximumDepth;
        var pageLimit = maximumPages ?? options.MaximumPages;
        if (depthLimit is < 0 || depthLimit > options.MaximumDepth)
            throw new Crawl4AiException("crawl4ai_depth_invalid", $"Maximum depth must be between 0 and {options.MaximumDepth}.");
        if (pageLimit is < 1 || pageLimit > options.MaximumPages)
            throw new Crawl4AiException("crawl4ai_page_limit_invalid", $"Maximum pages must be between 1 and {options.MaximumPages}.");

        var correlation = NormalizeCorrelation(correlationId);
        var queue = new Queue<(string Url, int Depth)>();
        queue.Enqueue((startUrl, 0));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startUrl };
        var contents = new List<AcquiredContentReference>();
        var corpus = new StringBuilder();
        var corpusBytes = 0;
        var discoveredBeyondLimit = false;
        var skippedPage = false;
        try
        {
            while (queue.Count > 0 && contents.Count < pageLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = queue.Dequeue();
                if (!visited.Add(current.Url)) continue;
                CrawlPage page;
                try
                {
                    page = await client.FetchAsync(current.Url, correlation, cancellationToken);
                }
                catch (Crawl4AiException) when (current.Depth > 0)
                {
                    skippedPage = true;
                    continue;
                }
                visited.Add(page.Url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped));
                contents.Add(await StoreAsync(page, cancellationToken));
                AppendCorpusPage(corpus, page, ref corpusBytes);
                if (current.Depth >= depthLimit) continue;
                foreach (var link in page.Links)
                {
                    var canonical = link.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
                    if (visited.Contains(canonical) || !scheduled.Add(canonical)) continue;
                    if (scheduled.Count > pageLimit)
                    {
                        discoveredBeyondLimit = true;
                        break;
                    }
                    queue.Enqueue((canonical, current.Depth + 1));
                }
            }

            var corpusStored = await contentStore.WriteAsync(
                Encoding.UTF8.GetBytes(corpus.ToString()), startUrl, "text/markdown; charset=utf-8", cancellationToken);
            var corpusReference = new AcquiredContentReference(
                corpusStored.Reference,
                startUrl,
                "text/markdown; charset=utf-8",
                corpusStored.Length,
                corpusStored.Sha256,
                contents.Select(content => content.SourceUrl).ToArray(),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["pageCount"] = contents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["kind"] = "crawl-corpus"
                });
            return new WebCrawlResult(correlation, startUrl, depthLimit, pageLimit, contents, corpusReference,
                discoveredBeyondLimit || queue.Count > 0 || skippedPage);
        }
        catch
        {
            foreach (var content in contents)
            {
                try
                {
                    await contentStore.DeleteAsync(content.Reference, CancellationToken.None);
                }
                catch
                {
                    // Preserve the acquisition failure; orphaned bounded content is removed by store retention.
                }
            }
            throw;
        }
    }

    public async Task<WebFetchResult> ExtractAsync(
        string contentReference,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var source = await contentStore.ReadAllAsync(contentReference, cancellationToken);
        var text = Encoding.UTF8.GetString(source.Content);
        if (source.MediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) text = ExtractHtmlText(text);
        else if (!source.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(source.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new Crawl4AiException("crawl4ai_media_type_unsupported", $"Media type '{source.MediaType}' cannot be extracted as text.");
        text = NormalizeText(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var stored = await contentStore.WriteAsync(bytes, source.SourceUrl, "text/plain; charset=utf-8", cancellationToken);
        var content = new AcquiredContentReference(
            stored.Reference,
            source.SourceUrl,
            "text/plain; charset=utf-8",
            stored.Length,
            stored.Sha256,
            [],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["derivedFrom"] = contentReference });
        return new WebFetchResult(NormalizeCorrelation(correlationId), content);
    }

    public Task<ContentReadResult> ReadAsync(string contentReference, long offset, int? maximumBytes, CancellationToken cancellationToken) =>
        contentStore.ReadAsync(contentReference, offset, maximumBytes, cancellationToken);

    public async Task<bool> DeleteAsync(string contentReference, CancellationToken cancellationToken)
    {
        await contentStore.DeleteAsync(contentReference, cancellationToken);
        return true;
    }

    private async Task<AcquiredContentReference> StoreAsync(CrawlPage page, CancellationToken cancellationToken)
    {
        var stored = await contentStore.WriteAsync(page.Content, page.Url.AbsoluteUri, page.MediaType, cancellationToken);
        return new AcquiredContentReference(
            stored.Reference,
            page.Url.AbsoluteUri,
            page.MediaType,
            stored.Length,
            stored.Sha256,
            page.Links.Select(link => link.AbsoluteUri).ToArray(),
            page.Metadata);
    }

    private void AppendCorpusPage(StringBuilder corpus, CrawlPage page, ref int corpusBytes)
    {
        var text = Encoding.UTF8.GetString(page.Content);
        if (page.MediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) text = ExtractHtmlText(text);
        text = NormalizeText(text);
        var section = $"\n\n---\n\nSource: {page.Url.AbsoluteUri}\n\n{text}";
        corpusBytes = checked(corpusBytes + Encoding.UTF8.GetByteCount(section));
        if (corpusBytes > options.MaximumContentBytes)
            throw new Crawl4AiException("crawl4ai_corpus_too_large",
                $"The normalized crawl corpus exceeds the configured {options.MaximumContentBytes}-byte content limit.");
        corpus.Append(section);
    }

    private static string ExtractHtmlText(string html)
    {
        var builder = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var character in html)
        {
            if (character == '<') { inTag = true; builder.Append(' '); }
            else if (character == '>') inTag = false;
            else if (!inTag) builder.Append(character);
        }
        return WebUtility.HtmlDecode(builder.ToString());
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWhitespace = false;
        foreach (var character in value)
        {
            var whitespace = char.IsWhiteSpace(character);
            if (!whitespace || !previousWhitespace) builder.Append(whitespace ? ' ' : character);
            previousWhitespace = whitespace;
        }
        return builder.ToString().Trim();
    }

    private static string NormalizeCorrelation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.NewGuid().ToString("N");
        var normalized = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').Take(128).ToArray());
        return normalized.Length == 0 ? Guid.NewGuid().ToString("N") : normalized;
    }
}
