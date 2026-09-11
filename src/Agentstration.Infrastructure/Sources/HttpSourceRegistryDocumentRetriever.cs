using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Infrastructure.Sources;

public sealed class HttpSourceRegistryDocumentRetriever(
    HttpClient client,
    SourceRegistryTransportOptions options,
    ISecretResolver? secrets = null) : ISourceRegistryDocumentRetriever
{
    private static readonly HttpRequestOptionsKey<SourceRegistryEndpointPolicy> EndpointPolicyKey = new("Agentstration.SourceRegistry.EndpointPolicy");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/yaml",
        "application/x-yaml",
        "text/yaml"
    };

    public async Task<RetrievedSourceRegistryDocument> RetrieveAsync(
        Uri source,
        string? etag,
        DateTimeOffset? lastModified,
        int maximumBytes,
        SourceRegistryRetrievalContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateEndpoint(source, context.EndpointPolicy);
        if (maximumBytes is < 1 or > SourceRegistryLimits.MaximumDocumentBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var current = source;
        for (var redirect = 0; ; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Options.Set(EndpointPolicyKey, context.EndpointPolicy);
            request.Headers.ConnectionClose = true;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/yaml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-yaml"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/yaml"));
            if (etag is not null)
            {
                if (!EntityTagHeaderValue.TryParse(etag, out var parsed))
                    throw new SourceRetrievalException("source_registry_validator_invalid", "The cached registry ETag is invalid.");
                request.Headers.IfNoneMatch.Add(parsed);
            }
            if (lastModified is not null) request.Headers.IfModifiedSince = lastModified;

            using var credential = await ResolveCredentialAsync(context, cancellationToken);
            if (credential is not null)
            {
                string token;
                try { token = StrictUtf8.GetString(credential.Value.AccessValue().Span); }
                catch (DecoderFallbackException exception)
                {
                    throw new SourceRetrievalException("source_registry_credential_invalid", "The registry credential is not a valid Bearer token.", exception);
                }
                if (token.Length is < 1 or > 8192 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
                    throw new SourceRetrievalException("source_registry_credential_invalid", "The registry credential is not a valid Bearer token.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await SendAsync(request, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect >= options.MaximumRedirects)
                    throw new SourceRetrievalException("source_registry_redirect_limit", "The registry exceeded the redirect limit.");
                if (response.Headers.Location is null)
                    throw new SourceRetrievalException("source_registry_redirect_invalid", "The registry returned a redirect without a Location header.");
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                ValidateEndpoint(next, context.EndpointPolicy);
                if (!SameOrigin(source, next))
                    throw new SourceRetrievalException("source_registry_redirect_origin_invalid", "Registry redirects must remain on the configured origin.");
                current = next;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new(source, current, FileName(current), response.Headers.ETag?.ToString() ?? etag,
                    response.Content.Headers.LastModified ?? lastModified, true, null);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new SourceRetrievalException("source_registry_http_error", $"The registry returned HTTP {(int)response.StatusCode}.");

            ValidateMediaType(response.Content.Headers.ContentType, current);
            if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
                throw new SourceRetrievalException("source_registry_size_limit", $"The registry document exceeds {maximumBytes} bytes.");
            var bytes = await ReadBoundedAsync(response.Content, maximumBytes, cancellationToken);
            string content;
            try { content = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException exception)
            {
                throw new SourceRetrievalException("source_registry_encoding_invalid", "Registry documents must be valid UTF-8.", exception);
            }
            return new(source, current, FileName(current), response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified, false, content);
        }
    }

    internal static HttpMessageHandler CreatePrimaryHandler(SourceRegistryTransportOptions options)
    {
        options.Validate();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromSeconds(options.PooledConnectionLifetimeSeconds),
            ConnectCallback = ConnectAllowedAsync
        };
    }

    private static async ValueTask<Stream> ConnectAllowedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var policy = context.InitialRequestMessage.Options.TryGetValue(EndpointPolicyKey, out var requestPolicy)
            ? requestPolicy
            : new SourceRegistryEndpointPolicy();
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var allowed = addresses.Where(address => SourceRegistryNetworkPolicy.IsAddressAllowed(address, policy.AllowPrivateNetwork)).ToArray();
        if (allowed.Length == 0)
            throw new SourceRegistryEndpointPolicyException();
        Exception? lastError = null;
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
                lastError = exception;
            }
        }
        throw new HttpRequestException("The registry could not be reached through an allowed network address.", lastError);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceRetrievalException("source_registry_timeout", "The registry request timed out.");
        }
        catch (HttpRequestException exception) when (ContainsEndpointPolicyFailure(exception))
        {
            throw new SourceRetrievalException("source_registry_endpoint_policy_denied", "The registry resolved only to addresses denied by its endpoint policy.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new SourceRetrievalException("source_registry_unavailable", "The registry request failed.", exception);
        }
    }

    private async Task<ResolvedSecret?> ResolveCredentialAsync(
        SourceRegistryRetrievalContext context,
        CancellationToken cancellationToken)
    {
        if (context.AuthenticationMode == SourceRegistryAuthenticationMode.None) return null;
        if (context.AuthenticationMode != SourceRegistryAuthenticationMode.StaticBearer
            || context.Credential is not { } credential
            || secrets is null)
            throw new SourceRetrievalException("source_registry_credential_unavailable", "The registry credential is unavailable.");
        try
        {
            var address = credential.Resolve(context.Consumer.Namespace, ResourceKinds.Secret);
            return await secrets.ResolveAsync(
                new SecretReference(address, credential.ScopeRef),
                new SecretResolutionContext(context.ConsumerScopeRef, context.Consumer),
                cancellationToken)
                ?? throw new SourceRetrievalException("source_registry_credential_unavailable", "The registry credential is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SourceRetrievalException) { throw; }
        catch (Exception exception)
        {
            throw new SourceRetrievalException("source_registry_credential_unavailable", "The registry credential is unavailable.", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
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
                    throw new SourceRetrievalException("source_registry_size_limit", $"The registry document exceeds {maximumBytes} bytes.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateMediaType(MediaTypeHeaderValue? contentType, Uri finalUrl)
    {
        if (contentType?.CharSet is { Length: > 0 } charset
            && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            throw new SourceRetrievalException("source_registry_charset_invalid", "Registry documents must use UTF-8.");
        var mediaType = contentType?.MediaType;
        if (mediaType is not null && AllowedMediaTypes.Contains(mediaType)) return;
        if ((mediaType is null or "application/octet-stream") && SupportedSuffix(finalUrl)) return;
        throw new SourceRetrievalException("source_registry_media_type_invalid", "The registry response media type is not supported.");
    }

    private static void ValidateEndpoint(Uri endpoint, SourceRegistryEndpointPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var permittedScheme = endpoint.Scheme == Uri.UriSchemeHttps
            || policy.AllowHttp && endpoint.Scheme == Uri.UriSchemeHttp;
        if (!endpoint.IsAbsoluteUri || !permittedScheme
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath.Contains('%'))
            throw new SourceRetrievalException("source_registry_url_invalid", "Registry URLs must use an authorized HTTP(S) origin without credentials, query, fragment, or percent-encoding.");
        if (IPAddress.TryParse(endpoint.IdnHost, out var address)
            && !SourceRegistryNetworkPolicy.IsAddressAllowed(address, policy.AllowPrivateNetwork))
            throw new SourceRetrievalException("source_registry_endpoint_policy_denied", "The registry URL targets an address denied by its endpoint policy.");
        if (!SupportedSuffix(endpoint))
            throw new SourceRetrievalException("source_registry_file_name_invalid", "Registry URLs must end in .json, .yaml, or .yml.");
    }

    private static bool SupportedSuffix(Uri uri) => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".json" or ".yaml" or ".yml";
    private static string FileName(Uri uri) => Path.GetFileName(uri.AbsolutePath);
    private static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port;
    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static bool ContainsEndpointPolicyFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SourceRegistryEndpointPolicyException) return true;
        return false;
    }

    private sealed class SourceRegistryEndpointPolicyException()
        : Exception("The registry endpoint is denied by policy.");

}
