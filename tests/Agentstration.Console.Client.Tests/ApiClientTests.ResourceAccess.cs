using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ResourceAccessPolicyClientReturnsNullWhenNoOverlayExists()
    {
        var uid = Guid.NewGuid();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual($"/api/resource-access-policies/{uid:D}", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        { BaseAddress = new Uri("https://console.test/") };

        var snapshot = await new ResourceAccessPolicyApiClient(httpClient).GetAsync(uid, CancellationToken.None);

        Assert.IsNull(snapshot);
    }

    [TestMethod]
    public async Task ResourceAccessPolicyClientSendsConcurrencyTokenAndReadsUpdatedSnapshot()
    {
        var uid = Guid.NewGuid();
        var target = ResourceScopeRef.Tenant(Guid.NewGuid());
        var policy = new ResourceAccessPolicy(
            new(uid, ResourceScopeRef.Instance, ResourceNamespace.Default, "Agent", 2),
            [new(target, [ResourceAccessAction.Discover, ResourceAccessAction.Use], true)]);
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Put, request.Method);
            Assert.AreEqual($"/api/resource-access-policies/{uid:D}", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("\"policy-1\"", request.Headers.IfMatch.Single().Tag);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(policy)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"policy-2\"");
            return response;
        }))
        { BaseAddress = new Uri("https://console.test/") };

        var snapshot = await new ResourceAccessPolicyApiClient(httpClient)
            .PutAsync(policy, "\"policy-1\"", CancellationToken.None);

        Assert.AreEqual("\"policy-2\"", snapshot.ETag);
        Assert.AreEqual(uid, snapshot.Policy.Resource.Uid);
        CollectionAssert.AreEquivalent(
            new[] { ResourceAccessAction.Discover, ResourceAccessAction.Use },
            snapshot.Policy.Grants.Single().Actions.ToArray());
    }
}
