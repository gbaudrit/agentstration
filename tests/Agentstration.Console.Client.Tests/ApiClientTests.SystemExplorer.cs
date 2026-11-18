using System.Net;
using System.Net.Http.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task SystemExplorerKeepsActorContextSeparateFromExactOwnerAndEscapesNeutralIdentities()
    {
        var actor = ResourceScopeRef.Workspace(Guid.NewGuid());
        var owner = ResourceScopeRef.Instance;
        var ns = new ResourceNamespace("finance");
        using var handler = new StubHandler(request =>
        {
            Assert.AreEqual(actor.Value, request.Headers.GetValues("X-Agentstration-Context").Single());
            Assert.Contains("scopeRef=%2Finstance", request.RequestUri!.Query);
            Assert.Contains("namespace=finance", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create<IReadOnlyList<SystemView>>([]) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var client = new SystemExplorerApiClient(http);
        Assert.IsEmpty(await client.ListAsync(actor, owner, ns, default));
    }

    [TestMethod]
    public async Task SystemExplorerPreservesSignedCursorRootAndEtagsWithoutRetryingAStaleMutation()
    {
        var actor = ResourceScopeRef.Workspace(Guid.NewGuid());
        var value = new SystemView(Guid.NewGuid(), actor, ResourceNamespace.Default, "payments", "Payments", null, 2, "\"etag-2\"", []);
        var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++;
            Assert.AreEqual(actor.Value, request.Headers.GetValues("X-Agentstration-Context").Single());
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("root=publication%3Auid%3A1%2F2", request.RequestUri!.Query);
                Assert.Contains("cursor=signed%2B%2F%3D", request.RequestUri.Query);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new SystemGraphPage([], [], "snapshot", DateTimeOffset.UnixEpoch, "partial", false, false, null)) };
            }
            Assert.AreEqual(value.ETag, request.Headers.IfMatch.Single().ToString());
            return new(HttpStatusCode.PreconditionFailed) { Content = JsonContent.Create(new { title = "system_changed" }) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        var client = new SystemExplorerApiClient(http);
        await client.ReadAsync(actor, ResourceNamespace.Default, "publication:uid:1/2", "signed+/=", default);
        var error = await Assert.ThrowsExactlyAsync<AgentstrationApiException>(() => client.RenameAsync(actor, value, "Renamed", default));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, error.StatusCode);
        Assert.AreEqual(2, calls);
    }
}
