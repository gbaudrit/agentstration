using System.Net;
using System.Net.Http.Json;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Console.Client.Tests;

[TestClass]
public sealed class ConsoleContextSwitchResolverTests
{
    private static readonly string Target = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")).Value;
    private static ConsoleContextSwitchRequest Request(string path) =>
        new(new Uri("https://console.test" + path), "/instance", Target, "/?context=" + Uri.EscapeDataString(Target), "/instance");

    [TestMethod]
    [DataRow("/agents/same?namespace=team&context=%2Finstance#definition/tools")]
    [DataRow("/flows/same?scopeRef=%2Finstance&context=%2Finstance#access")]
    [DataRow("/namespaces/team/flows/same?context=%2Finstance#yaml")]
    [DataRow("/modelprofiles/same?namespace=team#definition/configuration")]
    [DataRow("/storages/same#overview")]
    [DataRow("/connectedservices/same#definition/identity")]
    [DataRow("/custom-family/same#custom-section")]
    [DataRow("/arbitrary/same?scopeRef=&context=#overview")]
    public async Task EveryResourceKeepsItsLocationAndSavedOwnerWithoutAnAdditionalResourceRead(string path)
    {
        using var handler = new Handler();
        var result = await new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(Request(path), default);
        Assert.IsFalse(result.ResourceUnavailable);
        var uri = new Uri(result.Location);
        var source = new Uri("https://console.test" + path);
        Assert.AreEqual(source.AbsolutePath, uri.AbsolutePath);
        Assert.AreEqual(source.Fragment, uri.Fragment);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.AreEqual("/instance", query["scopeRef"].ToString());
        Assert.AreEqual(Target, query["context"].ToString());
        Assert.HasCount(1, handler.Requests, "The destination screen owns the existing resource authorization read.");
        Assert.AreEqual("/api/identity/context", handler.Requests[0].AbsolutePath);
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/agents?search=example")]
    [DataRow("/flows")]
    public async Task NonResourcePagesRetainTheirRouteAndQueryWithoutInventingOwnership(string path)
    {
        using var handler = new Handler();
        var request = Request(path) with { ResourceOwnerScopeRef = null };
        var result = await new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(request, default);
        var uri = new Uri(result.Location);
        Assert.AreEqual(request.Location.AbsolutePath, uri.AbsolutePath);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.AreEqual(Target, query["context"].ToString());
        Assert.IsFalse(query.ContainsKey("scopeRef"));
        if (path.Contains("search=", StringComparison.Ordinal)) Assert.AreEqual("example", query["search"].ToString());
    }

    [TestMethod]
    public async Task AnUnresolvedResourceOwnerUsesTheSafeCollectionInsteadOfRebindingItsName()
    {
        using var handler = new Handler();
        var request = Request("/arbitrary/same") with { ResourcePage = true, ResourceOwnerScopeRef = null };
        var result = await new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(request, default);
        Assert.AreEqual(request.FallbackLocation, result.Location);
        Assert.IsFalse(result.ResourceUnavailable);
    }

    [TestMethod]
    public async Task ObsoleteSectionQueriesAreRemovedWhileTheAnchorAndNamespaceRemain()
    {
        using var handler = new Handler();
        var result = await new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(
            Request("/flows/same?namespace=team&view=runs&tab=yaml&returnUrl=%2Fflows#definition/identity"), default);
        var query = QueryHelpers.ParseQuery(new Uri(result.Location).Query);
        Assert.AreEqual("team", query["namespace"].ToString());
        Assert.IsFalse(query.ContainsKey("view") || query.ContainsKey("tab") || query.ContainsKey("returnUrl"));
        Assert.AreEqual("#definition/identity", new Uri(result.Location).Fragment);
    }

    [TestMethod]
    public async Task AnExplicitOwnerCannotBeReplacedByAnotherSavedIdentity()
    {
        using var handler = new Handler();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new ConsoleContextSwitchResolver(new Factory(handler))
            .ResolveAsync(Request("/flows/same?scopeRef=%2Fworkspaces%2Fbbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), default));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task ContextDenialAndTransportFailuresDoNotBecomeMissingResourceFallbacks(HttpStatusCode status)
    {
        using var handler = new Handler { Status = status };
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new ConsoleContextSwitchResolver(new Factory(handler))
            .ResolveAsync(Request("/flows/same#yaml"), default));
        Assert.HasCount(1, handler.Requests);
    }

    private sealed class Factory(Handler handler) : IConsoleContextHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.test/") };
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Assert.AreEqual("/api/identity/context", request.RequestUri!.AbsolutePath);
            Assert.AreEqual(Target, request.Headers.GetValues(ConsoleContextHeaders.Name).Single());
            Assert.AreEqual(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = JsonContent.Create(new { selectedScopeRef = Target }) });
        }
    }
}
