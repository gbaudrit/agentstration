using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Reconciliation.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ReconciliationClientPreservesActorAndReviewDigestAndDoesNotRetryAConflict()
    {
        var owner = ResourceScopeRef.Workspace(Guid.NewGuid()); var calls = 0;
        var plan = new ReconciliationPreview("digest", DateTimeOffset.UnixEpoch, "generation-drift", [], [], [], ["partial-coverage"], false, false);
        using var handler = new ReconciliationHandler(async (request, ct) =>
        {
            calls++;
            Assert.AreEqual(owner.Value, request.Headers.GetValues("X-Agentstration-Context").Single());
            Assert.AreEqual(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (calls == 1)
            {
                Assert.AreEqual("old", body.RootElement.GetProperty("from").GetString());
                Assert.AreEqual("current", body.RootElement.GetProperty("to").GetString());
            }
            else Assert.AreEqual("digest", body.RootElement.GetProperty("expectedDigest").GetString());
            Assert.IsTrue(request.RequestUri!.AbsolutePath.EndsWith(calls == 1 ? "/preview" : "/revalidate", StringComparison.Ordinal));
            return calls == 1 ? new(HttpStatusCode.OK) { Content = JsonContent.Create(plan) }
                : new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { title = "reconciliation_review_stale" }) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var client = new ReconciliationApiClient(http);
        var request = new ReconciliationPreviewRequest("old", "current", ["selected"]);
        var preview = await client.PreviewAsync(owner, request, default);
        Assert.AreEqual("digest", preview.Digest);
        var error = await Assert.ThrowsExactlyAsync<AgentstrationApiException>(() => client.RevalidateAsync(owner, new(request, preview.Digest), default));
        Assert.AreEqual(HttpStatusCode.Conflict, error.StatusCode); Assert.AreEqual(2, calls);
    }
    private sealed class ReconciliationHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
