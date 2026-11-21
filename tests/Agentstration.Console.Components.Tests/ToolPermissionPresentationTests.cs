using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ToolDefinitionConsoleTests
{
    [TestMethod]
    public async Task OwnerReaderCanInspectToolDefinitionButCannotEditActivateOrDeleteIt()
    {
        var provider = new MutableDefinitionPermissions { Snapshot = NewToolDefinitionTests.SnapshotFor(SavedDefinition().ScopeRef!.Value) with { Permissions = new HashSet<string> { "resources/read" } } };
        var state = new ConsoleContextState(provider); await state.EnsureLoadedAsync(CancellationToken.None);
        var client = new DefinitionClient { Resource = SavedDefinition() }; using var context = SavedContext(client, state);
        var page = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, client.Resource.Name));
        await Select(page, "definition/identity");
        Assert.AreEqual(ResourceReadOnlyReason.InsufficientPermissions, page.FindComponent<ResourceReadOnlyNotice>().Instance.Reason);
        Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        Assert.IsTrue(page.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
        Assert.IsEmpty(page.FindAll(".resource-header button.button-danger,.unsaved-changes-bar"));
        Assert.IsTrue(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.AllowAccess, "Definition write permission must not gate the separately authorized policy adapter.");
        await page.Find("#tool-definition-resource-form").SubmitAsync();
        await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.EnabledChanged.InvokeAsync(false));
        Assert.IsNull(client.Updated); Assert.IsNull(client.LastEtag);
        await Select(page, "versions"); Assert.AreEqual(1, client.HistoryReads);
        await Select(page, "yaml"); Assert.IsTrue(page.FindComponent<ResourceYamlEditor>().Instance.ReadOnly);
    }

    [TestMethod]
    public async Task ToolPermissionChangesRetainTheDefinitionDraftThenPurgeItImmediatelyOnReadLoss()
    {
        var provider = new MutableDefinitionPermissions { Snapshot = NewToolDefinitionTests.SnapshotFor(SavedDefinition().ScopeRef!.Value) };
        var state = new ConsoleContextState(provider); await state.EnsureLoadedAsync(CancellationToken.None);
        var client = new DefinitionClient { Resource = SavedDefinition() }; using var context = SavedContext(client, state);
        var page = context.Render<ToolDefinitionEditor>(p => p.Add(c => c.Name, client.Resource.Name));
        await Select(page, "definition/identity"); page.FindComponent<ResourceIdentityFields>().Find("input:not([disabled])").Input("Private pending Tool name");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        provider.Snapshot = provider.Snapshot with { Permissions = new HashSet<string> { "resources/read" } };
        await page.InvokeAsync(() => state.LoadAsync(CancellationToken.None));
        page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly));
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Private pending Tool name", page.FindComponent<ResourceIdentityFields>().Find("input").GetAttribute("value"));
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await page.Find("#tool-definition-resource-form").SubmitAsync(); Assert.IsNull(client.Updated);
        provider.Snapshot = provider.Snapshot with { Permissions = new HashSet<string>() };
        await page.InvokeAsync(() => state.LoadAsync(CancellationToken.None));
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindComponents<ResourceDefinitionForm>()));
        Assert.DoesNotContain("Private pending Tool name", page.Markup);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.LoadState!.Status);
    }
    private sealed class MutableDefinitionPermissions : IConsoleContextProvider
    {
        public required ConsoleContextSnapshot Snapshot { get; set; }
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }
}

public sealed partial class ToolRelayEditorTests
{
    [TestMethod]
    public async Task OwnerReaderInspectsRelayPublicationsButCannotEditActivateOrPublish()
    {
        var provider = new MutableRelayPermissions { Snapshot = NewToolDefinitionTests.SnapshotFor(Scope) with { Permissions = new HashSet<string> { "resources/read" } } };
        var state = new ConsoleContextState(provider); await state.EnsureLoadedAsync(CancellationToken.None);
        using var handler = new ApiHandler(); using var context = Context(handler, state: state);
        var page = await RenderRelay(context, p => p.Add(c => c.Name, "relay"));
        Assert.AreEqual(ResourceReadOnlyReason.InsufficientPermissions, page.FindComponent<ResourceReadOnlyNotice>().Instance.Reason);
        Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        Assert.IsTrue(page.Find(".resource-header input[role=switch]").HasAttribute("disabled"));
        Assert.IsEmpty(page.FindAll("[data-testid=relay-publish-review],[data-testid=relay-save-draft]"));
        await page.Find("#relay-resource-form").SubmitAsync();
        await page.InvokeAsync(() => page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.EnabledChanged.InvokeAsync(false));
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("versions"));
        Assert.Contains("1.2.0", page.Find("[data-relay-section=versions]").TextContent);
        Assert.IsTrue(page.FindComponent<ConsoleResourceDetailsTemplate>().Instance.AllowAccess);
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        Assert.Contains("kind: ToolDefinition", page.FindComponent<ResourceYamlEditor>().Instance.Value);
        Assert.Contains("relay", page.FindComponent<ResourceYamlEditor>().Instance.Value);
        Assert.IsTrue(page.FindComponent<ResourceYamlEditor>().Instance.ReadOnly);
        Assert.IsEmpty(handler.Writes); Assert.AreEqual(0, handler.Publications);
    }

    [TestMethod]
    public async Task RelayPermissionChangesRetainInvalidBindingsThenPurgeThemImmediatelyOnReadLoss()
    {
        var provider = new MutableRelayPermissions { Snapshot = NewToolDefinitionTests.SnapshotFor(Scope) };
        var state = new ConsoleContextState(provider); await state.EnsureLoadedAsync(CancellationToken.None);
        using var handler = new ApiHandler(); using var context = Context(handler, state: state);
        var page = await RenderRelay(context, p => p.Add(c => c.Name, "relay")); EnterLiteral(page, "/store", "{private-incomplete");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        provider.Snapshot = provider.Snapshot with { Permissions = new HashSet<string> { "resources/read" } };
        await page.InvokeAsync(() => state.LoadAsync(CancellationToken.None));
        page.WaitForAssertion(() => Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly));
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("{private-incomplete", page.Find("[data-binding-path='/store'] textarea").GetAttribute("value"));
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await page.Find("#relay-resource-form").SubmitAsync(); Assert.IsEmpty(handler.Writes);
        provider.Snapshot = provider.Snapshot with { Permissions = new HashSet<string>() };
        await page.InvokeAsync(() => state.LoadAsync(CancellationToken.None));
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindComponents<ResourceDefinitionForm>()));
        Assert.DoesNotContain("{private-incomplete", page.Markup);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, page.FindComponent<ResourceLoadFeedback>().Instance.State.Status);
    }
    private sealed class MutableRelayPermissions : IConsoleContextProvider
    {
        public required ConsoleContextSnapshot Snapshot { get; set; }
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }
}
