using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Crawl4AI;

public interface ICrawlContentStore
{
    Task<(string Reference, long Length, string Sha256)> WriteAsync(
        ReadOnlyMemory<byte> content,
        string sourceUrl,
        string mediaType,
        CancellationToken cancellationToken);
    Task<ContentReadResult> ReadAsync(string reference, long offset, int? maximumBytes, CancellationToken cancellationToken);
    Task<StoredCrawlContent> ReadAllAsync(string reference, CancellationToken cancellationToken);
    Task DeleteAsync(string reference, CancellationToken cancellationToken);
}

public sealed record StoredCrawlContent(byte[] Content, string SourceUrl, string MediaType);

public sealed class FileCrawlContentStore(
    IOptions<Crawl4AiOptions> options,
    TimeProvider timeProvider) : ICrawlContentStore
{
    private const string ReferencePrefix = "crawl4ai-content:";
    private readonly Crawl4AiOptions options = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<(string Reference, long Length, string Sha256)> WriteAsync(
        ReadOnlyMemory<byte> content,
        string sourceUrl,
        string mediaType,
        CancellationToken cancellationToken)
    {
        if (content.Length > options.MaximumContentBytes)
            throw Error("crawl4ai_content_too_large", "The acquired content exceeds the configured content limit.");
        if (string.IsNullOrWhiteSpace(sourceUrl) || sourceUrl.Length > 2048)
            throw Error("crawl4ai_content_metadata_invalid", "The acquired content source URL is invalid.");
        if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > 256)
            throw Error("crawl4ai_content_metadata_invalid", "The acquired content media type is invalid.");

        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.ContentDirectory);
            await RemoveExpiredFilesAsync(cancellationToken);
            var currentSize = Directory.EnumerateFiles(options.ContentDirectory, "*.content")
                .Sum(path => new FileInfo(path).Length);
            if (currentSize + content.Length > options.MaximumSpoolBytes)
                throw Error("crawl4ai_spool_full", "The temporary acquisition content store is full.");

            var id = Guid.NewGuid().ToString("N");
            var destination = GetPath(id);
            var metadataDestination = GetMetadataPath(id);
            var temporary = Path.Combine(options.ContentDirectory, $".{id}.content.tmp");
            var metadataTemporary = Path.Combine(options.ContentDirectory, $".{id}.metadata.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(content, cancellationToken);
                }

                await File.WriteAllBytesAsync(
                    metadataTemporary,
                    JsonSerializer.SerializeToUtf8Bytes(new StoredContentMetadata(sourceUrl, mediaType)),
                    cancellationToken);
                File.Move(metadataTemporary, metadataDestination);
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(metadataTemporary)) File.Delete(metadataTemporary);
                if (!File.Exists(destination) && File.Exists(metadataDestination)) File.Delete(metadataDestination);
            }

            return ($"{ReferencePrefix}{id}", content.Length, Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ContentReadResult> ReadAsync(
        string reference,
        long offset,
        int? maximumBytes,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw Error("crawl4ai_read_invalid", "The content offset cannot be negative.");
        var requested = maximumBytes ?? options.MaximumReadChunkBytes;
        if (requested is < 1 || requested > options.MaximumReadChunkBytes)
            throw Error("crawl4ai_read_invalid", $"The requested chunk size must be between 1 and {options.MaximumReadChunkBytes} bytes.");

        var path = ResolveExistingPath(reference);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (offset > stream.Length) throw Error("crawl4ai_read_invalid", "The content offset exceeds the content length.");
        stream.Position = offset;
        var buffer = new byte[Math.Min(requested, checked((int)Math.Min(int.MaxValue, stream.Length - offset)))];
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (current == 0) break;
            read += current;
        }
        File.SetLastWriteTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
        return new ContentReadResult(reference, offset, Convert.ToBase64String(buffer, 0, read), offset + read >= stream.Length);
    }

    public async Task<StoredCrawlContent> ReadAllAsync(string reference, CancellationToken cancellationToken)
    {
        var path = ResolveExistingPath(reference);
        var id = ParseReference(reference);
        var metadataPath = GetMetadataPath(id);
        if (!File.Exists(metadataPath)) throw Error("crawl4ai_content_not_found", "The temporary content reference was not found or has expired.");
        var info = new FileInfo(path);
        if (info.Length > options.MaximumContentBytes)
            throw Error("crawl4ai_content_too_large", "The stored content exceeds the configured content limit.");
        var content = await File.ReadAllBytesAsync(path, cancellationToken);
        StoredContentMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<StoredContentMetadata>(await File.ReadAllBytesAsync(metadataPath, cancellationToken))
                ?? throw Error("crawl4ai_content_metadata_invalid", "The temporary content metadata is invalid.");
        }
        catch (JsonException exception)
        {
            throw new Crawl4AiException("crawl4ai_content_metadata_invalid", "The temporary content metadata is invalid.", exception);
        }
        File.SetLastWriteTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
        File.SetLastWriteTimeUtc(metadataPath, timeProvider.GetUtcNow().UtcDateTime);
        return new StoredCrawlContent(content, metadata.SourceUrl, metadata.MediaType);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ParseReference(reference);
        var path = GetPath(id);
        if (File.Exists(path)) File.Delete(path);
        var metadataPath = GetMetadataPath(id);
        if (File.Exists(metadataPath)) File.Delete(metadataPath);
        return Task.CompletedTask;
    }

    private async Task RemoveExpiredFilesAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromMinutes(options.ContentRetentionMinutes);
        foreach (var path in Directory.EnumerateFiles(options.ContentDirectory, "*.content"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
            {
                var id = Path.GetFileNameWithoutExtension(path);
                File.Delete(path);
                var metadataPath = GetMetadataPath(id);
                if (File.Exists(metadataPath)) File.Delete(metadataPath);
            }
        }
        await Task.CompletedTask;
    }

    private string ResolveExistingPath(string reference)
    {
        var path = GetPath(ParseReference(reference));
        if (!File.Exists(path)) throw Error("crawl4ai_content_not_found", "The temporary content reference was not found or has expired.");
        var expiry = File.GetLastWriteTimeUtc(path) + TimeSpan.FromMinutes(options.ContentRetentionMinutes);
        if (expiry < timeProvider.GetUtcNow().UtcDateTime)
        {
            File.Delete(path);
            var metadataPath = GetMetadataPath(ParseReference(reference));
            if (File.Exists(metadataPath)) File.Delete(metadataPath);
            throw Error("crawl4ai_content_not_found", "The temporary content reference was not found or has expired.");
        }
        return path;
    }

    private static string ParseReference(string reference)
    {
        if (!reference.StartsWith(ReferencePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(reference[ReferencePrefix.Length..], "N", out var id))
            throw Error("crawl4ai_content_reference_invalid", "The temporary content reference is invalid.");
        return id.ToString("N");
    }

    private string GetPath(string id) => Path.Combine(options.ContentDirectory, $"{id}.content");
    private string GetMetadataPath(string id) => Path.Combine(options.ContentDirectory, $"{id}.metadata.json");
    private static Crawl4AiException Error(string code, string message) => new(code, message);
    private sealed record StoredContentMetadata(string SourceUrl, string MediaType);
}
