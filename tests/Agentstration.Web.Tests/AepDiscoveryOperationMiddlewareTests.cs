using System.Net;
using System.Net.Http.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Contracts;
using Agentstration.Resources;
using Agentstration.Tools.Mcp;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class AepDiscoveryOperationMiddlewareTests
{
    [TestMethod]
    [DataRow("/api/datasources/feed/readiness", "application/json", 2)]
    [DataRow("/bff/resources", "application/json", 2)]
    [DataRow("/mcp", "application/json", 4)]
    [DataRow("/api/events", "text/event-stream", 4)]
    public async Task FiniteRestRequestsShareDiscoveriesWithoutSharingBetweenRequestsOrStreamingSessions(
        string path, string accept, int expectedCalls)
    {
        var http = new ManifestHandler();
        var transport = new AepMcpTransport(http, NullLoggerFactory.Instance);
        var extension = new ResolvedAepExtension(new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionRegistration,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "fixture" },
            Definition = new() { DisplayName = "Fixture", Endpoint = new Uri("http://extension.test/") }
        }, null);
        var middleware = new AepDiscoveryOperationMiddleware(async context =>
        {
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
            _ = await transport.DiscoverAsync(extension, "example.extension", default);
        });
        for (var request = 0; request < 2; request++)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Headers.Accept = accept;
            await middleware.InvokeAsync(context, transport);
        }
        Assert.AreEqual(expectedCalls, http.Calls);
    }

    private sealed class ManifestHandler : HttpMessageHandler, IHttpMessageHandlerFactory
    {
        public int Calls { get; private set; }
        public HttpMessageHandler CreateHandler(string name) => this;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AepManifest(AepProtocol.Version,
                    new("example.extension", "Fixture", "1.0.0"), new Dictionary<string, AepCapabilityDescriptor>(), new([])), options: AepProtocol.JsonOptions)
            });
        }
    }
}
