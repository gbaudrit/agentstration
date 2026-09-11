using System.Net;
using System.Net.Http.Json;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SourceConsoleApiClientTests
{
    [TestMethod]
    public async Task ConsoleReadsAndMutationsUseTheManagementHttpBoundary()
    {
        var requests = new List<(HttpMethod Method, string Path, string? ETag)>();
        var handler = new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.IfMatch.SingleOrDefault()?.Tag));
            return request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create<IReadOnlyList<SourceConsoleListItem>>([]) }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new SourceConsoleApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://console.test/") });

        var sources = await client.ListAsync(default);
        await client.UpdateDisplayNameAsync(ResourceScopeRef.Instance, "contoso", "catalog", "Catalog", "\"v1\"", default);

        Assert.IsEmpty(sources);
        CollectionAssert.AreEqual(
            new[]
            {
                (HttpMethod.Get, "/api/sources/console", (string?)null),
                (HttpMethod.Put, "/api/sources/contoso/catalog/display-name?scopeRef=%2Finstance", "\"v1\"")
            },
            requests);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
