using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ProviderCommandsRetainExactCoordinatesAndEtag()
    {
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid()); var ns = new ResourceNamespace("shared.providers");
        var requests = new List<(HttpMethod Method, string Path, string? Etag)>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.IfMatch.SingleOrDefault()?.ToString()));
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = request.Method == HttpMethod.Put ? JsonContent.Create(new ToolProviderResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolProvider,
                ScopeRef = scope,
                Metadata = new() { Name = "same", Namespace = ns },
                Definition = new() { DisplayName = "Saved", ProviderType = ToolProviderType.Mcp }
            }) : request.RequestUri!.AbsolutePath.EndsWith("/test", StringComparison.Ordinal)
                ? JsonContent.Create(new ToolConnectionTestResponse("connected", 0, new Dictionary<string, bool>(), new Dictionary<string, string>()))
                : JsonContent.Create(new ToolDiscoveryDiffResponse(0, 0, 0, 0, 0));
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"updated\""); return response;
        }))
        { BaseAddress = new("http://localhost/") };
        var client = new ToolsApiClient(http);
        _ = await client.UpdateProviderAsync(scope, ns, "same", new(new() { DisplayName = "Saved", ProviderType = ToolProviderType.Mcp }), "\"original\"", default);
        _ = await client.TestAsync(scope, ns, "same", default);
        _ = await client.RefreshAsync(scope, ns, "same", default);
        var query = $"?resourceNamespace=shared.providers&scopeRef={Uri.EscapeDataString(scope.Value)}";
        CollectionAssert.AreEqual(new[] { "/api/toolproviders/same" + query, "/api/toolproviders/same/test" + query, "/api/toolproviders/same/refresh" + query }, requests.Select(r => r.Path).ToArray());
        Assert.AreEqual("\"original\"", requests[0].Etag); Assert.AreEqual(HttpMethod.Put, requests[0].Method);
    }

    [TestMethod]
    public async Task AdministrativeToolReadsUseExactFamilyRoutesAndSeparateOperationalEtag()
    {
        var owner = ResourceScopeRef.Instance;
        var ns = new ResourceNamespace("shared.tools");
        var uid = Guid.NewGuid();
        var paths = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath.EndsWith("/activation", StringComparison.Ordinal)
                    ? JsonContent.Create(new ToolOperationalStatus(uid, true, "\"state\""))
                    : JsonContent.Create(new ToolProviderAdministration([], []))
            };
        })) { BaseAddress = new("http://localhost/") };
        var client = new ToolsApiClient(http);
        var state = await client.GetOperationalStateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.Tool, Uid = uid,
            ScopeRef = owner, Metadata = new() { Name = "read", Namespace = ns },
            Definition = new() { DisplayName = "Read", Enabled = false }
        }, default);
        _ = await client.GetProviderAdministrationAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider,
            ScopeRef = owner, Metadata = new() { Name = "github", Namespace = ns },
            Definition = new() { DisplayName = "GitHub", ProviderType = ToolProviderType.Aep }
        }, default);
        Assert.IsTrue(state.Enabled);
        Assert.AreEqual("\"state\"", state.ETag);
        CollectionAssert.AreEqual(new[]
        {
            "/api/tools/read/activation?namespace=shared.tools&scopeRef=%2Finstance",
            "/api/toolproviders/github/administration?resourceNamespace=shared.tools&scopeRef=%2Finstance"
        }, paths);
    }

    [TestMethod]
    public async Task ToolClientListsDesignerToolsInTheExplicitFlowContext()
    {
        var scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        string? context = null;
        string? path = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            context = request.Headers.GetValues("X-Agentstration-Context").Single();
            path = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Agentstration.Api.Contracts.ValueResponse<ToolResource>([]))
            };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        _ = await new ToolsApiClient(httpClient).GetToolsAsync(scope, cancellationToken: default);

        Assert.AreEqual(scope.Value, context);
        Assert.AreEqual($"/api/tools?scopeRef={Uri.EscapeDataString(scope.Value)}", path);
    }

    [TestMethod]
    public async Task ToolClientReadsExactScopedTool()
    {
        HttpRequestMessage? captured = null;
        var scopeRef = ResourceScopeRef.Instance;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ToolResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ToolResourceKinds.Tool,
                    Metadata = new ResourceMetadata { Name = "search", Namespace = new("shared.tools") },
                    ScopeRef = scopeRef,
                    Definition = new ToolResourceProperties { DisplayName = "Search" }
                })
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"tool-1\"");
            return response;
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var snapshot = await new ToolsApiClient(httpClient).GetToolAsync(
            scopeRef,
            new ResourceNamespace("shared.tools"),
            "search",
            default);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Get, captured.Method);
        Assert.AreEqual("/api/tools/search?namespace=shared.tools&scopeRef=%2Finstance", captured.RequestUri!.PathAndQuery);
        Assert.AreEqual(scopeRef, snapshot.Value.ScopeRef);
    }

    [TestMethod]
    public async Task ToolClientRunsNamespacedToolInRequestedMode()
    {
        HttpRequestMessage? captured = null;
        RunToolRequest? payload = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            payload = request.Content!.ReadFromJsonAsync<RunToolRequest>().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RunToolResponse(
                    ToolRunMode.Simulate,
                    "simulated",
                    "search",
                    "shared.tools",
                    "utilities",
                    false,
                    false,
                    []))
            };
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var response = await new ToolsApiClient(httpClient).RunToolAsync(
            new ResourceNamespace("shared.tools"),
            "search",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { query = "agent" })),
            default);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Post, captured.Method);
        Assert.AreEqual("/api/tools/search/run?namespace=shared.tools", captured.RequestUri!.PathAndQuery);
        Assert.IsNotNull(payload);
        Assert.AreEqual(ToolRunMode.Simulate, payload.Mode);
        Assert.AreEqual("agent", payload.Arguments.GetProperty("query").GetString());
        Assert.IsFalse(response.ProviderInvoked);
    }
}
