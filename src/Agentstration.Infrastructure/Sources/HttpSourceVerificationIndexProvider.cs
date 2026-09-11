using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agentstration.Management.Abstractions;

namespace Agentstration.Infrastructure.Sources;

public sealed class HttpSourceVerificationIndexProvider(
    HttpClient client,
    SourceVerificationIndexOptions options,
    ISourceVerificationIndexReader reader) : ISourceVerificationIndexProvider
{
    public async Task<VerifiedSourceIndexManifest?> GetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Url)) return null;
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new SourceValidationException("source_verification_index_url_invalid", "The verification index URL must be absolute HTTPS.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/yaml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new SourceRetrievalException("source_verification_index_transport_invalid", "The verification index request must remain on HTTPS after redirects.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new SourceRetrievalException("source_verification_index_http_error", $"The verification index returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > options.MaximumBytes)
            throw new SourceRetrievalException("source_verification_index_size_limit", $"The verification index exceeds {options.MaximumBytes} bytes.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var content = new MemoryStream(Math.Min(options.MaximumBytes, 81920));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (content.Length + read > options.MaximumBytes)
                throw new SourceRetrievalException("source_verification_index_size_limit", $"The verification index exceeds {options.MaximumBytes} bytes.");
            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return reader.Read(Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length)));
    }
}
