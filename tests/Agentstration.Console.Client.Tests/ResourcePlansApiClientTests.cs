using System.Net;
using System.Net.Http.Json;
using Agentstration.ResourcePlanning.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ResourcePlansApiClientTests
{
    [TestMethod]
    public async Task ClientUsesScopedServerRoutesWithoutCallerSuppliedScope()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath.EndsWith("/change-sets", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ResourceChangeSetPage([], false)) };
            if (request.RequestUri.AbsolutePath.EndsWith("/validations", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<ResourceChangeSetValidation>()) };
            if (request.RequestUri.AbsolutePath.Contains("/resource-plans/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ResourcePlanPage([], false)) };
        }));
        http.BaseAddress = new Uri("http://localhost/");
        var client = new ResourcePlansApiClient(http);
        var planId = Guid.NewGuid();
        var changeSetId = Guid.NewGuid();

        _ = await client.ListPlansAsync(ResourcePlanStatus.Ready, 30, 30, default);
        Assert.IsNull(await client.GetPlanAsync(planId, default));
        _ = await client.ListChangeSetsAsync(planId, 0, 50, default);
        _ = await client.ListValidationsAsync(changeSetId, default);

        CollectionAssert.Contains(requests, "/api/resource-plans?skip=30&take=30&status=Ready");
        CollectionAssert.Contains(requests, $"/api/resource-plans/{planId:D}");
        CollectionAssert.Contains(requests, $"/api/resource-plans/change-sets?planId={planId:D}&skip=0&take=50");
        CollectionAssert.Contains(requests, $"/api/resource-plans/change-sets/{changeSetId:D}/validations");
        Assert.IsFalse(requests.Any(value => value.Contains("workspace", StringComparison.OrdinalIgnoreCase) || value.Contains("tenant", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ClientReadsAndUpdatesSavedBindingsWithETag()
    {
        var id = Guid.NewGuid();
        var scope = new ResourcePlanScope(Guid.NewGuid(), new Agentstration.Resources.WorkspaceId(Guid.NewGuid()));
        var draft = new ResourcePlanBindingDraft(new(id), scope, 2,
            [new("triage", new("model-a"), new("runtime-a"))], DateTimeOffset.UnixEpoch);
        string? sentETag = null;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual($"/api/resource-plans/{id:D}/bindings", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Put) sentETag = request.Headers.IfMatch.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(draft) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"draft-a\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ResourcePlansApiClient(http);
        Assert.AreEqual("model-a", (await client.GetBindingsAsync(id, default))!.Value.Bindings.Single().ModelProfile!.Name);
        var saved = await client.SaveBindingsAsync(id, new(2, draft.Bindings), "\"draft-a\"", default);
        Assert.AreEqual("\"draft-a\"", sentETag);
        Assert.AreEqual("runtime-a", saved.Value.Bindings.Single().RuntimeProfile!.Name);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
