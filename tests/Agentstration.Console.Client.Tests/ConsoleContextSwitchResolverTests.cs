using System.Net;
using System.Net.Http.Json;
using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Console.Client.Tests;

[TestClass]
public sealed class ConsoleContextSwitchResolverTests
{
    private static readonly string Target = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")).Value;
    private static ConsoleContextSwitchRequest Request(string path = "/agents/same?namespace=team&context=%2Finstance#definition/tools") =>
        new(new Uri("https://console.test" + path), "/instance", Target, "/agents?context=" + Uri.EscapeDataString(Target));

    [TestMethod]
    [DataRow("/agents/same?namespace=team&context=%2Finstance#definition/tools")]
    [DataRow("/namespaces/team/agents/same?scopeRef=%2Finstance&context=%2Finstance#yaml")]
    public async Task AccessibleAgentRetainsItsExactIdentityAndAnchorWithoutChangingTheSourceContext(string path)
    {
        using var handler = new Handler();
        var result = await new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(Request(path), default);
        Assert.IsFalse(result.ResourceUnavailable);
        var uri = new Uri(result.Location);
        Assert.AreEqual(new Uri("https://console.test" + path).AbsolutePath, uri.AbsolutePath);
        Assert.AreEqual(new Uri("https://console.test" + path).Fragment, uri.Fragment);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.AreEqual("/instance", query["scopeRef"].ToString());
        Assert.AreEqual(Target, query["context"].ToString());
        Assert.HasCount(3, handler.Requests);
        Assert.AreEqual(Target, handler.Requests[0].Scope);
        Assert.AreEqual("/instance", handler.Requests[1].Scope);
        Assert.AreEqual(Target, handler.Requests[2].Scope);
        Assert.AreEqual("/instance", QueryHelpers.ParseQuery(handler.Requests[2].Uri.Query)["scopeRef"].ToString());
        Assert.IsTrue(handler.Requests.All(request => request.Method == HttpMethod.Get));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task UnavailableAgentUsesItsCollectionWithoutTryingAHomonym(HttpStatusCode status)
    {
        using var handler = new Handler { TargetStatus = status };
        var result = await new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(Request(), default);
        Assert.IsTrue(result.ResourceUnavailable);
        Assert.AreEqual(Request().FallbackLocation, result.Location);
        Assert.HasCount(3, handler.Requests);
        Assert.IsTrue(handler.Requests[2].Uri.Query.Contains("scopeRef=%2Finstance", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AReplacementWithTheSameNameCannotStandInForTheOriginalAgent()
    {
        using var handler = new Handler { ReplaceUid = true };
        var result = await new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(Request(), default);
        Assert.IsTrue(result.ResourceUnavailable);
        Assert.AreEqual(Request().FallbackLocation, result.Location);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError, false)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.Forbidden, true)]
    public async Task ContextDenialAndTransportFailuresDoNotBecomeMissingResourceFallbacks(HttpStatusCode status, bool contextFailure)
    {
        using var handler = new Handler { TargetStatus = contextFailure ? HttpStatusCode.OK : status, ContextStatus = contextFailure ? status : HttpStatusCode.OK };
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(Request(), default));
        Assert.AreEqual(contextFailure ? 1 : 3, handler.Requests.Count);
    }

    [TestMethod]
    [DataRow("/agents")]
    [DataRow("/entries/same#definition/configuration")]
    [DataRow("/agents/new#yaml")]
    public async Task CollectionsAndUnmigratedDetailsUseTheDeclaredSafeDestination(string path)
    {
        using var handler = new Handler();
        var request = Request(path) with { FallbackLocation = "/entries?context=" + Uri.EscapeDataString(Target) };
        var result = await new ConsoleContextSwitchResolver(new Factory(handler)).ResolveAsync(request, default);
        Assert.AreEqual(request.FallbackLocation, result.Location);
        Assert.IsFalse(result.ResourceUnavailable);
        Assert.HasCount(1, handler.Requests);
    }

    private sealed class Factory(Handler handler) : IConsoleContextHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.test/") };
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode TargetStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode ContextStatus { get; init; } = HttpStatusCode.OK;
        public bool ReplaceUid { get; init; }
        public List<(Uri Uri, string Scope, HttpMethod Method)> Requests { get; } = [];
        private readonly AgentResource resource = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = AgentResourceKinds.Agent,
            Uid = Guid.NewGuid(), ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Namespace = ResourceNamespace.Parse("team"), Name = "same" },
            Definition = new() { DisplayName = "Same", Instructions = "Deterministic fixture", ModelProfile = new("model") }
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var scope = request.Headers.GetValues(ConsoleContextHeaders.Name).Single();
            Requests.Add((request.RequestUri!, scope, request.Method));
            var contextRequest = request.RequestUri!.AbsolutePath == "/api/identity/context";
            var status = contextRequest ? ContextStatus : scope == Target ? TargetStatus : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status)
            {
                Content = contextRequest ? JsonContent.Create(new { selectedScopeRef = Target })
                    : JsonContent.Create(ReplaceUid && scope == Target ? resource with { Uid = Guid.NewGuid() } : resource)
            };
            return Task.FromResult(response);
        }
    }
}
