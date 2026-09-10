using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
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
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ToolDefinition,
            Metadata = new ResourceMetadata { Name = "notification.send", Namespace = new ResourceNamespace("communications") },
            Definition = new ToolDefinitionProperties
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
}
