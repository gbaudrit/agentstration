using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ConnectedServicesClientReadsGovernedTypeResources()
    {
        var type = new ConnectedServiceTypeResource
        {
            Uid = Guid.NewGuid(),
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            Metadata = new() { Name = "http-endpoint" },
            ScopeRef = ResourceScopeRef.Instance,
            Governance = ResourceGovernance.Core("connected-services.http", "1.0.0"),
            Definition = new()
            {
                DisplayName = "HTTP endpoint",
                PreferredVersion = "1.0.0",
                Versions = []
            }
        };
        string? path = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            path = request.RequestUri?.PathAndQuery;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { type }) };
        })) { BaseAddress = new Uri("http://localhost/") };

        var result = await new ConnectedServicesApiClient(httpClient).GetTypesAsync(default);

        Assert.AreEqual("/api/connected-service-types", path);
        Assert.AreEqual(type.Uid, result.Single().Uid);
        Assert.AreEqual(ResourceManagementAuthorityKind.Core, result.Single().Governance.ManagedBy?.Kind);
    }

    [TestMethod]
    public async Task ConnectedServicesClientPreservesExactScopeAndConcurrencyTokenForReconcile()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var resource = ConnectedService(scope);
        string? path = null;
        string? ifMatch = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            path = request.RequestUri?.PathAndQuery;
            ifMatch = request.Headers.IfMatch.SingleOrDefault()?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
            response.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            return response;
        })) { BaseAddress = new Uri("http://localhost/") };

        var snapshot = await new ConnectedServicesApiClient(httpClient)
            .ReconcileAsync(resource.Name, scope, "\"v1\"", default);

        Assert.AreEqual($"/api/connected-services/redis/reconcile?scopeRef={Uri.EscapeDataString(scope.Value)}", path);
        Assert.AreEqual("\"v1\"", ifMatch);
        Assert.AreEqual("\"v2\"", snapshot.ETag);
    }

    private static ConnectedServiceResource ConnectedService(ResourceScopeRef scope) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedService,
        Metadata = new() { Name = "redis" },
        ScopeRef = scope,
        Definition = new()
        {
            DisplayName = "Redis",
            Type = new()
            {
                Type = new ResourceReference("redis", ResourceScopeRef.Instance, new("azure")),
                TypeUid = Guid.NewGuid(),
                Version = "1.0.0",
                SchemaDigest = "sha256:contract"
            },
            Options = JsonSerializer.SerializeToElement(new { })
        }
    };
}
