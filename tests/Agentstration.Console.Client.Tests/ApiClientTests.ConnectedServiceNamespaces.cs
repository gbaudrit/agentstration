using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ConnectedServicesClientPropagatesNamespaceOnReadUsagesAndEveryCommand()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var resource = ConnectedService(scope) with { Metadata = new() { Name = "redis", Namespace = ResourceNamespace.Parse("provider-fixture") } };
        var requests = new List<(string Method, string Path, string? Etag)>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Headers.IfMatch.SingleOrDefault()?.ToString()));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath.EndsWith("/usages", StringComparison.Ordinal)
                    ? JsonContent.Create(new ConnectedServiceUsagesResponse([], 0)) : JsonContent.Create(resource)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"current\"");
            return response;
        }))
        { BaseAddress = new("https://agentstration.test/") };
        IConnectedServicesClient client = new ConnectedServicesApiClient(http);
        await client.GetConnectedServiceAsync(resource.Name, scope, resource.Namespace, default);
        await client.GetUsagesAsync(resource.Name, scope, resource.Namespace, default);
        await client.UpdateConnectedServiceAsync(resource.Name, scope, resource.Namespace, new(resource.Definition), "\"reviewed\"", default);
        await client.ReconcileAsync(resource.Name, scope, resource.Namespace, "\"reviewed\"", default);
        await client.DeactivateAsync(resource.Name, scope, resource.Namespace, "\"reviewed\"", default);
        CollectionAssert.AreEqual(new[] { "GET", "GET", "PUT", "POST", "POST" }, requests.Select(value => value.Method).ToArray());
        Assert.IsTrue(requests.All(value => value.Path.Contains($"scopeRef={Uri.EscapeDataString(scope.Value)}&namespace=provider-fixture", StringComparison.Ordinal)));
        Assert.IsTrue(requests.Skip(2).All(value => value.Etag == "\"reviewed\""));
    }
}
