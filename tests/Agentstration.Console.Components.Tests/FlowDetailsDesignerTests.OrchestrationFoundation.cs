using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Triggers;
using Agentstration.Web.Components.Models;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class FlowDetailsDesignerTests
{
    [TestMethod]
    public async Task OrchestrationSharesTheLayoutsActualStateDeclarationsAndRetainsInvalidConfiguration()
    {
        using var context = FlowContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FlowClientStub(orchestration: true);
        context.Services.AddSingleton<IFlowApiClient>(client);
        context.Services.AddSingleton<IManagementApiClient>(new ManagementClientStub());
        AddConsoleContext(context);
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var state = new ResourceSectionState();
        state.Select("definition/identity");
        var declarations = FlowOrchestrationEditor.CreateSectionPresentations(false);
        var page = context.Render<FlowOrchestrationEditor>(p => p.Add(c => c.FlowId, "review").Add(c => c.Embedded, true)
            .Add(c => c.SectionState, state).Add(c => c.SectionPresentations, declarations));
        var form = page.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(state, form.SectionState);
        Assert.AreSame(declarations, form.Sections);
        Assert.HasCount(1, page.FindAll("[data-testid='flow-orchestration-identity'] [data-testid='labels-editor']"));
        Assert.HasCount(0, page.FindAll("[data-testid='label-add']"));
        page.Find("[data-testid='flow-definition-display-name']").Input("Pending identity");
        state.Select("definition/configuration");
        page.Render();
        page.Find("[data-testid='flow-definition-version']").Input("");
        state.Select("definition/identity");
        page.Render();
        Assert.AreEqual("Pending identity", page.Find("[data-testid='flow-definition-display-name']").GetAttribute("value"));
        Assert.AreEqual(string.Empty, page.Find("[data-testid='flow-definition-version']").GetAttribute("value"));
        Assert.AreSame(form.EditContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsTrue(page.Instance.HasUnsavedChanges);
        Assert.IsNull(client.LastDefinitionUpdate);
    }

    [TestMethod]
    public async Task OrchestrationActivationAcknowledgementRetainsIdentityAndIndependentPublicationDraft()
    {
        using var context = FlowContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FlowClientStub(orchestration: true);
        context.Services.AddSingleton<IFlowApiClient>(client);
        context.Services.AddSingleton<IManagementApiClient>(new ManagementClientStub());
        AddConsoleContext(context);
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var original = await client.GetFlowSnapshotAsync("review", CancellationToken.None);
        var page = context.Render<FlowOrchestrationEditor>(p => p.Add(c => c.FlowId, "review").Add(c => c.Embedded, true));
        page.Find("[data-testid='flow-definition-display-name']").Input("Keep edited title");
        page.Find("[data-testid='flow-publish-version']").Input("9.0.0");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await page.InvokeAsync(() => page.Instance.ApplyActivationSnapshot(new(original.Value with { Enabled = false }, "\"enabled-ack\"")));
        Assert.AreEqual("Keep edited title", page.Find("[data-testid='flow-definition-display-name']").GetAttribute("value"));
        Assert.AreEqual("9.0.0", page.Find("[data-testid='flow-publish-version']").GetAttribute("value"));
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsTrue(page.Instance.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task OrchestrationExplicitSnapshotAdoptionReplacesOnlyAfterCallerRequestsDiscard()
    {
        using var context = FlowContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FlowClientStub(orchestration: true);
        context.Services.AddSingleton<IFlowApiClient>(client);
        context.Services.AddSingleton<IManagementApiClient>(new ManagementClientStub());
        AddConsoleContext(context);
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var original = await client.GetFlowSnapshotAsync("review", CancellationToken.None);
        var page = context.Render<FlowOrchestrationEditor>(p => p.Add(c => c.FlowId, "review").Add(c => c.Embedded, true));
        page.Find("[data-testid='flow-definition-display-name']").Input("Local unsaved");
        var newer = new FlowResourceSnapshot(original.Value with { DisplayName = "New saved title" }, "\"newer\"");
        page.Render(p => p.Add(c => c.InitialSnapshot, newer));
        Assert.AreEqual("Local unsaved", page.Find("[data-testid='flow-definition-display-name']").GetAttribute("value"));
        await page.InvokeAsync(() => page.Instance.ApplySavedSnapshotAsync(newer));
        Assert.AreEqual("New saved title", page.Find("[data-testid='flow-definition-display-name']").GetAttribute("value"));
        Assert.IsFalse(page.Instance.HasUnsavedChanges);
    }

    [TestMethod]
    public async Task OrchestrationCorrectedParticipantSelectionCanBeSavedAfterSpecializedValidationFails()
    {
        using var context = FlowContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var client = new FlowClientStub(orchestration: true);
        context.Services.AddSingleton<IFlowApiClient>(client);
        context.Services.AddSingleton<IManagementApiClient>(new OrchestrationCatalogManagementClient(
        [
            new("agent-a", "Agent A", "assistant", "1", "Ready", [], "local", FlowClientStub.Now) { ScopeRef = client.ScopeRef },
            new("agent-b", "Agent B", "assistant", "1", "Ready", [], "local", FlowClientStub.Now) { ScopeRef = client.ScopeRef }
        ]));
        AddConsoleContext(context);
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(CancellationToken.None);
        var page = context.Render<FlowOrchestrationEditor>(p => p.Add(c => c.FlowId, "review").Add(c => c.Embedded, true));
        await page.FindAll(".order-actions .button-danger").Last().ClickAsync(new());
        await page.Find("[data-testid='flow-orchestration-editor']").SubmitAsync();
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        Assert.IsNotEmpty(form.GetValidationMessages(form.Field(nameof(OrchestrationEditorModel.ParticipantIds))));
        Assert.IsNull(client.LastDefinitionUpdate);
        await page.Find("[data-testid='flow-participant'][data-agent-id='agent-b']").ChangeAsync(true);
        await page.Find("[data-testid='flow-orchestration-editor']").SubmitAsync();
        Assert.IsNotNull(client.LastDefinitionUpdate);
        var definition = (OrchestrationFlowDefinition)client.LastDefinitionUpdate.Definition;
        CollectionAssert.AreEqual(new[] { "agent-a", "agent-b" }, definition.Participants.Select(value => value.Id).ToArray());
        Assert.AreEqual(client.ScopeRef, definition.Participants[1].ScopeRef);
        Assert.IsEmpty(page.FindComponent<ResourceDefinitionForm>().Instance.EditContext.GetValidationMessages());
    }
    private sealed class OrchestrationCatalogManagementClient(IReadOnlyList<AgentSummary> agents) : IManagementApiClient
    {
        private readonly IManagementApiClient fallback = new ManagementClientStub();
        public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) => Task.FromResult(agents);
        public Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsAsync(CancellationToken cancellationToken) => fallback.GetDeploymentsAsync(cancellationToken);
        public Task<IReadOnlyList<TriggerResource>> GetTriggersAsync(CancellationToken cancellationToken) => fallback.GetTriggersAsync(cancellationToken);
        public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string name, CancellationToken cancellationToken) => fallback.GetAgentAsync(name, cancellationToken);
        public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken) => fallback.PutAgentAsync(request, etag, createOnly, cancellationToken);
        public Task DeleteAgentAsync(string name, string etag, CancellationToken cancellationToken) => fallback.DeleteAgentAsync(name, etag, cancellationToken);
        public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => fallback.GetSummaryAsync(cancellationToken);
    }
}
