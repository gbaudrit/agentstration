using System.Net;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class EntryAdministrationComponentTests
{
    [TestMethod]
    public async Task EntryUsesItsActualWorkContractWithoutFabricatingCoreOwnerPolicyOrLabels()
    {
        using var context = CreateContext(); context.Services.AddSingleton<IEntryAdministrationApiClient>(new FakeEntryAdministrationApiClient());
        var page = await RenderEntry(context, p => p.Add(c => c.Name, "primary"));
        var layout = page.FindComponent<ResourceDetailsTemplate>().Instance;
        Assert.IsEmpty(page.FindComponents<ConsoleResourceDetailsTemplate>(), "Work's Entry has no Core Resource envelope/UID/policy contract.");
        Assert.IsNull(layout.ScopeRef);
        Assert.IsNull(layout.IdentityLabels);
        Assert.IsNull(layout.AccessContent);
        Assert.IsEmpty(page.FindAll(".resource-header .resource-owner-scope,.resource-identity-summary .resource-owner-scope"));
        Assert.Contains("Personal", page.Find(".resource-identity-summary").TextContent);
        await SelectEntry(page, "definition/identity");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(layout.SectionState, form.SectionState);
        Assert.AreSame(layout.SectionPresentations, form.Sections);
        Assert.IsInstanceOfType<EntryEditorModel>(form.EditContext.Model);
        Assert.IsTrue(form.Sections.Values.SelectMany(value => value.Fields).All(field => form.EditContext.Model.GetType().GetProperty(field) is not null));
        Assert.AreEqual("yaml", page.FindComponent<ResourceNavigation>().Instance.Items.Last().Path);
    }

    [TestMethod]
    public async Task EntryRefreshRetainsInvalidDefinitionAndExactYamlUntilAnExplicitDiscard()
    {
        using var context = CreateContext(); var client = new FakeEntryAdministrationApiClient(); context.Services.AddSingleton<IEntryAdministrationApiClient>(client);
        var page = await RenderEntry(context, p => p.Add(c => c.Name, "primary"));
        await SelectEntry(page, "definition/identity"); page.Find("[data-testid=entry-display-name]").Input("");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await SelectEntry(page, "yaml"); page.Find(".resource-yaml-editor textarea").Input("definition: [incomplete");
        var incoming = FakeEntryAdministrationApiClient.EntryDraftResponse("primary"); client.EntryOverride = incoming with { Value = incoming.Value with { DisplayName = "New server Entry", Revision = 8 } };
        await page.InvokeAsync(() => page.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("definition: [incomplete", page.Find(".resource-yaml-editor textarea").GetAttribute("value"));
        Assert.AreEqual("", page.Find("[data-testid=entry-display-name]").GetAttribute("value"));
        Assert.Contains("Prepare a report", page.Find(".resource-header").TextContent);
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await page.Find(".unsaved-changes-bar .button-secondary").ClickAsync();
        Assert.Contains("New server Entry", page.Find(".resource-header").TextContent);
        Assert.IsFalse(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.Contains("New server Entry", page.Find(".resource-yaml-editor textarea").GetAttribute("value")!);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EntryReadLossOrForeignWorkspacePurgesPrivateDrafts(bool forbidden)
    {
        using var context = CreateContext(); var client = new FakeEntryAdministrationApiClient(); context.Services.AddSingleton<IEntryAdministrationApiClient>(client);
        var page = await RenderEntry(context, p => p.Add(c => c.Name, "primary"));
        await SelectEntry(page, "definition/identity"); page.Find("[data-testid=entry-display-name]").Input("Private Entry input");
        if (forbidden) client.ReadError = new AgentstrationApiException("No Entry access", "fixture-entry-denied", HttpStatusCode.Forbidden);
        else { var incoming = FakeEntryAdministrationApiClient.EntryDraftResponse("primary"); client.EntryOverride = incoming with { Value = incoming.Value with { WorkspaceId = new(Guid.NewGuid()) } }; }
        await page.InvokeAsync(() => page.FindComponent<ResourceDetailsTemplate>().Instance.OnRefresh.InvokeAsync());
        Assert.IsEmpty(page.FindComponents<ResourceDefinitionForm>());
        Assert.DoesNotContain("Private Entry input", page.Markup); Assert.DoesNotContain("Prepare a report", page.Markup);
        Assert.AreEqual(forbidden ? ResourceLoadStatus.Forbidden : ResourceLoadStatus.NotFound, page.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
    }

    [TestMethod]
    public async Task EntryRejectedMetadataSaveRetainsTheRelativeBindingAndTheSameInvalidatableForm()
    {
        using var context = CreateContext(); var client = new FakeEntryAdministrationApiClient { SaveError = new InvalidOperationException("Entry write rejected") }; context.Services.AddSingleton<IEntryAdministrationApiClient>(client);
        var page = await RenderEntry(context, p => p.Add(c => c.Name, "primary"));
        await SelectEntry(page, "definition/identity"); page.Find("[data-testid=entry-display-name]").Input("Pending Entry name");
        var form = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        await page.Find("#entry-resource-form").SubmitAsync();
        Assert.AreSame(form, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Pending Entry name", page.Find("[data-testid=entry-display-name]").GetAttribute("value"));
        Assert.Contains("Entry write rejected", page.Find("[data-testid=entry-message]").TextContent);
        Assert.IsNotNull(client.SavedEntry); Assert.IsNull(client.SavedEntry.Binding.ScopeRef); Assert.IsNull(client.SavedEntry.Binding.Namespace);
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    [TestMethod]
    public void EntryModelPreservesUnchangedColonCommaOptionsValidationDefaultsAndConversationTarget()
    {
        var saved = FakeEntryAdministrationApiClient.EntryDraftResponse("primary").Value;
        var target = new EntryResolvedTarget("continuation", "2.3.4") { Namespace = ResourceNamespace.Parse("other"), ScopeRef = ResourceScopeRef.Instance };
        var field = saved.Presentation.Fields[0] with { Type = EntryFieldType.Choice, Options = [new("a,b", "Label: with, commas")], DefaultValue = JsonSerializer.SerializeToElement("a,b"), Validation = new(2, 19), Order = 12 };
        saved = saved with { Presentation = saved.Presentation with { Fields = [field] }, Behavior = new(Conversation: new(false, target)) };
        var model = EntryEditorModel.From(saved, "default:router"); model.Description = "Only metadata changed";
        var draft = model.Draft(saved.Binding, saved.Revision, saved.PublishedBinding);
        CollectionAssert.AreEqual(field.Options.ToArray(), draft.Presentation.Fields[0].Options.ToArray());
        Assert.AreEqual(field.Validation, draft.Presentation.Fields[0].Validation); Assert.AreEqual(field.Order, draft.Presentation.Fields[0].Order);
        Assert.IsTrue(JsonElement.DeepEquals(field.DefaultValue!.Value, draft.Presentation.Fields[0].DefaultValue!.Value));
        Assert.AreEqual(saved.Behavior.Conversation, draft.Behavior.Conversation);
        Assert.AreEqual(saved.Binding, draft.Binding);
    }
    [TestMethod]
    public async Task EntryWritePermissionChangesImmediatelyUpdateReadOnlyPresentationWithoutReplacingItsDraft()
    {
        using var context = CreateContext();
        var client = new FakeEntryAdministrationApiClient();
        var provider = new MutableEntryContextProvider { Snapshot = NewToolDefinitionTests.SnapshotFor(ResourceScopeRef.Workspace(FakeEntryAdministrationApiClient.WorkspaceResourceId)) };
        var state = new ConsoleContextState(provider); await state.EnsureLoadedAsync(CancellationToken.None);
        context.Services.AddSingleton<IEntryAdministrationApiClient>(client); context.Services.AddSingleton(state); context.Services.AddSingleton<IConsoleNavigationContext>(provider);
        var page = await RenderEntry(context, p => p.Add(c => c.Name, "primary"));
        await SelectEntry(page, "definition/identity"); page.Find("[data-testid=entry-display-name]").Input("Pending permission change");
        var editContext = page.FindComponent<ResourceDefinitionForm>().Instance.EditContext;
        provider.Snapshot = provider.Snapshot with { Permissions = new HashSet<string> { "resources/read" } };
        await page.InvokeAsync(() => state.LoadAsync(CancellationToken.None));
        page.WaitForAssertion(() => Assert.AreEqual(ResourceReadOnlyReason.InsufficientPermissions, page.FindComponent<ResourceReadOnlyNotice>().Instance.Reason));
        Assert.AreSame(editContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.IsTrue(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly);
        Assert.IsTrue(page.Find("#entry-resource-form > fieldset").HasAttribute("disabled"));
        Assert.AreEqual("Pending permission change", page.Find("[data-testid=entry-display-name]").GetAttribute("value"));
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await page.Find("#entry-resource-form").SubmitAsync(); Assert.IsNull(client.SavedEntry);
        provider.Snapshot = provider.Snapshot with { Permissions = new HashSet<string> { "resources/read", "resources/write" } };
        await page.InvokeAsync(() => state.LoadAsync(CancellationToken.None));
        page.WaitForAssertion(() => Assert.IsFalse(page.FindComponent<ResourceDefinitionForm>().Instance.ReadOnly));
        Assert.AreSame(editContext, page.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("Pending permission change", page.Find("[data-testid=entry-display-name]").GetAttribute("value"));
        provider.Snapshot = provider.Snapshot with { Permissions = new HashSet<string>() };
        await page.InvokeAsync(() => state.LoadAsync(CancellationToken.None));
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindComponents<ResourceDefinitionForm>()));
        Assert.DoesNotContain("Pending permission change", page.Markup);
        Assert.AreEqual(ResourceLoadStatus.Forbidden, page.FindComponent<ResourceDetailsTemplate>().Instance.LoadState!.Status);
    }
    private sealed class MutableEntryContextProvider : IConsoleContextProvider, IConsoleNavigationContext
    {
        public required ConsoleContextSnapshot Snapshot { get; set; }
        public string ScopeRef => Snapshot.SelectedScopeRef;
        public string AddToUri(string uri) => uri;
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }

}
