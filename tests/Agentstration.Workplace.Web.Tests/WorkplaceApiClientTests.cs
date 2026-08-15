using System.Net;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Workplace.Client;

namespace Agentstration.Workplace.Web.Tests;

[TestClass]
public sealed class WorkplaceApiClientTests
{
    [TestMethod]
    public async Task NamespacedEntryUsesNamespacedReadAndSubmissionRoutes()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5100/") };
        var client = new WorkplaceApiClient(http);
        var entryId = new EntryId("main", new ResourceNamespace("agentstration.daily-life-assistant"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetEntryAsync(entryId, default));
        Assert.AreEqual("/api/namespaces/agentstration.daily-life-assistant/entries/main", handler.RequestUri?.AbsolutePath);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SubmitAsync("personal", entryId, new Dictionary<string, System.Text.Json.JsonElement>(), default));
        Assert.AreEqual("/api/workspaces/personal/namespaces/agentstration.daily-life-assistant/entries/main/interactions", handler.RequestUri?.AbsolutePath);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }
}
