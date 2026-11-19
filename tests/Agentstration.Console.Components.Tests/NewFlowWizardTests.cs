using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Triggers;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Flows;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class NewFlowWizardTests
{
    [TestMethod]
    public async Task IncompleteStepsAreFreelyReachableAndFinalValidationReturnsToIdentityWithoutWriting()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new Handler(); using var context = Context(handler); var rendered = context.Render<NewFlow>();
        Assert.IsFalse(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty, "Initial authorized defaults are not a user draft.");
        Assert.AreEqual("multi-agent-review", rendered.Find("[data-testid=flow-name]").GetAttribute("value"));
        var wizard = rendered.FindComponent<CreationWizard>();
        rendered.Find("[data-testid=flow-display-name]").Input("");
        rendered.Find("[data-testid=flow-name]").Input("");
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("configuration"));
        Assert.AreEqual("configuration", wizard.Instance.CurrentStepId);
        Assert.AreEqual("false", rendered.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete"));
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("review"));
        await rendered.InvokeAsync(wizard.Instance.FinishAsync);
        Assert.AreEqual("identity", wizard.Instance.CurrentStepId);
        Assert.HasCount(2, rendered.FindAll(".validation-message"));
        Assert.AreEqual(0, handler.Writes);
        Assert.IsNotNull(rendered.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task OrchestrationRetainsRawInputAcrossStepsAndCreatesTheExactDefinitionWithoutPublication()
    {
        using var handler = new Handler(); using var context = Context(handler); var rendered = context.Render<NewFlow>();
        rendered.Find("[data-testid=flow-name]").Input("scoped-review");
        rendered.Find("[data-testid=flow-display-name]").Input("Scoped review");
        rendered.Find("[data-testid=flow-kind-orchestration] input").Change("Orchestration");
        rendered.Find("[data-testid=flow-version]").Change("1.2.3");
        var wizard = rendered.FindComponent<CreationWizard>();
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("configuration"));
        rendered.Find("[data-testid=flow-participant][data-agent-id=agent-a]").Change(true);
        rendered.Find("[data-testid=flow-participant][data-agent-id=agent-b]").Change(true);
        rendered.Find("[data-testid=flow-enabled]").Change(false);
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("identity"));
        Assert.AreEqual("scoped-review", rendered.Find("[data-testid=flow-name]").GetAttribute("value"));
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("configuration"));
        var editor = rendered.FindComponent<OrchestrationDefinitionEditor>().Instance;
        editor.Model.Strategy = FlowOrchestrationStrategy.GroupChat;
        await rendered.InvokeAsync(() => rendered.Render());
        rendered.Find("[data-testid=flow-strategy-section] input[type=number]").Change("invalid");
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("review"));
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("configuration"));
        Assert.AreSame(editor, rendered.FindComponent<OrchestrationDefinitionEditor>().Instance);
        Assert.AreEqual("invalid", rendered.Find("[data-testid=flow-strategy-section] input[type=number]").GetAttribute("value"));
        rendered.Find("[data-testid=flow-strategy-section] input[type=number]").Change("7");
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("review"));
        await rendered.InvokeAsync(wizard.Instance.FinishAsync);
        rendered.WaitForAssertion(() => Assert.AreEqual(1, handler.Writes));
        Assert.AreEqual(Owner, handler.Created!.ScopeRef);
        Assert.AreEqual("scoped-review", handler.Created.Name);
        Assert.AreEqual("Scoped review", handler.Created.DisplayName);
        Assert.AreEqual("1.2.3", handler.Created.Version);
        Assert.IsFalse(handler.Created.Enabled);
        var definition = (OrchestrationFlowDefinition)handler.Created.Definition;
        CollectionAssert.AreEqual(new[] { "agent-a", "agent-b" }, definition.Participants.Select(p => p.Id).ToArray());
        Assert.AreEqual(7, ((GroupChatOrchestrationPattern)definition.Pattern).MaximumIterations);
        var url = context.Services.GetRequiredService<NavigationManager>().Uri;
        Assert.IsTrue(url.Contains(Uri.EscapeDataString(Owner.Value), StringComparison.Ordinal));
        Assert.IsTrue(url.EndsWith("#definition/configuration", StringComparison.Ordinal));
        Assert.IsFalse(rendered.FindComponent<UnsavedNavigationGuard>().Instance.IsDirty);
    }

    [TestMethod]
    public async Task WorkflowCreatesRevisionOneUsingTheSelectedTemplateAndOwnerWithoutPublishing()
    {
        using var handler = new Handler(); using var context = Context(handler); var rendered = context.Render<NewFlow>();
        rendered.Find("[data-testid=flow-name]").Input("scoped-workflow");
        var wizard = rendered.FindComponent<CreationWizard>();
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("configuration"));
        rendered.Find("input[type=radio][value=EmptyFlow]").Change("EmptyFlow");
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("review"));
        await rendered.InvokeAsync(wizard.Instance.FinishAsync);
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Draft));
        Assert.AreEqual(Owner, handler.Draft!.ScopeRef);
        Assert.AreEqual("EmptyFlow", handler.Draft.Template);
        Assert.AreEqual("scoped-workflow", handler.Draft.Name);
        Assert.IsNull(handler.Created);
        Assert.AreEqual(1, handler.Writes);
        StringAssert.Contains(context.Services.GetRequiredService<NavigationManager>().Uri, "/flows/scoped-workflow/designer?scopeRef=");
    }

    [TestMethod]
    public async Task MissingScopeCannotDefaultToInstance()
    {
        using var culture = new TestCultureScope("en-US");
        using var handler = new Handler(); using var context = Context(handler, noScope: true); var rendered = context.Render<NewFlow>();
        var wizard = rendered.FindComponent<CreationWizard>();
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("review"));
        await rendered.InvokeAsync(wizard.Instance.FinishAsync);
        Assert.AreEqual("identity", wizard.Instance.CurrentStepId);
        Assert.AreEqual(0, handler.Writes);
        Assert.IsTrue(rendered.Markup.Contains("Choose an authorized scope", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task IncompleteOrchestrationReturnsToConfigurationAndCanBeCorrectedWithoutLosingIdentity()
    {
        using var handler = new Handler(); using var context = Context(handler); var rendered = context.Render<NewFlow>();
        rendered.Find("[data-testid=flow-name]").Input("recoverable-team");
        rendered.Find("[data-testid=flow-kind-orchestration] input").Change("Orchestration");
        var wizard = rendered.FindComponent<CreationWizard>();
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("review"));
        await rendered.InvokeAsync(wizard.Instance.FinishAsync);
        Assert.AreEqual("configuration", wizard.Instance.CurrentStepId);
        Assert.AreEqual(0, handler.Writes);
        rendered.Find("[data-testid=flow-participant][data-agent-id=agent-a]").Change(true);
        rendered.Find("[data-testid=flow-participant][data-agent-id=agent-b]").Change(true);
        await rendered.InvokeAsync(() => wizard.Instance.GoToStepAsync("review"));
        await rendered.InvokeAsync(wizard.Instance.FinishAsync);
        rendered.WaitForAssertion(() => Assert.AreEqual("recoverable-team", handler.Created?.Name));
    }

    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static BunitContext Context(Handler handler, bool noScope = false)
    {
        var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IFlowApiClient>(new FlowApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost/") }));
        context.Services.AddSingleton<IManagementApiClient>(new ManagementStub());
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ScopeStub(noScope));
        return context;
    }
    private sealed class ScopeStub(bool empty) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>(empty ? [] : [new(Owner, ResourceScopeKind.Workspace, "Fixture workspace", true)]);
    }
    private sealed class Handler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public int Writes { get; private set; }
        public CreateFlowRequest? Created { get; private set; }
        public CreateFlowDraftRequest? Draft { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method); Writes++;
            if (request.RequestUri!.AbsolutePath == "/api/flows/drafts")
            {
                Draft = await request.Content!.ReadFromJsonAsync<CreateFlowDraftRequest>(Json, cancellationToken);
                var value = new FlowDraft { WorkspaceId = new(Guid.NewGuid()), Id = "fixture", FlowId = new(Draft!.Name), DisplayName = Draft.DisplayName, ScopeRef = Draft.ScopeRef, Definition = new() { EntryStep = "input", Steps = [], Transitions = [] }, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };
                return new(HttpStatusCode.Created) { Content = JsonContent.Create(new FlowDraftResponse(value, "\"draft-etag\""), options: Json) };
            }
            Assert.AreEqual("/api/flows", request.RequestUri.AbsolutePath);
            Created = await request.Content!.ReadFromJsonAsync<CreateFlowRequest>(Json, cancellationToken);
            var flow = new FlowResponse(Created!.Name, Created.Name, Created.Description, Created.Version, Created.Enabled, null, Created.Definition, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) { ScopeRef = Created.ScopeRef, DisplayName = Created.DisplayName, Namespace = ResourceNamespace.Default, Uid = Guid.NewGuid() };
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(flow, options: Json) }; response.Headers.ETag = new("\"created-etag\""); return response;
        }
    }
    private sealed class ManagementStub : IManagementApiClient
    {
        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentSummary>>([
            new("agent-a", "Agent A", "assistant", "1", "Ready", [], "local", DateTimeOffset.UnixEpoch, "default"),
            new("agent-b", "Agent B", "assistant", "1", "Ready", [], "local", DateTimeOffset.UnixEpoch, "default")]);
        public Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
