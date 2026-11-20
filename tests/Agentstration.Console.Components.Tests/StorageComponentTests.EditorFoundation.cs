using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class StorageComponentTests
{
    [TestMethod]
    public async Task StorageMetadataFormSharesSectionsAndRetainsInvalidIdentityAndEtagUntilDiscard()
    {
        var client = new StorageClient(); using var context = Setup(client); Navigate(context, Resource(), edit: true);
        var state = new ResourceSectionState(); state.Select("definition/identity"); var declarations = StorageEditor.DefinitionPresentations();
        var cut = context.Render<StorageEditor>(p => p.Add(c => c.Name, "documents").Add(c => c.Embedded, true).Add(c => c.SectionState, state).Add(c => c.SectionPresentations, declarations).Add(c => c.SectionRequested, path => state.Select(path)));
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(state, form.SectionState); Assert.AreSame(declarations, form.Sections);
        Assert.IsTrue(cut.FindComponent<ResourceIdentityEditor>().Instance.LabelsReadOnly);
        cut.Find("[data-testid=storage-display-name]").Change(""); cut.Find("form").Submit();
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any()); Assert.IsNull(client.Updated);
        var newer = Resource() with { Generation = 5, Definition = Resource().Definition with { DisplayName = "New saved Storage" } };
        await cut.InvokeAsync(() => cut.Instance.ApplySnapshot(new(newer, "new-etag")));
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("", cut.Find("[data-testid=storage-display-name]").GetAttribute("value"));
        Assert.AreEqual("\"revision-4\"", cut.Find("[data-testid=storage-form]").GetAttribute("data-resource-etag"));
        await cut.InvokeAsync(() => cut.Instance.ApplySnapshot(new(newer, "new-etag"), discard: true));
        Assert.AreEqual("New saved Storage", cut.Find("[data-testid=storage-display-name]").GetAttribute("value"));
        Assert.AreEqual("new-etag", cut.Find("[data-testid=storage-form]").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task StorageInvalidRawConfigurationRoutesToItsSectionAndSurvivesAnIncomingSnapshot(string locale)
    {
        using var culture = new TestCultureScope(locale);
        var client = new StorageClient(); using var context = Setup(client); Navigate(context, Resource(), edit: true);
        var state = new ResourceSectionState(); state.Select("definition/configuration"); var declarations = StorageEditor.DefinitionPresentations();
        var cut = context.Render<StorageEditor>(p => p.Add(c => c.Name, "documents").Add(c => c.Embedded, true).Add(c => c.SectionState, state).Add(c => c.SectionPresentations, declarations).Add(c => c.SectionRequested, path => state.Select(path)));
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        cut.Find("[data-testid=storage-configuration] [data-testid=schema-raw-mode]").Click();
        cut.Find("[data-testid=storage-configuration] textarea").Change("{invalid-retained-configuration");
        await cut.InvokeAsync(() => { state.Select("definition/identity"); cut.Render(); });
        cut.Find("form").Submit();
        Assert.AreEqual("definition/configuration", state.CurrentPath);
        Assert.IsTrue(form.EditContext.GetValidationMessages(form.EditContext.Field(nameof(StorageEditorModel.Configuration))).Any());
        Assert.IsTrue(cut.Instance.IsDirty); Assert.IsNull(client.Updated);
        await cut.InvokeAsync(() => cut.Instance.ApplySnapshot(new(Resource() with { Generation = 9 }, "new-etag")));
        Assert.AreEqual("{invalid-retained-configuration", cut.Find("[data-testid=storage-configuration] textarea").GetAttribute("value"));
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("\"revision-4\"", cut.Find("[data-testid=storage-form]").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StorageReadOnlyFormKeepsIdentityAndConfigurationButCannotSubmit(bool inherited)
    {
        var client = new StorageClient(); using var context = Setup(client, canWrite: inherited, selectedScope: inherited ? ResourceScopeRef.Instance.Value : Scope.Value);
        Navigate(context, Resource(), edit: true);
        var cut = context.Render<StorageEditor>(p => p.Add(c => c.Name, "documents").Add(c => c.Embedded, true));
        Assert.IsTrue(cut.Find("[data-testid=storage-definition-fields]").HasAttribute("disabled"));
        Assert.HasCount(1, cut.FindAll("[data-testid=storage-display-name]"));
        Assert.HasCount(1, cut.FindAll("[data-testid=storage-configuration]"));
        cut.Find("form").Submit(); Assert.IsNull(client.Updated); Assert.AreEqual(0, client.Reconciles);
        Assert.IsEmpty(cut.FindAll("[data-testid=storage-save]"));
    }

    [TestMethod]
    public void StorageCreationKeepsTheQualifiedNamespaceAndCurrentOwnerWithoutOfferingAnUnsupportedMetadataWrite()
    {
        var client = new StorageClient(); using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/storages/new?namespace=creation-namespace");
        var cut = context.Render<StorageEditor>();
        var identity = cut.FindComponent<ResourceIdentityEditor>().Instance;
        Assert.AreEqual("creation-namespace", identity.Namespace); Assert.IsFalse(identity.NamespaceEditable); Assert.IsTrue(identity.LabelsReadOnly);
        SelectType(cut, "local"); cut.Find("[data-testid=storage-display-name]").Change("Qualified creation"); cut.Find("[data-testid=storage-name]").Change("qualified-creation"); cut.Find("form").Submit();
        Assert.IsNotNull(client.Created); Assert.AreEqual(ResourceNamespace.Parse("creation-namespace"), client.Created.Namespace); Assert.AreEqual(Scope, client.Created.ScopeRef);
        Assert.AreEqual("local", client.Created.Properties.Type.Type.Name);
    }
}
