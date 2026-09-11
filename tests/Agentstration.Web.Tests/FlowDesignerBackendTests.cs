using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowDesignerBackendTests
{
    private static readonly WorkspaceId TestWorkspaceId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [TestMethod]
    public async Task MaterializesDraftFromActivePublishedVersion()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var flowId = new FlowId("universal-router");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-id"));
        var flow = new FlowResponse(flowId.Value, flowId.Value, null, "1.0.0", true, "1.0.0", definition, new Dictionary<string, string>(), now, now);
        var draft = new FlowDraftResponse(new FlowDraft
        {
            WorkspaceId = TestWorkspaceId,
            Id = "draft-universal-router",
            FlowId = flowId,
            DisplayName = "Universal router",
            Definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] },
            CreatedAt = now,
            UpdatedAt = now
        }, "\"draft-etag\"");
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/draft", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { title = "flow_draft_not_found", status = 404 }) };
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/draft/source", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new FlowSourceResponse("entryStep: input", "yaml", 1)) };
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(draft) };
        }))
        { BaseAddress = new Uri("http://localhost/") };

        var actual = await new FlowDesignerBackend(new FlowApiClient(httpClient)).LoadAsync(new(ResourceNamespace.Default, flowId.Value), default);

        Assert.AreEqual(flowId, actual.Resource.FlowId);
        CollectionAssert.AreEqual(new[]
        {
            "GET /api/flows/universal-router/draft",
            "GET /api/flows/universal-router",
            "POST /api/flows/universal-router/versions/1.0.0/draft",
            "GET /api/flows/universal-router/draft/source"
        }, requests);
    }

    [TestMethod]
    public async Task LoadsNamespacedPublishedGraphWithoutDraftCallsAndRejectsMutations()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var @namespace = new ResourceNamespace("pack.sample");
        var graph = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-id"));
        var flow = new FlowResponse("sample", "Pack sample", null, "1.2.0", true, "1.2.0", definition, new Dictionary<string, string>(), now, now) { Namespace = @namespace };
        var version = new FlowVersionResponse("sample", "1.2.0", null, definition, new Dictionary<string, string>(), now, graph) { Namespace = @namespace };
        var requests = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return request.RequestUri.AbsolutePath.EndsWith("/versions/1.2.0", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(version) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var backend = new FlowDesignerBackend(new FlowApiClient(httpClient));
        var target = new FlowDesignerTarget(@namespace, "sample");

        var loaded = await backend.LoadAsync(target, default);

        Assert.AreEqual("1.2.0", loaded.PublishedVersion);
        StringAssert.Contains(loaded.Source, "entryStep: input");
        CollectionAssert.AreEqual(new[]
        {
            "GET /api/namespaces/pack.sample/flows/sample",
            "GET /api/namespaces/pack.sample/flows/sample/versions/1.2.0"
        }, requests);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.SaveDraftAsync(target, new("Sample", null, null, graph), string.Empty, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.ReplaceSourceAsync(target, new("entryStep: input"), string.Empty, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.PublishAsync(target, new("1.3.0"), default));
        using var input = JsonDocument.Parse("{}");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => backend.RunDraftAsync(target, new(input.RootElement.Clone()), default));
        Assert.HasCount(2, requests);
    }

    [TestMethod]
    public async Task ReportsLegacyNamespacedVersionWithoutGraph()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var @namespace = new ResourceNamespace("pack.legacy");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-id"));
        var flow = new FlowResponse("legacy", "Legacy", null, "1.0.0", true, "1.0.0", definition, new Dictionary<string, string>(), now, now) { Namespace = @namespace };
        var version = new FlowVersionResponse("legacy", "1.0.0", null, definition, new Dictionary<string, string>(), now) { Namespace = @namespace };
        using var httpClient = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/versions/1.0.0", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(version) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(flow) }))
        { BaseAddress = new Uri("http://localhost/") };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new FlowDesignerBackend(new FlowApiClient(httpClient)).LoadAsync(new(@namespace, "legacy"), default));

        StringAssert.Contains(exception.Message, "legacy Flow version without a Graph");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
