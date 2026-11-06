using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceTargetClientTests
{
    [TestMethod]
    public async Task ReconciliationUsesTheObservedTargetETagAndRetainsConcurrencyFailures()
    {
        using var http = new HttpClient(new ReconcileHandler()) { BaseAddress = new Uri("https://console.test/") };
        var client = new DataSourcesApiClient(http);
        var exception = await Assert.ThrowsAsync<AgentstrationApiException>(() => client.ReconcileTargetAsync(
            ResourceNamespace.Parse("catalog"), "documents", new("discovery-2", "collection"), "\"target-v1\""));
        Assert.IsTrue(exception.IsConcurrencyConflict);
    }

    private sealed class ReconcileHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/datasourceacquisitiontargets/documents/reconcile?namespace=catalog", request.RequestUri!.PathAndQuery);
            Assert.AreEqual("\"target-v1\"", request.Headers.IfMatch.Single().Tag);
            Assert.IsFalse(request.Headers.Contains("If-None-Match"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.AreEqual("discovery-2", body.RootElement.GetProperty("discoveryId").GetString());
            Assert.AreEqual("collection", body.RootElement.GetProperty("choiceId").GetString());
            return new(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { title = "Resource version conflict", detail = "Target changed." })
            };
        }
    }
}
