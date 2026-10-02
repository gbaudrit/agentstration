using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Crawl4AI;

public interface ICrawl4AiTokenProvider
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class FileCrawl4AiTokenProvider(IOptions<Crawl4AiOptions> options) : ICrawl4AiTokenProvider
{
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.ApiTokenFile;
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!File.Exists(path)) throw new Crawl4AiException("crawl4ai_token_unavailable", "The configured Crawl4AI API token file is unavailable.");
            if (new FileInfo(path).Length > 4098)
                throw new Crawl4AiException("crawl4ai_token_invalid", "The configured Crawl4AI API token is invalid.");
            var token = await File.ReadAllTextAsync(path, cancellationToken);
            if (token.EndsWith("\r\n", StringComparison.Ordinal)) token = token[..^2];
            else if (token.EndsWith('\n')) token = token[..^1];
            if (token.Length is < 1 or > 4096 || token.Contains('\n', StringComparison.Ordinal) || token.Contains('\r', StringComparison.Ordinal))
                throw new Crawl4AiException("crawl4ai_token_invalid", "The configured Crawl4AI API token is invalid.");
            return token;
        }
        catch (Crawl4AiException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new Crawl4AiException("crawl4ai_token_unavailable", "The configured Crawl4AI API token file is unavailable.", exception);
        }
    }
}
