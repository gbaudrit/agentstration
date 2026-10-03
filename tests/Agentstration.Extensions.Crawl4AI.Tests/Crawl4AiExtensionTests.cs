using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Crawl4AI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Agentstration.Extensions.Crawl4AI.Tests;

[TestClass]
public sealed class Crawl4AiExtensionTests
{
    [TestMethod]
    public async Task ExtensionPublishesBoundedAcquisitionContributions()
    {
        await using var fixture = new TemporaryFixture();
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Crawl4AI:AllowedDomains:0", "example.test");
            builder.UseSetting("Crawl4AI:ContentDirectory", fixture.Root);
        });

        var manifest = await new AepClient(factory.CreateClient()).GetManifestAsync();

        Assert.AreEqual("Agentstration.Extensions.Crawl4AI", manifest.Extension.Id);
        CollectionAssert.AreEquivalent(
            new[] { "web.fetch", "web.crawl", "content.extract", "content.read", "content.delete" },
            manifest.Contributions.Tools?.Select(tool => tool.Id).ToArray());
        Assert.AreEqual("/mcp", manifest.Mcp?.Servers.Single().Endpoint);
    }

    [TestMethod]
    public async Task McpPublishesTheExactToolsAdvertisedByAep()
    {
        await using var fixture = new TemporaryFixture();
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Crawl4AI:AllowedDomains:0", "example.test");
            builder.UseSetting("Crawl4AI:ContentDirectory", fixture.Root);
        });
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "crawl4ai-test" },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);

        var tools = await mcp.ListToolsAsync();

        CollectionAssert.AreEquivalent(
            new[] { "web_fetch", "web_crawl", "content_extract", "content_read", "content_delete" },
            tools.Select(tool => tool.Name).ToArray());
    }

    [TestMethod]
    public async Task FetchReturnsOpaqueReferenceAndSupportsBoundedReads()
    {
        await using var fixture = new TemporaryFixture();
        var service = fixture.Service((request, _) => Json(new
        {
            results = new[]
            {
                new
                {
                    success = true,
                    url = "https://example.test/page",
                    markdown = "# Acquired content",
                    links = new { @internal = new[] { new { href = "/next" } } },
                    metadata = new { title = "Page" }
                }
            }
        }));

        var result = await service.FetchAsync("https://example.test/page", "flow-run-1", default);
        var first = await service.ReadAsync(result.Content.Reference, 0, 5, default);
        var second = await service.ReadAsync(result.Content.Reference, 5, 1024, default);

        Assert.StartsWith("crawl4ai-content:", result.Content.Reference, StringComparison.Ordinal);
        Assert.AreEqual("text/markdown", result.Content.MediaType);
        Assert.AreEqual("flow-run-1", result.CorrelationId);
        Assert.AreEqual("# Acq", Encoding.UTF8.GetString(Convert.FromBase64String(first.ContentBase64)));
        Assert.IsFalse(first.EndOfContent);
        Assert.IsTrue(second.EndOfContent);
        Assert.AreEqual("https://example.test/next", result.Content.Links.Single());
    }

    [TestMethod]
    public async Task CrawlAppliesBreadthFirstDepthAndPageBounds()
    {
        await using var fixture = new TemporaryFixture(maximumPages: 5);
        var service = fixture.Service(async (request, cancellationToken) =>
        {
            var input = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var url = input.GetProperty("urls")[0].GetString()!;
            var links = url.EndsWith("/root", StringComparison.Ordinal)
                ? new[] { "/first", "/second", "https://outside.test/denied" }
                : Array.Empty<string>();
            return Json(new { results = new[] { new { success = true, url, markdown = url, links = new { @internal = links } } } });
        });

        var result = await service.CrawlAsync("https://example.test/root", 1, 2, "crawl-1", default);

        Assert.HasCount(2, result.Contents);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual("https://example.test/root", result.Contents[0].SourceUrl);
        Assert.AreEqual("https://example.test/first", result.Contents[1].SourceUrl);
    }

    [TestMethod]
    public async Task DestinationPolicyRejectsPrivateResolutionBeforeCallingCrawl4Ai()
    {
        await using var fixture = new TemporaryFixture(address: IPAddress.Loopback);
        var calls = 0;
        var service = fixture.Service((_, _) => { calls++; return Json(new { }); });

        var exception = await Assert.ThrowsAsync<Crawl4AiException>(() =>
            service.FetchAsync("https://example.test/page", null, default));

        Assert.AreEqual("crawl4ai_destination_denied", exception.Code);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task FetchRejectsRedirectedResultOutsideAllowlist()
    {
        await using var fixture = new TemporaryFixture();
        var service = fixture.Service((_, _) => Json(new
        {
            results = new[] { new { success = true, url = "https://outside.test/page", markdown = "unsafe" } }
        }));

        var exception = await Assert.ThrowsAsync<Crawl4AiException>(() =>
            service.FetchAsync("https://example.test/page", null, default));

        Assert.AreEqual("crawl4ai_domain_denied", exception.Code);
    }

    [TestMethod]
    public async Task ExtractionProducesSeparateNormalizedTextReference()
    {
        await using var fixture = new TemporaryFixture();
        var service = fixture.Service((_, _) => Json(new
        {
            results = new[] { new { success = true, url = "https://example.test/page", html = "<h1>Title</h1><p>A &amp; B</p>" } }
        }));
        var acquired = await service.FetchAsync("https://example.test/page", null, default);

        var extracted = await service.ExtractAsync(
            acquired.Content.Reference,
            null,
            default);
        var content = await service.ReadAsync(extracted.Content.Reference, 0, 1024, default);

        Assert.AreNotEqual(acquired.Content.Reference, extracted.Content.Reference);
        Assert.AreEqual("text/plain; charset=utf-8", extracted.Content.MediaType);
        Assert.AreEqual("Title A & B", Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)));
    }

    [TestMethod]
    public async Task FetchEnforcesConfiguredMediaTypesAndResponseBounds()
    {
        await using var mediaFixture = new TemporaryFixture(allowedMediaTypes: ["text/html"]);
        var mediaService = mediaFixture.Service((_, _) => Json(new
        {
            results = new[] { new { success = true, url = "https://example.test/page", markdown = "denied" } }
        }));
        var mediaError = await Assert.ThrowsAsync<Crawl4AiException>(() =>
            mediaService.FetchAsync("https://example.test/page", null, default));

        await using var sizeFixture = new TemporaryFixture(maximumResponseBytes: 32);
        var sizeService = sizeFixture.Service((_, _) => Json(new
        {
            results = new[] { new { success = true, url = "https://example.test/page", markdown = new string('x', 128) } }
        }));
        var sizeError = await Assert.ThrowsAsync<Crawl4AiException>(() =>
            sizeService.FetchAsync("https://example.test/page", null, default));

        Assert.AreEqual("crawl4ai_media_type_denied", mediaError.Code);
        Assert.AreEqual("crawl4ai_response_too_large", sizeError.Code);
    }

    [TestMethod]
    public async Task FetchPropagatesCallerCancellation()
    {
        await using var fixture = new TemporaryFixture();
        var service = fixture.Service(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json(new { });
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.FetchAsync("https://example.test/page", null, cancellation.Token));
    }

    [TestMethod]
    public async Task TokenProviderReturnsTheOrchestratorManagedToken()
    {
        var provider = new ConfiguredCrawl4AiTokenProvider(Options.Create(new Crawl4AiOptions
        {
            ApiToken = "managed-token"
        }));

        var token = await provider.GetTokenAsync(default);

        Assert.AreEqual("managed-token", token);
    }

    [TestMethod]
    public async Task TokenProviderRejectsMultilineManagedToken()
    {
        var provider = new ConfiguredCrawl4AiTokenProvider(Options.Create(new Crawl4AiOptions
        {
            ApiToken = "invalid\ntoken"
        }));

        var exception = await Assert.ThrowsAsync<Crawl4AiException>(() => provider.GetTokenAsync(default));

        Assert.AreEqual("crawl4ai_token_invalid", exception.Code);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class TemporaryFixture : IAsyncDisposable
    {
        private readonly int maximumPages;
        private readonly IPAddress address;
        private readonly int maximumResponseBytes;
        private readonly IReadOnlyList<string> allowedMediaTypes;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "agentstration-crawl4ai-tests", Guid.NewGuid().ToString("N"));

        public TemporaryFixture(
            int maximumPages = 25,
            IPAddress? address = null,
            int maximumResponseBytes = 1024 * 1024,
            IReadOnlyList<string>? allowedMediaTypes = null)
        {
            this.maximumPages = maximumPages;
            this.address = address ?? IPAddress.Parse("93.184.216.34");
            this.maximumResponseBytes = maximumResponseBytes;
            this.allowedMediaTypes = allowedMediaTypes ?? ["text/html", "text/markdown"];
        }

        public Crawl4AiAcquisitionService Service(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
            Service((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)));

        public Crawl4AiAcquisitionService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            var options = Options.Create(new Crawl4AiOptions
            {
                Endpoint = new Uri("http://crawl4ai.test"),
                AllowedDomains = ["example.test"],
                AllowedMediaTypes = allowedMediaTypes,
                MaximumDepth = 3,
                MaximumPages = maximumPages,
                MaximumContentBytes = 1024 * 1024,
                MaximumResponseBytes = maximumResponseBytes,
                MaximumReadChunkBytes = 64 * 1024,
                MaximumSpoolBytes = 4 * 1024 * 1024,
                ContentDirectory = Root
            });
            var policy = new DestinationPolicy(options, new Resolver(address));
            var client = new Crawl4AiClient(
                new HttpClient(new Handler(handler)),
                policy,
                new NoTokenProvider(),
                options);
            var store = new FileCrawlContentStore(options, TimeProvider.System);
            return new Crawl4AiAcquisitionService(client, store, options);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Resolver(IPAddress address) : IDestinationAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([address]);
    }

    private sealed class NoTokenProvider : ICrawl4AiTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
