using System.Net;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Tools;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class KnowledgeHttpAcquisitionTests
{
    private static readonly IReadOnlySet<string> JsonMediaType =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" };

    [TestMethod]
    public async Task FetcherReturnsOneBoundedAllowedPublicResponse()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("{\"value\":42}"u8.ToArray())
            {
                Headers = { ContentType = new("application/json") }
            }
        }));
        var result = await new SafeKnowledgeHttpContentFetcher(client).FetchAsync(Request("https://example.test/data"), default);
        Assert.AreEqual("application/json", result.MediaType);
        Assert.AreEqual("data", result.FileName);
        CollectionAssert.AreEqual("{\"value\":42}"u8.ToArray(), result.Content);
    }

    [TestMethod]
    public async Task FetcherRejectsPrivateDestinationsRedirectsMediaTypesAndOversizedResponses()
    {
        using var unused = new HttpClient(new Handler(_ => throw new AssertFailedException("Denied URLs must not be sent.")));
        var privateFailure = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() =>
            new SafeKnowledgeHttpContentFetcher(unused).FetchAsync(Request("http://127.0.0.1/data"), default));
        Assert.AreEqual("knowledge_http_endpoint_denied", privateFailure.Code);

        using var redirect = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new("http://10.0.0.1/private") }
        }));
        var redirectFailure = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() =>
            new SafeKnowledgeHttpContentFetcher(redirect).FetchAsync(Request("https://example.test/data"), default));
        Assert.AreEqual("knowledge_http_endpoint_denied", redirectFailure.Code);

        using var invalidMedia = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("binary") { Headers = { ContentType = new("application/octet-stream") } }
        }));
        var mediaFailure = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() =>
            new SafeKnowledgeHttpContentFetcher(invalidMedia).FetchAsync(Request("https://example.test/data"), default));
        Assert.AreEqual("knowledge_http_media_type_invalid", mediaFailure.Code);

        using var oversized = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[33]) { Headers = { ContentType = new("application/json") } }
        }));
        var sizeFailure = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() =>
            new SafeKnowledgeHttpContentFetcher(oversized).FetchAsync(Request("https://example.test/data") with
            {
                MaximumBytes = 32
            }, default));
        Assert.AreEqual("knowledge_http_size_limit", sizeFailure.Code);
    }

    private static KnowledgeHttpFetchRequest Request(string url) => new(
        new(url), 1024, 3, TimeSpan.FromSeconds(5), JsonMediaType);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
