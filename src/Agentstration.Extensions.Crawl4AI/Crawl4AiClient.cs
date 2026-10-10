using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Crawl4AI;

public sealed record CrawlPage(
    Uri Url,
    string MediaType,
    byte[] Content,
    IReadOnlyList<Uri> Links,
    IReadOnlyDictionary<string, string> Metadata);

public sealed class Crawl4AiClient(
    HttpClient httpClient,
    DestinationPolicy destinationPolicy,
    ICrawl4AiTokenProvider tokenProvider,
    IOptions<Crawl4AiOptions> options)
{
    private readonly Crawl4AiOptions options = options.Value;

    public async Task<CrawlPage> FetchAsync(string url, string correlationId, CancellationToken cancellationToken)
    {
        var destination = await destinationPolicy.ValidateAsync(url, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Endpoint, "/crawl"))
        {
            Content = JsonContent.Create(new { urls = new[] { destination.AbsoluteUri } })
        };
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", NormalizeCorrelation(correlationId));
        if (await tokenProvider.GetTokenAsync(timeout.Token) is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new Crawl4AiException("crawl4ai_upstream_error", $"Crawl4AI returned HTTP {(int)response.StatusCode}.");
        await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = await ReadBoundedAsync(responseStream, options.MaximumResponseBytes, timeout.Token);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return await ParsePageAsync(document.RootElement, destination, timeout.Token);
        }
        catch (JsonException exception)
        {
            throw new Crawl4AiException("crawl4ai_response_invalid", "Crawl4AI returned an invalid JSON response.", exception);
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(10, options.RequestTimeoutSeconds)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Endpoint, "/health"));
            if (await tokenProvider.GetTokenAsync(timeout.Token) is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Crawl4AiException)
        {
            return false;
        }
    }

    private async Task<CrawlPage> ParsePageAsync(JsonElement root, Uri requested, CancellationToken cancellationToken)
    {
        var page = SelectPage(root);
        if (page.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new Crawl4AiException("crawl4ai_acquisition_failed", ReadProviderError(page));

        var finalUrlText = GetString(page, "url") ?? requested.AbsoluteUri;
        var finalUrl = await destinationPolicy.ValidateAsync(finalUrlText, cancellationToken);
        var (content, mediaType) = ReadContent(page);
        if (!options.AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            throw new Crawl4AiException("crawl4ai_media_type_denied", $"Crawl4AI content media type '{mediaType}' is not allowed.");
        var contentBytes = Encoding.UTF8.GetBytes(content);
        if (contentBytes.Length > options.MaximumContentBytes)
            throw new Crawl4AiException("crawl4ai_content_too_large", "The acquired content exceeds the configured content limit.");
        var links = await ReadLinksAsync(page, finalUrl, cancellationToken);
        return new CrawlPage(finalUrl, mediaType, contentBytes, links, ReadMetadata(page));
    }

    private static JsonElement SelectPage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.GetArrayLength() > 0 ? root[0] : throw new Crawl4AiException("crawl4ai_response_invalid", "Crawl4AI returned no result.");
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            return results.GetArrayLength() > 0 ? results[0] : throw new Crawl4AiException("crawl4ai_response_invalid", "Crawl4AI returned no result.");
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object) return result;
        if (root.ValueKind == JsonValueKind.Object) return root;
        throw new Crawl4AiException("crawl4ai_response_invalid", "Crawl4AI returned an unsupported response shape.");
    }

    private static (string Content, string MediaType) ReadContent(JsonElement page)
    {
        if (page.TryGetProperty("markdown", out var markdown))
        {
            if (markdown.ValueKind == JsonValueKind.String) return (markdown.GetString() ?? string.Empty, "text/markdown");
            if (markdown.ValueKind == JsonValueKind.Object)
            {
                var value = GetString(markdown, "raw_markdown") ?? GetString(markdown, "fit_markdown");
                if (value is not null) return (value, "text/markdown");
            }
        }
        if (GetString(page, "cleaned_html") is { } cleaned) return (cleaned, "text/html");
        if (GetString(page, "html") is { } html) return (html, "text/html");
        throw new Crawl4AiException("crawl4ai_response_invalid", "Crawl4AI returned no supported page content.");
    }

    private async Task<IReadOnlyList<Uri>> ReadLinksAsync(JsonElement page, Uri baseUri, CancellationToken cancellationToken)
    {
        if (!page.TryGetProperty("links", out var links)) return [];
        var candidates = new List<string>();
        AddLinkValues(links, candidates);
        if (links.ValueKind == JsonValueKind.Object)
        {
            if (links.TryGetProperty("internal", out var internalLinks)) AddLinkValues(internalLinks, candidates);
            if (links.TryGetProperty("external", out var externalLinks)) AddLinkValues(externalLinks, candidates);
        }
        var accepted = new List<Uri>();
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal).Take(options.MaximumLinksPerPage))
        {
            if (!Uri.TryCreate(baseUri, candidate, out var resolved)) continue;
            try { accepted.Add(await destinationPolicy.ValidateAsync(resolved.AbsoluteUri, cancellationToken)); }
            catch (Crawl4AiException exception) when (exception.Code is "crawl4ai_domain_denied" or "crawl4ai_port_denied" or "crawl4ai_destination_denied" or "crawl4ai_url_invalid") { }
        }
        return accepted;
    }

    private static void AddLinkValues(JsonElement element, ICollection<string> destination)
    {
        if (element.ValueKind != JsonValueKind.Array) return;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text) destination.Add(text);
            else if (item.ValueKind == JsonValueKind.Object && GetString(item, "href") is { } href) destination.Add(href);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata(JsonElement page)
    {
        if (!page.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in metadata.EnumerateObject())
        {
            if (result.Count == 32) break;
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) continue;
            var name = property.Name[..Math.Min(property.Name.Length, 128)];
            result.TryAdd(name, TruncateMetadataValue(property.Value));
        }
        return result;
    }

    private static string TruncateMetadataValue(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        return text[..Math.Min(text.Length, 1024)];
    }

    private static string ReadProviderError(JsonElement page)
    {
        var value = GetString(page, "error_message") ?? GetString(page, "error") ?? "Crawl4AI could not acquire the requested page.";
        return value[..Math.Min(value.Length, 512)];
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string NormalizeCorrelation(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.NewGuid().ToString("N");
        return new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').Take(128).ToArray());
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new Crawl4AiException("crawl4ai_response_too_large", "Crawl4AI returned a response larger than the configured limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
