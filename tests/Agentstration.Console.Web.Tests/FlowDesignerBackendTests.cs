using System.Net;
using System.Net.Http.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Console.Web.Features.Flows;

namespace Agentstration.Console.Web.Tests;

[TestClass]
public sealed class FlowDesignerBackendTests
{
    [TestMethod]
    public async Task OpensAnExplicitPublishedVersionReadOnlyWithoutCreatingOrReadingADraft()
    {
        var definition = new WorkflowFlowDefinition("input", [], [], []);
        var flow = new FlowResponse("transform", "Transform", null, "2.0.0", true, "2.0.0", definition,
            new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) { ScopeRef = ResourceScopeRef.Instance };
        var graph = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }] };
        var version = new FlowVersionResponse("transform", "1.0.0", null, definition, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, graph);
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = request.RequestUri.AbsolutePath.EndsWith("/versions/1.0.0", StringComparison.Ordinal) ? JsonContent.Create(version) : JsonContent.Create(flow) };
        })) { BaseAddress = new Uri("http://localhost/") };
        IFlowDesignerBackend backend = new FlowDesignerBackend(new FlowApiClient(http));
        var target = new FlowDesignerTarget(ResourceNamespace.Default, "transform", ResourceScopeRef.Instance, "1.0.0");
        var loaded = await backend.LoadAsync(target, default);
        Assert.IsTrue(loaded.IsReadOnly);
        Assert.AreEqual("1.0.0", loaded.PublishedVersion);
        CollectionAssert.AreEqual(new[] { "GET /api/flows/transform", "GET /api/flows/transform/versions/1.0.0" }, requests);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.SaveDraftAsync(target, new("Transform", null, null, graph), "etag", default));
        Assert.HasCount(2, requests);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(factory(request));
    }
}
