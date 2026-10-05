using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Sources.Contracts;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Knowledge;

internal sealed record KnowledgeHttpFetchRequest(
    Uri Url,
    int MaximumBytes,
    int MaximumRedirects,
    TimeSpan Timeout,
    IReadOnlySet<string> AllowedMediaTypes);

internal sealed record KnowledgeHttpFetchResult(Uri FinalUrl, string FileName, string MediaType, byte[] Content);

internal interface IKnowledgeHttpContentFetcher
{
    Task<KnowledgeHttpFetchResult> FetchAsync(KnowledgeHttpFetchRequest request, CancellationToken cancellationToken);
}

internal sealed class SafeKnowledgeHttpContentFetcher(HttpClient client) : IKnowledgeHttpContentFetcher
{
    public async Task<KnowledgeHttpFetchResult> FetchAsync(
        KnowledgeHttpFetchRequest request,
        CancellationToken cancellationToken)
    {
        ValidateUrl(request.Url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        var current = request.Url;
        for (var redirect = 0; ; redirect++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.ConnectionClose = true;
            using var response = await SendAsync(message, timeout.Token, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect >= request.MaximumRedirects)
                    throw Error("knowledge_http_redirect_limit", "The source exceeded the redirect limit.");
                if (response.Headers.Location is null)
                    throw Error("knowledge_http_redirect_invalid", "The source returned a redirect without a Location header.");
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                ValidateUrl(next);
                if (string.Equals(current.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(next.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    throw Error("knowledge_http_redirect_downgrade", "An HTTPS source cannot redirect to HTTP.");
                current = next;
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw Error("knowledge_http_status_invalid", $"The source returned HTTP {(int)response.StatusCode}.");
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
                ?? throw Error("knowledge_http_media_type_missing", "The source response must declare a media type.");
            if (!request.AllowedMediaTypes.Contains(mediaType))
                throw Error("knowledge_http_media_type_invalid", $"Media type '{mediaType}' is not allowed for this profile.");
            if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset
                && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                throw Error("knowledge_http_charset_invalid", "Textual source responses must use UTF-8.");
            if (response.Content.Headers.ContentLength is > 0
                && response.Content.Headers.ContentLength > request.MaximumBytes)
                throw Error("knowledge_http_size_limit", $"The source response exceeds {request.MaximumBytes} bytes.");
            byte[] content;
            try { content = await ReadBoundedAsync(response.Content, request.MaximumBytes, timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw Error("knowledge_http_timeout", "The source request timed out."); }
            return new(current, SafeFileName(current, mediaType), mediaType, content);
        }
    }

    internal static HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectPublicAsync
    };

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var allowed = addresses.Where(address => SourceRegistryNetworkPolicy.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0) throw new KnowledgeHttpEndpointPolicyException();
        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (exception is OperationCanceledException) throw;
                last = exception;
            }
        }
        throw new HttpRequestException("The source could not be reached through an allowed network address.", last);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        { throw Error("knowledge_http_timeout", "The source request timed out."); }
        catch (HttpRequestException exception) when (ContainsPolicyFailure(exception))
        { throw Error("knowledge_http_endpoint_denied", "The source resolved only to denied network addresses.", exception); }
        catch (HttpRequestException exception)
        { throw Error("knowledge_http_unavailable", "The source request failed.", exception); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) return output.ToArray();
                if (output.Length + read > maximumBytes)
                    throw Error("knowledge_http_size_limit", $"The source response exceeds {maximumBytes} bytes.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ValidateUrl(Uri url)
    {
        if (!url.IsAbsoluteUri
            || !(string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Fragment))
            throw Error("knowledge_http_url_invalid", "The source URL must be an absolute HTTP(S) URL without credentials or a fragment.");
        if (IPAddress.TryParse(url.IdnHost, out var address)
            && !SourceRegistryNetworkPolicy.IsAddressAllowed(address, false))
            throw Error("knowledge_http_endpoint_denied", "The source URL targets a denied network address.");
    }

    private static string SafeFileName(Uri url, string mediaType)
    {
        var value = Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath));
        if (string.IsNullOrWhiteSpace(value)) value = mediaType switch
        {
            "application/json" => "response.json",
            "application/xml" => "response.xml",
            "text/html" => "index.html",
            "text/markdown" => "content.md",
            _ => "content.txt"
        };
        value = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return value.Length <= 240 ? value : value[..240];
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;
    private static bool ContainsPolicyFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is KnowledgeHttpEndpointPolicyException) return true;
        return false;
    }
    private static ToolDefinitionInvocationException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
    private sealed class KnowledgeHttpEndpointPolicyException : Exception;
}

