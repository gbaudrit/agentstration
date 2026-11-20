using Agentstration.Flows;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.State;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using FlowDesignerComponent = Agentstration.Web.FlowDesigner.Components.FlowDesigner;
using FlowDesignerPage = Agentstration.Web.Components.Pages.FlowDesigner;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowDesignerRouteFoundationTests
{
    [TestMethod]
    public async Task FlowDesignerRouteGuardsRawDraftsAndKeepsTheEditorAcrossFragments()
    {
        using var context = new BunitContext().WithCollectionPresentation(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var workspace = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var snapshot = new ConsoleContextSnapshot(Guid.NewGuid(), "Fixture", Guid.NewGuid(), "fixture", "Fixture", workspace, "default", "Default",
            new HashSet<string>(StringComparer.Ordinal) { "resources/read", "resources/write" }, []);
        context.Services.AddSingleton<IConsoleContextProvider>(new ContextProvider(snapshot)); context.Services.AddSingleton<ConsoleContextState>();
        context.Services.AddSingleton<FlowEditorStore>(); context.ComponentFactories.AddStub<FlowDesignerComponent>();
        var navigation = context.Services.GetRequiredService<NavigationManager>(); navigation.NavigateTo("/flows/sample/designer");
        var page = context.Render<FlowDesignerPage>(p => p.Add(value => value.FlowId, "sample"));
        var editor = page.FindComponent<Stub<FlowDesignerComponent>>().Instance;
        var store = context.Services.GetRequiredService<FlowEditorStore>();
        var owner = ResourceScopeRef.Workspace(workspace);
        await page.InvokeAsync(() =>
        {
            store.Load(new FlowDesignerLoadResult(new(new("sample"), "Saved Flow", null, new Dictionary<string, string>(), new() { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }] }) { ScopeRef = owner }, "saved YAML", "etag"), new FlowDesignerTarget(ResourceNamespace.Default, "sample", owner));
            store.SetSource("entryStep: [", "Invalid raw draft");
        });
        page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!()));
        await page.InvokeAsync(() => navigation.NavigateTo("/flows/sample/designer#source"));
        Assert.AreSame(editor, page.FindComponent<Stub<FlowDesignerComponent>>().Instance);
        Assert.AreEqual("entryStep: [", store.State.SourceText);
        await page.InvokeAsync(() => navigation.NavigateTo("/flows/different"));
        page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<ConfirmationDialog>().Instance.IsOpen));
        Assert.AreEqual("entryStep: [", store.State.SourceText);
    }

    private sealed class ContextProvider(ConsoleContextSnapshot snapshot) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
