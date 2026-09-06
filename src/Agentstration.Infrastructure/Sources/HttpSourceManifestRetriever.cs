using System.Buffers;
using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Infrastructure.Sources;

public sealed class HttpSourceManifestRetriever(HttpClient client) : ISourceManifestRetriever
{
    public async Task<RetrievedSourceManifest> RetrieveAsync(Uri source, CancellationToken cancellationToken)
    {
        Validate(source);
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new SourceRetrievalException("source_manifest_http_error", $"Source Version download returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is > SourceManifestReader.MaximumManifestBytes)
            throw new SourceRetrievalException("source_manifest_size_limit", $"Source Version manifests cannot exceed {SourceManifestReader.MaximumManifestBytes} bytes.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                if (output.Length + read > SourceManifestReader.MaximumManifestBytes)
                    throw new SourceRetrievalException("source_manifest_size_limit", $"Source Version manifests cannot exceed {SourceManifestReader.MaximumManifestBytes} bytes.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
        var content = await reader.ReadToEndAsync(cancellationToken);
        return new RetrievedSourceManifest(content, new SourceManifestOrigin
        {
            Url = source.AbsoluteUri,
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.Content.Headers.LastModified
        });
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceRetrievalException("source_manifest_timeout", "Source Version download timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new SourceRetrievalException("source_manifest_unavailable", "Source Version download failed.", exception);
        }
    }

    private static void Validate(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri || source.Scheme is not ("http" or "https"))
            throw new SourceRetrievalException("source_origin_invalid", "Source origin must be an absolute HTTP(S) URL.");
        if (!string.IsNullOrEmpty(source.UserInfo) || !string.IsNullOrEmpty(source.Fragment))
            throw new SourceRetrievalException("source_origin_invalid", "Source origin cannot contain credentials or a fragment.");
    }
}