internal abstract class KnowledgeHttpAcquisitionMcpTool(
    ArtifactManagementService artifacts,
    IKnowledgeHttpContentFetcher fetcher) : IInternalMcpToolHandler
{
    protected abstract string Name { get; }
    protected abstract string DisplayName { get; }
    protected abstract string Description { get; }
    protected abstract IReadOnlySet<string> AllowedMediaTypes { get; }

    public InternalMcpToolDefinition Definition => new(Name, DisplayName, Description,
        KnowledgeHttpSchemas.Input, KnowledgeHttpSchemas.Output,
        InitialCategory: KnowledgeBuiltinSchemas.Category, ExposeThroughMcp: false);

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        var configuration = RequiredObject(invocation.Arguments, "sourceConfiguration");
        var urlText = RequiredString(configuration, "url");
        if (urlText.Length > 2048 || !Uri.TryCreate(urlText, UriKind.Absolute, out var url))
            throw Error("knowledge_http_url_invalid", "sourceConfiguration.url must be an absolute HTTP(S) URL.");
        var result = await fetcher.FetchAsync(new(url, MaximumBytes: 1024 * 1024, MaximumRedirects: 3,
            Timeout: TimeSpan.FromSeconds(20), AllowedMediaTypes), cancellationToken);
        var producer = new ArtifactProducer
        {
            Kind = ArtifactProducerKind.FlowRun,
            Id = invocation.RunId ?? invocation.CallId,
            FlowRunId = invocation.RunId,
            FlowStepId = invocation.FlowStepId,
            ToolCallId = invocation.CallId,
            CorrelationId = invocation.CorrelationId
        };
        var staged = await artifacts.CreateStagedAsync(new(result.FileName, result.MediaType, producer), cancellationToken);
        long offset = 0;
        while (offset < result.Content.Length)
        {
            var length = Math.Min(ArtifactManagementService.MaximumChunkBytes, result.Content.Length - checked((int)offset));
            var updated = await artifacts.WriteAsync(staged.Value.ArtifactId, offset,
                result.Content.AsMemory(checked((int)offset), length), cancellationToken);
            offset = updated.Value.Length;
        }
        _ = await artifacts.SealAsync(staged.Value.ArtifactId, cancellationToken);
        return JsonSerializer.SerializeToElement(new
        {
            stagedArtifactId = staged.Value.ArtifactId.ToString(),
            producerFlowRunId = invocation.RunId ?? invocation.CallId,
            producerFlowStepId = invocation.FlowStepId ?? Name,
            fileName = result.FileName,
            mediaType = result.MediaType,
            sourceUrl = result.FinalUrl.AbsoluteUri
        });
    }

    private static JsonElement RequiredObject(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Object ? property : throw Error("knowledge_http_configuration_invalid", $"Argument '{name}' must be an object.");
    private static string RequiredString(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
        ? property.GetString()! : throw Error("knowledge_http_configuration_invalid", $"Property '{name}' is required.");
    private static ToolDefinitionInvocationException Error(string code, string message) => new(code, message);
}

internal sealed class KnowledgeWebFetchMcpTool(ArtifactManagementService artifacts, IKnowledgeHttpContentFetcher fetcher)
    : KnowledgeHttpAcquisitionMcpTool(artifacts, fetcher)
{
    public const string ToolName = "knowledge.ingestion.web.fetch";
    protected override string Name => ToolName;
    protected override string DisplayName => "Fetch a Web resource";
    protected override string Description => "Fetches one bounded public HTTP(S) Web resource into governed staging.";
    protected override IReadOnlySet<string> AllowedMediaTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "text/html", "text/plain", "text/markdown", "application/json", "application/xml", "text/xml" };
}

internal sealed class KnowledgeRestGetMcpTool(ArtifactManagementService artifacts, IKnowledgeHttpContentFetcher fetcher)
    : KnowledgeHttpAcquisitionMcpTool(artifacts, fetcher)
{
    public const string ToolName = "knowledge.ingestion.rest.get";
    protected override string Name => ToolName;
    protected override string DisplayName => "Fetch a public REST resource";
    protected override string Description => "Performs one bounded unauthenticated public HTTP(S) GET into governed staging.";
    protected override IReadOnlySet<string> AllowedMediaTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "application/json", "application/xml", "text/xml", "text/plain" };
}

internal static class KnowledgeHttpSchemas
{
    public static JsonElement Input { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { sourceConfiguration = new { type = "object" } },
        required = new[] { "sourceConfiguration" },
        additionalProperties = false
    });
    public static JsonElement Output { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            stagedArtifactId = new { type = "string" },
            producerFlowRunId = new { type = "string" },
            producerFlowStepId = new { type = "string" },
            fileName = new { type = "string" },
            mediaType = new { type = "string" },
            sourceUrl = new { type = "string" }
        },
        required = new[] { "stagedArtifactId", "producerFlowRunId", "producerFlowStepId", "fileName", "mediaType", "sourceUrl" },
        additionalProperties = false
    });
}
