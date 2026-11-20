using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using FlowDesignerComponent = Agentstration.Web.FlowDesigner.Components.FlowDesigner;

namespace Agentstration.Web.FlowDesigner.Tests;

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public void DesignerRejectsAForeignTargetBeforeLoadingItsPrivateGraph()
    {
        using var context = CreateContext();
        var foreign = new FlowDesignerResource(new("different-flow", new("different-namespace")), "Foreign private title", "Foreign description",
            new Dictionary<string, string>(), new() { EntryStep = "foreign-step", Steps = [new InputFlowStepDefinition { Name = "foreign-step" }] })
        { ScopeRef = TestScope };
        var backend = new BackendStub(false) { LoadOverride = _ => Task.FromResult(new FlowDesignerLoadResult(foreign, "foreign private YAML", "foreign-etag")) };
        ConfigureSavingContext(context, backend);
        var page = context.Render<FlowDesignerComponent>(p => p.Add(value => value.ResourceId, "sample").Add(value => value.ScopeRef, TestScope));

        Assert.IsNull(context.Services.GetRequiredService<FlowEditorStore>().State.Resource);
        Assert.HasCount(0, page.FindAll(".flow-editor-shell"));
        Assert.DoesNotContain("Foreign private title", page.Markup); Assert.DoesNotContain("foreign private YAML", page.Markup);
        Assert.HasCount(1, page.FindAll(".designer-error"));
    }

    [TestMethod]
    public async Task DesignerSameTargetRerenderRetainsInvalidRawDraftAndSaveSession()
    {
        using var context = CreateContext();
        var backend = new BackendStub(false); ConfigureSavingContext(context, backend);
        var page = context.Render<FlowDesignerComponent>(p => p.Add(value => value.ResourceId, "sample").Add(value => value.ScopeRef, TestScope));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await page.InvokeAsync(() => store.SetSource("entryStep: [", "Invalid draft kept"));
        var sent = store.CaptureSave();
        page.Render(p => p.Add(value => value.ResourceId, "sample").Add(value => value.ScopeRef, TestScope));
        Assert.IsTrue(store.IsCurrent(sent)); Assert.IsTrue(store.State.IsDirty); Assert.IsTrue(store.State.HasSourceChanges);
        Assert.AreEqual("entryStep: [", store.State.SourceText); Assert.AreEqual("Invalid draft kept", store.State.SourceError);
        Assert.AreEqual("sample", store.State.Resource!.FlowId.Value); Assert.AreEqual(TestScope, store.State.Resource.ScopeRef);
        Assert.AreEqual(0, backend.SaveCount);
    }

    [TestMethod]
    public async Task ReadOnlyPristineDraftCanExecuteWithoutWritingAndPreservesExactRunCoordinates()
    {
        using var context = CreateContext();
        var executionWorkspace = WorkspaceId.New();
        var tenant = Guid.NewGuid();
        var logical = ResourceScopeRef.Tenant(tenant);
        var owner = ResourceScopeRef.Instance;
        var backend = new BackendStub(false)
        {
            RunOverride = (target, request) => Task.FromResult(new FlowRun
            {
                Id = "pristine-run",
                WorkspaceId = executionWorkspace,
                FlowId = new(target.ResourceId, target.Namespace),
                FlowVersion = "draft",
                DefinitionState = FlowDefinitionState.Draft,
                DefinitionScopeRef = owner,
                Scope = new(tenant, executionWorkspace, Guid.NewGuid()) { LogicalScopeRef = logical },
                Input = request.Input.Clone(),
                CreatedAt = DateTimeOffset.UnixEpoch,
                DefinitionSnapshot = new(owner, new(target.ResourceId, target.Namespace), "draft", null,
                    new WorkflowFlowDefinition("input", [], []), new Dictionary<string, string>(), DateTimeOffset.UnixEpoch)
            })
        };
        ConfigureSavingContext(context, backend);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/flows/sample/designer?context=" + Uri.EscapeDataString(logical.Value));
        var page = context.Render<FlowDesignerComponent>(p => p.Add(value => value.ResourceId, "sample")
            .Add(value => value.ScopeRef, owner).Add(value => value.IsReadOnly, true).Add(value => value.ExecutionAllowed, true));
        var strings = context.Services.GetRequiredService<IStringLocalizer<Agentstration.Web.FlowDesigner.Components.FlowDesignerStrings>>();
        Assert.HasCount(0, page.FindAll("[data-testid=flow-designer-save]"));
        page.FindAll(".designer-actions button").Single(button => button.TextContent == strings["RunDraft"].Value).Click();
        await page.Find("[data-testid=flow-designer-run-submit]").ClickAsync();

        Assert.AreEqual(1, backend.RunCount); Assert.AreEqual(0, backend.SaveCount); Assert.AreEqual(0, backend.ValidationCount);
        Assert.AreEqual(owner, backend.LastRunTarget!.ScopeRef);
        var destination = new Uri(navigation.Uri); var query = QueryHelpers.ParseQuery(destination.Query);
        Assert.AreEqual("/flow-runs/pristine-run", destination.AbsolutePath);
        Assert.AreEqual(ResourceScopeRef.Workspace(executionWorkspace.Value).Value, query["scopeRef"].ToString());
        Assert.AreEqual(logical.Value, query["context"].ToString());
        Assert.IsFalse(context.Services.GetRequiredService<FlowEditorStore>().State.IsDirty);
    }

    [TestMethod]
    public async Task ReadOnlyDraftExecutionDoesNotSaveOrRunPendingGraphAndRawChanges()
    {
        using var context = CreateContext();
        var backend = new BackendStub(false); ConfigureSavingContext(context, backend);
        var page = context.Render<FlowDesignerComponent>(p => p.Add(value => value.ResourceId, "sample").Add(value => value.ExecutionAllowed, true));
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        await page.InvokeAsync(() => store.DispatchAsync(new MoveStepCommand("input", new(31, 42))));
        await page.InvokeAsync(() => store.SetSource("entryStep: [", "Retained incomplete source"));
        page.Render(p => p.Add(value => value.IsReadOnly, true));
        await page.InvokeAsync(() => InvokeDesignerAsync(page.Instance, "RunDraftAsync"));
        Assert.AreEqual(0, backend.RunCount); Assert.AreEqual(0, backend.SaveCount);
        Assert.IsTrue(store.State.IsDirty); Assert.AreEqual("entryStep: [", store.State.SourceText);
        Assert.AreEqual(new FlowNodePosition(31, 42), store.State.Resource!.Definition.Designer.NodePositions["input"]);
    }

    [TestMethod]
    public async Task MissingExecutionCapabilityDoesNotPreventIndependentDefinitionEditing()
    {
        using var context = CreateContext();
        var backend = new BackendStub(false); ConfigureSavingContext(context, backend);
        var page = context.Render<FlowDesignerComponent>(p => p.Add(value => value.ResourceId, "sample").Add(value => value.ExecutionAllowed, false));
        var strings = context.Services.GetRequiredService<IStringLocalizer<Agentstration.Web.FlowDesigner.Components.FlowDesignerStrings>>();
        Assert.IsFalse(page.FindAll(".designer-actions button").Any(button => button.TextContent == strings["RunDraft"].Value));
        Assert.HasCount(1, page.FindAll("[data-testid=flow-designer-save]"));
        await page.InvokeAsync(() => InvokeDesignerAsync(page.Instance, "RunDraftAsync"));
        Assert.AreEqual(0, backend.RunCount); Assert.AreEqual(0, backend.SaveCount);
    }
}
