using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowDetailsDesignerTests
{
    [TestMethod]
    public void NamespacedFlowOrdersDefinitionSecondAndExposesReadOnlyYamlLast()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FlowClientStub();
        context.Services.AddSingleton<IFlowApiClient>(client);
        var strings = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<FlowDetailsStrings>>();

        var rendered = context.Render<FlowDetails>(parameters => parameters
            .Add(component => component.FlowId, "sample")
            .Add(component => component.FlowNamespace, "pack.sample"));

        var overviewLink = rendered.FindAll("a").Single(link => link.TextContent.Trim() == strings["ReadOnlyDesigner"].Value);
        Assert.AreEqual("/namespaces/pack.sample/flows/sample/designer", overviewLink.GetAttribute("href"));
        CollectionAssert.AreEqual(
            new[] { strings["Tab.Overview"].Value, strings["Tab.Definition"].Value, strings["Tab.Runs"].Value, strings["Tab.Deployments"].Value, strings["Tab.YAML"].Value },
            rendered.FindAll("nav.section-tabs button").Select(button => button.TextContent.Trim()).ToArray());
        rendered.FindAll("nav.section-tabs button").Single(button => button.TextContent.Trim() == strings["Tab.Definition"].Value).Click();

        var link = rendered.Find("a.button-primary");
        Assert.AreEqual("/namespaces/pack.sample/flows/sample/designer", link.GetAttribute("href"));
        Assert.AreEqual(new ResourceNamespace("pack.sample"), client.RequestedNamespace);
        rendered.FindAll("nav.section-tabs button").Single(button => button.TextContent.Trim() == strings["Tab.YAML"].Value).Click();
        var yaml = rendered.Find("[data-testid='flow-yaml-viewer'] textarea");
        Assert.IsTrue(yaml.HasAttribute("readonly"));
        Assert.IsTrue(yaml.TextContent.Contains("entryStep: input", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NamespacedOrchestrationPageShowsPublishedDefinitionWithoutSaveOrPublish()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FlowClientStub(orchestration: true);
        context.Services.AddSingleton<IFlowApiClient>(client);
        context.Services.AddSingleton<IManagementApiClient>(new ManagementClientStub());
        var strings = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<FlowOrchestrationEditorStrings>>();

        var rendered = context.Render<FlowOrchestrationEditor>(parameters => parameters
            .Add(component => component.FlowId, "review")
            .Add(component => component.FlowNamespace, "pack.sample"));

        StringAssert.Contains(rendered.Markup, strings["ReadOnly"].Value);
        StringAssert.Contains(rendered.Markup, strings["PublishedVersion"].Value);
        Assert.IsFalse(rendered.FindAll("button").Any(button => button.TextContent.Contains(strings["Save"].Value, StringComparison.Ordinal) || button.TextContent.Contains(strings["PublishAndActivate"].Value, StringComparison.Ordinal)));
        Assert.IsTrue(rendered.Find("fieldset.orchestration-configuration").HasAttribute("disabled"));
    }

    [TestMethod]
    public void OrchestrationDefinitionTabEmbedsTheEditableEditorWithoutAnIntermediateLink()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub(orchestration: true));
        context.Services.AddSingleton<IManagementApiClient>(new ManagementClientStub());
        var detailsStrings = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<FlowDetailsStrings>>();
        var editorStrings = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<FlowOrchestrationEditorStrings>>();

        var rendered = context.Render<FlowDetails>(parameters => parameters.Add(component => component.FlowId, "review"));
        Assert.HasCount(1, rendered.FindAll(".flow-overview-preview .topology-shell"));
        StringAssert.Contains(rendered.Find(".flow-overview-preview").TextContent, detailsStrings["ExecutionPreview"].Value);
        rendered.FindAll("nav.section-tabs button").Single(button => button.TextContent.Trim() == detailsStrings["Tab.Definition"].Value).Click();

        Assert.HasCount(1, rendered.FindAll("fieldset.orchestration-configuration"));
        Assert.IsFalse(rendered.FindAll("a, button").Any(element => element.TextContent.Contains(detailsStrings["OpenOrchestrationEditor"].Value, StringComparison.Ordinal)));
        Assert.IsTrue(rendered.FindAll("button").Any(button => button.TextContent.Contains(editorStrings["Save"].Value, StringComparison.Ordinal)));
        Assert.IsTrue(rendered.FindAll("button").Any(button => button.TextContent.Contains(editorStrings["PublishAndActivate"].Value, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FlowRunCausalityTabShowsVersionsAttemptsAndGovernanceDeepLink()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var flowId = new FlowId("news-analysis");
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "analyst"));
        var run = new FlowRun
        {
            WorkspaceId = workspaceId,
            Id = "flowrun-news",
            FlowId = flowId,
            FlowVersion = "2.0.0",
            ResolvedFromActiveReference = true,
            Status = FlowRunStatus.Succeeded,
            InvocationOrigin = FlowInvocationOrigin.Trigger,
            Trigger = FlowRunTrigger.Event,
            CallerId = "news-watcher",
            CorrelationId = "news-42",
            Scope = new FlowRunScope(Guid.NewGuid(), workspaceId, Guid.NewGuid()),
            Input = JsonSerializer.SerializeToElement(new { }),
            CreatedAt = FlowClientStub.Now,
            DefinitionSnapshot = new FlowVersion(workspaceId, flowId, "2.0.0", null, definition, new Dictionary<string, string>(), FlowClientStub.Now)
        };
        const string logicalCallId = "flow:flowrun-news:step:notify";
        const string invocationId = "flow:flowrun-news:step:notify:attempt:1";
        var node = new FlowRunCausalityNode(
            run.Id, flowId, run.FlowVersion, true, FlowDefinitionState.Published, run.Status, null, null, 0,
            run.CreatedAt, run.StartedAt, run.CompletedAt, null, [],
            [new(logicalCallId, "notify", "notification.send", "agentstration", "notification.send", "agentstration.internal", "agentstration", "work.notification.create", "succeeded", run.CorrelationId,
                [new(invocationId, 1, "succeeded", run.CreatedAt, run.CreatedAt.AddMilliseconds(12), 12, null, null, 1)])]);
        var causality = new FlowRunCausalityPageResponse(
            new(run.Id, FlowInvocationOrigin.Trigger, FlowRunTrigger.Event, "news-watcher", "news-item-42", "news-42", Guid.NewGuid().ToString("D")),
            [node], 1, null);
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub(run: run, causality: causality));
        context.Services.AddSingleton<IConsoleRealtimeConnectionConfigurator>(NoOpRealtimeConfigurator.Instance);
        context.Services.AddSingleton(TimeProvider.System);
        var strings = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<FlowRunDetailsStrings>>();

        var rendered = context.Render<FlowRunDetails>(parameters => parameters.Add(component => component.RunId, run.Id));
        rendered.FindAll("nav.section-tabs button").Single(button => button.TextContent.Trim() == strings["Causality"].Value).Click();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "news-analysis · 2.0.0");
            StringAssert.Contains(rendered.Markup, strings["ResolvedFromActive"].Value);
            StringAssert.Contains(rendered.Markup, "notification.send");
            var link = rendered.Find(".tool-attempts a");
            StringAssert.Contains(link.GetAttribute("href"), "toolCallId=flow%3Aflowrun-news%3Astep%3Anotify");
            StringAssert.Contains(link.GetAttribute("href"), "invocationId=flow%3Aflowrun-news%3Astep%3Anotify%3Aattempt%3A1");
        });
    }

    [TestMethod]
    public void MissingFlowRunRendersNotFoundStateInsteadOfThrowing()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub());
        context.Services.AddSingleton<IConsoleRealtimeConnectionConfigurator>(NoOpRealtimeConfigurator.Instance);
        context.Services.AddSingleton(TimeProvider.System);
        var strings = context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<FlowRunDetailsStrings>>();

        var rendered = context.Render<FlowRunDetails>(parameters => parameters
            .Add(component => component.RunId, "flowrun-missing"));

        StringAssert.Contains(rendered.Markup, strings["NotFoundTitle"].Value);
        StringAssert.Contains(rendered.Markup, strings["NotFoundMessage"].Value);
        Assert.AreEqual("/flow-runs", rendered.Find("a.button-secondary").GetAttribute("href"));
    }

    private sealed class FlowClientStub : IFlowApiClient
    {
        public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        private static readonly FlowGraphDefinition Graph = new() { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
        private readonly FlowDefinition definition;
        private readonly FlowRun? run;
        private readonly FlowRunCausalityPageResponse? causality;
        public FlowClientStub(bool orchestration = false, FlowRun? run = null, FlowRunCausalityPageResponse? causality = null)
        {
            definition = orchestration
                ? new OrchestrationFlowDefinition([new(FlowTargetKind.Agent, "agent-a"), new(FlowTargetKind.Agent, "agent-b")], new SequentialOrchestrationPattern())
                : new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"));
            this.run = run;
            this.causality = causality;
        }
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
        public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) =>
            GetFlowAsync(ResourceNamespace.Default, flowId, cancellationToken);
        public async Task<FlowResourceSnapshot> GetFlowSnapshotAsync(string flowId, CancellationToken cancellationToken) =>
            new(await GetFlowAsync(ResourceNamespace.Default, flowId, cancellationToken), "\"etag-1\"");
        public Task<FlowResourceSnapshot> CreateFlowAsync(CreateFlowRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> UpdateFlowAsync(string flowId, UpdateFlowRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> CreateFlowVersionAsync(string flowId, CreateFlowVersionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) => run is not null && run.Id == runId
            ? Task.FromResult(run)
            : throw new AgentstrationApiException("The Flow Run was not found.", "missing-flow-run", System.Net.HttpStatusCode.NotFound, "flow_run_not_found");
        public Task<FlowRunCausalityPageResponse> GetFlowRunCausalityAsync(string runId, CancellationToken cancellationToken) => causality is not null
            ? Task.FromResult(causality)
            : throw new NotSupportedException();
        public Task<IReadOnlyList<InputRequest>> GetFlowRunInputsAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InputRequest> RespondToFlowRunInputAsync(string runId, string inputId, JsonElement value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowRunEvent>>([]);
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
        public Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeploymentSummary>>([]);
        public Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TriggerResource>>([]);
        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoOpRealtimeConfigurator : IConsoleRealtimeConnectionConfigurator
    {
        public static NoOpRealtimeConfigurator Instance { get; } = new();

        public void Configure(Uri hubUri, HttpConnectionOptions options)
        {
        }
    }
}
