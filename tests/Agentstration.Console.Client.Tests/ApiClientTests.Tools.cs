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
    public async Task ToolClientListsDesignerToolsInTheExplicitFlowContext()
    {
        var scope = ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        string? context = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            context = request.Headers.GetValues("X-Agentstration-Context").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Agentstration.Api.Contracts.ValueResponse<ToolResource>([]))
            };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        _ = await new ToolsApiClient(httpClient).GetToolsAsync(scope, cancellationToken: default);

        Assert.AreEqual(scope.Value, context);
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
