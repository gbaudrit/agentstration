using Agentstration.Resources;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Console.Client.Tests;

[TestClass]
public sealed class CollectionNavigationTests
{
    [TestMethod]
    public void ResourceLinkKeepsQualifiedIdentityAndOwnerContextWithoutParsingTheNameAsAPath()
    {
        var owner = ResourceScopeRef.Tenant(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var uri = new Uri("https://console.test" + ConsoleResourceUrls.Resource("/tools", "same name?#", ResourceNamespace.Parse("team-a"), owner));
        Assert.AreEqual("/tools/same%20name%3F%23", uri.AbsolutePath);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.AreEqual("team-a", query["namespace"].ToString());
        Assert.AreEqual(owner.Value, query["scopeRef"].ToString());
        Assert.AreEqual(owner.Value, query["context"].ToString());
    }
    [TestMethod]
    public void InheritedResourceLinkKeepsTheExactOwnerAndTheSourceNavigationContext()
    {
        var source = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var uri = new Uri("https://console.test" + ConsoleResourceUrls.Resource("/tools", "same", ResourceNamespace.Parse("team-a"), ResourceScopeRef.Instance, source));
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.AreEqual("/instance", query["scopeRef"].ToString());
        Assert.AreEqual(source.Value, query["context"].ToString());
        Assert.AreEqual("team-a", query["namespace"].ToString());
    }

    [TestMethod]
    public void OwnerScopeContextIsAddedBeforeFragmentsWithoutDuplicatingAnExistingOwner()
    {
        var link = ConsoleResourceUrls.WithOwnerScope("/tools/same?namespace=team-a&scopeRef=%2Finstance#usage", ResourceScopeRef.Instance);
        var uri = new Uri("https://console.test" + link);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.HasCount(1, query["scopeRef"]);
        Assert.AreEqual("/instance", query["context"].ToString());
        Assert.AreEqual("team-a", query["namespace"].ToString());
        Assert.AreEqual("#usage", uri.Fragment);
    }

    [TestMethod]
    public async Task CategoryHomonymsRetainTheirNamespaceForReadMembersUpdateAndDelete()
    {
        var handler = new CategoryHandler();
        var client = new ToolCategoriesApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://console.test/") });
        var ownerNamespace = ResourceNamespace.Parse("team-a");
        await client.GetCategoryAsync(ownerNamespace, "same");
        await client.GetMembersAsync(ownerNamespace, "same");
        await client.UpdateCategoryAsync(ownerNamespace, "same", new(new() { DisplayName = "Same" }), "\"one\"");
        await client.DeleteCategoryAsync(ownerNamespace, "same", "\"one\"");
        Assert.HasCount(4, handler.Requests);
        foreach (var request in handler.Requests)
            Assert.AreEqual("team-a", QueryHelpers.ParseQuery(request.Uri.Query)["resourceNamespace"].ToString());
        Assert.AreEqual("\"one\"", handler.Requests[2].ETag);
        Assert.AreEqual("\"one\"", handler.Requests[3].ETag);
    }

    private sealed class CategoryHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string? ETag)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.IfMatch.SingleOrDefault()?.Tag));
            var resource = new Agentstration.Tools.ToolCategoryResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = Agentstration.Tools.ToolResourceKinds.ToolCategory,
                Metadata = new() { Name = "same", Namespace = ResourceNamespace.Parse("team-a") },
                Definition = new() { DisplayName = "Same" }
            };
            var content = request.RequestUri!.AbsolutePath.EndsWith("/members", StringComparison.Ordinal)
                ? System.Net.Http.Json.JsonContent.Create(new { value = Array.Empty<Agentstration.Tools.ToolCategoryMember>() })
                : System.Net.Http.Json.JsonContent.Create(resource);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new("\"one\"");
            return Task.FromResult(response);
        }
    }

}
