using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Api.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ToolSetClientPreservesNamespaceAndConcurrencyToken()
    {
        var requests = new List<(HttpMethod Method, string Path, string? IfMatch)>();
        var toolSet = ToolSet("documentation", new ResourceNamespace("shared.knowledge"));
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.IfMatch.SingleOrDefault()?.ToString()));
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(toolSet) };
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"toolset-etag\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new ToolSetsApiClient(http);

        var snapshot = await client.GetToolSetAsync(toolSet.Namespace, toolSet.Metadata.Name);
        await client.DeleteAsync(toolSet.Namespace, toolSet.Metadata.Name, snapshot.ETag);

        Assert.AreEqual("/api/toolsets/documentation?resourceNamespace=shared.knowledge", requests[0].Path);
        Assert.AreEqual("/api/toolsets/documentation?resourceNamespace=shared.knowledge", requests[1].Path);
        Assert.AreEqual("\"toolset-etag\"", requests[1].IfMatch);
    }

    [TestMethod]
    public async Task KnowledgeSourceClientPublishesExposureInTheSelectedNamespace()
    {
        Uri? requested = null;
        PublishKnowledgeSourceToolExposureRequest? submitted = null;
        var sourceNamespace = new ResourceNamespace("shared.knowledge");
        var exposure = Exposure(sourceNamespace);
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            submitted = request.Content!.ReadFromJsonAsync<PublishKnowledgeSourceToolExposureRequest>().GetAwaiter().GetResult();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(exposure) };
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"exposure-etag\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var snapshot = await new KnowledgeSourcesApiClient(http).PublishExposureAsync(
            sourceNamespace,
            "documentation",
            new PublishKnowledgeSourceToolExposureRequest { Version = "2.0.0", RequiresApproval = true });

        Assert.AreEqual("/api/knowledgesources/documentation/tool-exposure?namespace=shared.knowledge", requested!.PathAndQuery);
        Assert.AreEqual("2.0.0", submitted!.Version);
        Assert.IsTrue(submitted.RequiresApproval);
        Assert.AreEqual("\"exposure-etag\"", snapshot.ETag);
    }

    [TestMethod]
    public async Task KnowledgeSourceClientMapsMissingExposureToNull()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)))
        { BaseAddress = new Uri("http://localhost/") };

        var result = await new KnowledgeSourcesApiClient(http).GetExposureAsync(ResourceNamespace.Default, "documentation");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task KnowledgeSourceClientStartsProjectionInTheSelectedNamespace()
    {
        Uri? requested = null;
        StartKnowledgeProjectionRequest? submitted = null;
        var sourceNamespace = new ResourceNamespace("shared.knowledge");
        var projection = Projection(sourceNamespace);
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            submitted = request.Content!.ReadFromJsonAsync<StartKnowledgeProjectionRequest>().GetAwaiter().GetResult();
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(projection) };
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"projection-etag\"");
            return response;
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var snapshot = await new KnowledgeSourcesApiClient(http).StartProjectionAsync(
            sourceNamespace,
            "documentation",
            new StartKnowledgeProjectionRequest
            {
                AcquisitionIds = new Dictionary<string, string> { ["website"] = "acquisition-selected" }
            });

        Assert.AreEqual("/api/knowledgesources/documentation/projections?namespace=shared.knowledge",
            requested!.PathAndQuery);
        Assert.AreEqual("acquisition-selected", submitted!.AcquisitionIds["website"]);
        Assert.AreEqual("\"projection-etag\"", snapshot.ETag);
    }

    private static ToolSetResource ToolSet(string name, ResourceNamespace @namespace) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolSet,
        Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
        Definition = new ToolSetProperties { DisplayName = "Documentation", Members = [] }
    };

    private static KnowledgeSourceToolExposureResource Exposure(ResourceNamespace @namespace) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeSourceToolExposure,
        Metadata = new ResourceMetadata { Name = "documentation", Namespace = @namespace },
        KnowledgeSourceUid = Guid.NewGuid(),
        KnowledgeSourceName = "documentation",
        KnowledgeSourceGeneration = 1,
        RetrievalFlow = new ResolvedKnowledgeFlowBinding(
            "retrieve-documentation", @namespace, "1.0.0", false, null, null),
        ToolSet = new ResourceReference("documentation", @namespace: @namespace),
        ToolSetVersion = "2.0.0"
    };

    private static KnowledgeProjectionResource Projection(ResourceNamespace @namespace) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeProjection,
        Metadata = new() { Name = "projection-test", Namespace = @namespace },
        KnowledgeSourceUid = Guid.NewGuid(),
        KnowledgeSourceName = "documentation",
        KnowledgeSourceNamespace = @namespace,
        KnowledgeSourceGeneration = 1,
        ProjectionFlow = new("project", @namespace, "1.0.0", false, null, null,
            KnowledgeFlowContracts.Projection),
        RetrievalFlow = new("retrieve", @namespace, "1.0.0", false, null, null,
            KnowledgeFlowContracts.Retrieval),
        ProjectionFlowRunId = "flowrun-projection-test",
        State = KnowledgeProjectionState.Succeeded,
        CorrelationId = "projection-test",
        CreatedBy = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        CreatedAt = new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero)
    };
}
