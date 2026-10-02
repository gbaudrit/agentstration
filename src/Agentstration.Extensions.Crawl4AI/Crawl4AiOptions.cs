using System.Net;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Crawl4AI;

public sealed record Crawl4AiOptions
{
    public const string SectionName = "Crawl4AI";
    public Uri Endpoint { get; init; } = new("http://localhost:11235");
    public string? ApiTokenFile { get; init; }
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];
    public IReadOnlyList<int> AllowedPorts { get; init; } = [80, 443];
    public IReadOnlyList<string> AllowedMediaTypes { get; init; } = ["text/html", "text/markdown"];
    public bool AllowPrivateAddresses { get; init; }
    public int MaximumDepth { get; init; } = 3;
    public int MaximumPages { get; init; } = 25;
    public int RequestTimeoutSeconds { get; init; } = 60;
    public int MaximumResponseBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumContentBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumLinksPerPage { get; init; } = 250;
    public int MaximumReadChunkBytes { get; init; } = 64 * 1024;
    public int ContentRetentionMinutes { get; init; } = 60;
    public long MaximumSpoolBytes { get; init; } = 256L * 1024 * 1024;
    public string ContentDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "agentstration-crawl4ai");
}

public sealed class Crawl4AiOptionsValidator : IValidateOptions<Crawl4AiOptions>
{
    public ValidateOptionsResult Validate(string? name, Crawl4AiOptions options)
    {
        var failures = new List<string>();
        if (!options.Endpoint.IsAbsoluteUri
            || (options.Endpoint.Scheme != Uri.UriSchemeHttp && options.Endpoint.Scheme != Uri.UriSchemeHttps))
            failures.Add("Crawl4AI:Endpoint must be an absolute HTTP(S) URL.");
        if (options.AllowedDomains.Count == 0 || options.AllowedDomains.Any(domain =>
            string.IsNullOrWhiteSpace(domain)
            || Uri.CheckHostName(domain.Trim().Trim('.')) == UriHostNameType.Unknown))
            failures.Add("Crawl4AI:AllowedDomains must contain at least one domain.");
        if (options.AllowedPorts.Count == 0 || options.AllowedPorts.Any(port => port is < 1 or > 65535))
            failures.Add("Crawl4AI:AllowedPorts must contain valid TCP ports.");
        if (options.AllowedMediaTypes.Count == 0 || options.AllowedMediaTypes.Any(mediaType => string.IsNullOrWhiteSpace(mediaType) || !mediaType.Contains('/', StringComparison.Ordinal)))
            failures.Add("Crawl4AI:AllowedMediaTypes must contain valid media types.");
        if (options.MaximumDepth is < 0 or > 10) failures.Add("Crawl4AI:MaximumDepth must be between 0 and 10.");
        if (options.MaximumPages is < 1 or > 500) failures.Add("Crawl4AI:MaximumPages must be between 1 and 500.");
        if (options.RequestTimeoutSeconds is < 1 or > 600) failures.Add("Crawl4AI:RequestTimeoutSeconds must be between 1 and 600.");
        if (options.MaximumResponseBytes is < 1024 or > 64 * 1024 * 1024) failures.Add("Crawl4AI:MaximumResponseBytes is outside the supported range.");
        if (options.MaximumContentBytes is < 1024 or > 64 * 1024 * 1024) failures.Add("Crawl4AI:MaximumContentBytes is outside the supported range.");
        if (options.MaximumLinksPerPage is < 1 or > 5000) failures.Add("Crawl4AI:MaximumLinksPerPage is outside the supported range.");
        if (options.MaximumReadChunkBytes is < 1024 or > 1024 * 1024) failures.Add("Crawl4AI:MaximumReadChunkBytes is outside the supported range.");
        if (options.ContentRetentionMinutes is < 1 or > 10080) failures.Add("Crawl4AI:ContentRetentionMinutes must be between 1 minute and 7 days.");
        if (options.MaximumSpoolBytes < options.MaximumContentBytes) failures.Add("Crawl4AI:MaximumSpoolBytes must be at least MaximumContentBytes.");
        if (string.IsNullOrWhiteSpace(options.ContentDirectory)) failures.Add("Crawl4AI:ContentDirectory is required.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public interface IDestinationAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDestinationAddressResolver : IDestinationAddressResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}
