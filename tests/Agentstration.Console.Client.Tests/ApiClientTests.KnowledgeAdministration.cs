using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ArtifactClientRequiresExplicitContentReadAndPreservesBoundedRange()
    {
        var requests = new List<string>();
        var artifact = Staged();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(request.RequestUri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                    ? (object)new ArtifactContentChunk(0, Convert.ToBase64String("preview"u8), true)
                    : artifact)
            };
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new ArtifactsApiClient(http);

        _ = await client.GetStagedAsync(artifact.ArtifactId);
        Assert.HasCount(1, requests);
        var content = await client.ReadContentAsync(artifact.ArtifactId, 10, 512);

        Assert.AreEqual("/api/artifacts/staged/11111111111111111111111111111111/content?offset=10&length=512", requests[1]);
        Assert.AreEqual("preview", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)));
        Assert.AreEqual("/api/artifacts/staged/11111111111111111111111111111111/download",
            client.GetDownloadUrl(artifact.ArtifactId));
    }

    [TestMethod]
    public async Task ArtifactClientGetsOneDurableArtifactByIdentity()
    {
        var id = FlowRunArtifactId.Parse("22222222222222222222222222222222");
        string? requestPath = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requestPath = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new ArtifactsApiClient(http);

        var result = await client.GetDurableAsync(id);

        Assert.IsNull(result);
        Assert.AreEqual("/api/artifacts/flow-run-artifacts/22222222222222222222222222222222", requestPath);
    }

    [TestMethod]
    public async Task ArtifactClientStartsAndTracksDurableMaterialization()
    {
        var id = FlowRunArtifactId.Parse("22222222222222222222222222222222");
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(Clone(request));
            var materialization = new FlowRunArtifactMaterialization(
                "flowrun-read-1", "Succeeded", StagedArtifactId.Parse("11111111111111111111111111111111"),
                StagedArtifactAvailable: true);
            return new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            {
                Content = request.RequestUri!.Query.Contains("maximum", StringComparison.Ordinal)
                    ? JsonContent.Create<IReadOnlyList<FlowRunArtifactMaterialization>>([materialization])
                    : JsonContent.Create(materialization)
            };
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new ArtifactsApiClient(http);

        var started = await client.StartMaterializationAsync(id);
        var completed = await client.GetMaterializationAsync(id, started.FlowRunId);
        var history = await client.GetMaterializationsAsync(id);

        Assert.AreEqual(HttpMethod.Post, requests[0].Method);
        Assert.AreEqual("/api/artifacts/flow-run-artifacts/22222222222222222222222222222222/materializations",
            requests[0].RequestUri!.AbsolutePath);
        Assert.AreEqual("/api/artifacts/flow-run-artifacts/22222222222222222222222222222222/materializations/flowrun-read-1",
            requests[1].RequestUri!.AbsolutePath);
        Assert.AreEqual("/api/artifacts/flow-run-artifacts/22222222222222222222222222222222/materializations?maximum=50",
            requests[2].RequestUri!.PathAndQuery);
        Assert.IsNotNull(completed?.StagedArtifactId);
        Assert.AreEqual(1, history.Count);
        Assert.IsTrue(history[0].StagedArtifactAvailable);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return copy;
    }

    private static KnowledgeSourceResource Source() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeSource,
        Metadata = new ResourceMetadata { Name = "docs", Namespace = new ResourceNamespace("shared") },
        Definition = new KnowledgeSourceProperties { DisplayName = "Documentation", Enabled = true }
    };

    private static StagedArtifactView Staged() => new(
        StagedArtifactId.Parse("11111111111111111111111111111111"), null, "document.txt", "text/plain", 7, null,
        StagedArtifactStatus.Sealed, new ArtifactProducer { Kind = ArtifactProducerKind.FlowRun, Id = "run" },
        "local", ResourceNamespace.Default, "local-staging", ResourceNamespace.Default, "1.0.0",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null, null, null, null, []);
}
