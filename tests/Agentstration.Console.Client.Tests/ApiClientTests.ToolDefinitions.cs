using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ToolDefinitionClientKeepsNamespaceAndEtagOnUpdate()
    {
        HttpRequestMessage? captured = null;
        var resource = new ToolDefinitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolDefinition,
            Metadata = new ResourceMetadata { Name = "notification.send", Namespace = new ResourceNamespace("communications") },
            Definition = new FlowToolDefinitionProperties
            {
                DisplayName = "Send notification",
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                Flow = new ToolDefinitionFlowTarget { Name = "delivery" }
            }
        };
        using var http = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(resource) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v2\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var result = await new ToolDefinitionsApiClient(http).UpdateAsync(
            resource.Name,
            resource.Namespace,
            new PutToolDefinitionRequest(resource.Definition),
            "\"v1\"",
            default);

        Assert.AreEqual("/api/tooldefinitions/notification.send?namespace=communications", captured!.RequestUri!.PathAndQuery);
        Assert.AreEqual("\"v1\"", captured.Headers.IfMatch.Single().Tag);
        Assert.AreEqual("\"v2\"", result.ETag);
    }
    [TestMethod]
    [DataRow("default")]
    [DataRow("communications")]
    public async Task ToolDefinitionClientKeepsTheExactOwnerForDefinitionPublicationsAndConsumers(string namespaceValue)
    {
        var owner = ResourceScopeRef.Tenant(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var ns = new ResourceNamespace(namespaceValue);
        var paths = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery; paths.Add(path);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = path.Contains("/versions?", StringComparison.Ordinal)
                    ? JsonContent.Create(new ValueResponse<ToolDefinitionVersionResource>([]))
                    : path.Contains("/usages?", StringComparison.Ordinal)
                        ? JsonContent.Create(new ValueResponse<ToolDefinitionUsage>([]))
                        : JsonContent.Create(new ToolDefinitionResource
                        {
                            ApiVersion = ResourceApiVersions.CoreV1,
                            Kind = ToolResourceKinds.ToolDefinition,
                            Metadata = new() { Name = "same-name", Namespace = ns },
                            ScopeRef = owner,
                            Definition = new FlowToolDefinitionProperties { DisplayName = "Exact owner", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }), Flow = new() { Name = "source" } }
                        })
            };
            response.Headers.ETag = new("\"exact\""); return response;
        }))
        { BaseAddress = new("http://localhost/") };
        var client = new ToolDefinitionsApiClient(http);
        var snapshot = await client.GetAsync(owner, "same-name", ns, default);
        _ = await client.GetVersionsAsync(owner, "same-name", ns, default);
        _ = await client.GetUsagesAsync(owner, "same-name", ns, default);
        var query = $"?namespace={namespaceValue}&scopeRef={Uri.EscapeDataString(owner.Value)}";
        CollectionAssert.AreEqual(new[] { "/api/tooldefinitions/same-name" + query, "/api/tooldefinitions/same-name/versions" + query, "/api/tooldefinitions/same-name/usages" + query }, paths.ToArray());
        Assert.AreEqual(owner, snapshot.Value.ScopeRef);
        Assert.AreEqual("\"exact\"", snapshot.ETag);
    }

}
