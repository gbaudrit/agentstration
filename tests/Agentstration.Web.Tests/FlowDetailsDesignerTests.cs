using System.Text.Json;
using System.Runtime.CompilerServices;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowDetailsDesignerTests
{
    [TestMethod]
    public void NamespacedFlowKeepsDesignerTabAndUsesNamespacedLink()
    {
        using var context = new BunitContext();
        var client = new FlowClientStub();
        context.Services.AddSingleton<IFlowApiClient>(client);

        var rendered = context.Render<FlowDetails>(parameters => parameters
            .Add(component => component.FlowId, "sample")
            .Add(component => component.FlowNamespace, "pack.sample"));

        var overviewLink = rendered.FindAll("a").Single(link => link.TextContent.Trim() == "read-only Flow Designer");
        Assert.AreEqual("/namespaces/pack.sample/flows/sample/designer", overviewLink.GetAttribute("href"));
        rendered.FindAll("nav.section-tabs button").Single(button => button.TextContent.Trim() == "Designer").Click();

        var link = rendered.Find("a.button-primary");
        Assert.AreEqual("/namespaces/pack.sample/flows/sample/designer", link.GetAttribute("href"));
        Assert.AreEqual(new ResourceNamespace("pack.sample"), client.RequestedNamespace);
    }

    [TestMethod]
    public void NamespacedOrchestrationPageShowsPublishedDefinitionWithoutSaveOrPublish()
    {
        using var context = new BunitContext();
        var client = new FlowClientStub(orchestration: true);
        context.Services.AddSingleton<IFlowApiClient>(client);
        context.Services.AddSingleton<IManagementApiClient>(new ManagementClientStub());

        var rendered = context.Render<FlowOrchestrationEditor>(parameters => parameters
            .Add(component => component.FlowId, "review")
            .Add(component => component.FlowNamespace, "pack.sample"));

        StringAssert.Contains(rendered.Markup, "Read only");
        StringAssert.Contains(rendered.Markup, "Published version");
        Assert.IsFalse(rendered.FindAll("button").Any(button => button.TextContent.Contains("Save", StringComparison.Ordinal) || button.TextContent.Contains("Publish", StringComparison.Ordinal)));
        Assert.IsTrue(rendered.Find("fieldset.orchestration-configuration").HasAttribute("disabled"));
    }

    private sealed class FlowClientStub : IFlowApiClient
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        private static readonly FlowGraphDefinition Graph = new() { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
        private readonly FlowDefinition definition;
        public FlowClientStub(bool orchestration = false) => definition = orchestration
            ? new OrchestrationFlowDefinition([new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")], new SequentialOrchestrationPattern())
            : new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"));
        public ResourceNamespace RequestedNamespace { get; private set; }

        public Task<FlowResponse> GetFlowAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken)
        {
            RequestedNamespace = @namespace;
            return Task.FromResult(new FlowResponse(flowId, "Sample", null, "1.0.0", true, "1.0.0", definition, new Dictionary<string, string>(), Now, Now, Graph) { Namespace = @namespace });
        }
        public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowVersionResponse>>([new FlowVersionResponse(flowId, "1.0.0", null, definition, new Dictionary<string, string>(), Now, Graph) { Namespace = @namespace }]);
        public Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(ResourceNamespace @namespace, string flowId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FlowRun>>([]);
        public Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FlowSummary>>([]);
        public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> GetFlowSnapshotAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> CreateFlowAsync(CreateFlowRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> UpdateFlowAsync(string flowId, UpdateFlowRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> CreateFlowVersionAsync(string flowId, CreateFlowVersionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public Task<FlowDraftResponse> CreateDraftAsync(CreateFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> GetDraftAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ManagementClientStub : IManagementApiClient
    {
        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentSummary>>([]);
        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
